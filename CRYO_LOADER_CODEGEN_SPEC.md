# CRYO-LOADER: Полная Техническая Спецификация для Кодогенерации
## Версия 1.3 — Production-Ready

**Среда:** Minecraft 1.21.1, NeoForge, Java 21 (Generational ZGC)  
**Цель:** Ускорить холодный старт тяжёлых сборок (ATM10, 400+ модов) с ~180 с до ~80–100 с

---

## КОНТЕКСТ: ЧТО И ПОЧЕМУ ТОРМОЗИТ

Текущий конвейер запуска ATM10 строго последователен:

```
Фаза                     | Примерное время | Статус в этом проекте
-------------------------|-----------------|----------------------
JVM Start + ModLauncher  | ~8 с            | Частично — AppCDS
Mixin Application (ASM)  | ~15 с           | Слой 4 (бонус)
Mod Construction         | ~20 с           | Частично — AppCDS
FMLCommonSetupEvent      | ~10 с           | Не трогаем
Registry Freeze          | ~10 с           | Не трогаем
DataPack Load            | ~30 с           | Слой 2 — параллелизация
Model Baking             | ~45 с           | Слой 3 — параллелизация
Atlas Stitching          | ~20 с           | Частично под Слоем 3
```

**Итого baseline: ~180 с на ATM10**

---

## АРХИТЕКТУРНЫЕ РЕШЕНИЯ, КОТОРЫЕ БЫЛИ ОТВЕРГНУТЫ (не повторять)

### ❌ Сырая сериализация объектов (Kryo + Unsafe)
Невозможно. Причины:
- Lambda-объекты имеют синтетические классы (`Lambda$42/0x...`), генерируемые динамически — при следующем запуске этого класса не существует
- `BlockStateDefinition` содержит ссылки на `Property<?>` объекты, которые верифицируются по identity, а не equals
- ZGC хранит GC-метаданные в старших битах 64-битных указателей — после перезапуска JVM все Unsafe-указатели невалидны
- OpenGL/OpenAL дескрипторы (int handles) привязаны к конкретной сессии GPU-драйвера
- Подписки на EventBus (@SubscribeEvent) регистрируются в конструкторе @Mod-класса — без его выполнения шина событий слепа

### ❌ Параллельное конструирование объектов модов
Невозможно без полного переписывания NeoForge. Причины:
- ClassLoader имеет `synchronized` методы — при параллельной загрузке взаимозависимых классов гарантирован deadlock
- Межмодовые зависимости (EnderIO → Thermal → CoFHCore) не декларированы на уровне объектов
- `DeferredHolder.get()` вернёт null если вызван до `bind()` из другого потока

### ❌ Перехват DeferredRegister.register()
Неправильная точка. `DeferredRegister.register()` уже является ленивым — он только складывает Supplier в LinkedHashMap, не вычисляет объект. Реальная работа происходит позже в обработчике RegisterEvent.

---

## КОМПОНЕНТ 1: Java Agent — I/O Prefetch

### Назначение
Параллельно читать все конфиг-файлы (`config/*.toml`, `config/*.json`) в RAM до того, как NeoForge начнёт последовательно обращаться к ним.

### Ожидаемый выигрыш
5–10 секунд (убираем I/O stall из однопоточного конвейера)

### Зависимости
```xml
<dependency>
    <groupId>net.bytebuddy</groupId>
    <artifactId>byte-buddy</artifactId>
    <version>1.14.18</version>
</dependency>
<dependency>
    <groupId>net.bytebuddy</groupId>
    <artifactId>byte-buddy-agent</artifactId>
    <version>1.14.18</version>
</dependency>
```

### MANIFEST.MF агента
```
Premain-Class: dev.cryo.agent.PrefetchAgent
Can-Redefine-Classes: true
Can-Retransform-Classes: true
```

### Класс PrefetchAgent

```java
package dev.cryo.agent;

import java.io.File;
import java.io.IOException;
import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;

public class PrefetchAgent {

    // Глобальный кэш: абсолютный путь → байты файла
    public static final ConcurrentHashMap<String, byte[]> configCache =
        new ConcurrentHashMap<>();

    public static void premain(String args, Instrumentation inst) {
        int cores = Runtime.getRuntime().availableProcessors();
        ExecutorService ioPool = Executors.newFixedThreadPool(cores);

        File configDir = new File("config");
        if (configDir.exists() && configDir.isDirectory()) {
            ioPool.submit(() -> prefetchDirectory(configDir));
        }

        ioPool.shutdown();
        try {
            // КРИТИЧНО: жёсткий барьер — main-поток БЛОКИРУЕТСЯ
            // без awaitTermination кэш будет пуст когда NeoForge стартует
            if (!ioPool.awaitTermination(10, TimeUnit.SECONDS)) {
                System.err.println("[Cryo-IO] Предупреждение: таймаут префетча, продолжаем...");
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }

        // Устанавливаем инструментацию Files.readAllBytes
        installByteBuddyTransformer(inst);
    }

    private static void prefetchDirectory(File dir) {
        File[] files = dir.listFiles();
        if (files == null) return;
        for (File file : files) {
            if (file.isDirectory()) {
                prefetchDirectory(file); // рекурсивно
            } else {
                String name = file.getName();
                if (name.endsWith(".toml") || name.endsWith(".json") || name.endsWith(".json5")) {
                    try {
                        byte[] data = Files.readAllBytes(file.toPath());
                        configCache.put(file.getAbsolutePath(), data);
                    } catch (IOException ignored) {
                        // файл недоступен — пропускаем, NeoForge прочитает сам
                    }
                }
            }
        }
    }

    private static void installByteBuddyTransformer(Instrumentation inst) {
        new net.bytebuddy.agent.builder.AgentBuilder.Default()
            // КРИТИЧНО: без этих двух строк ByteBuddy игнорирует классы java.base
            .with(net.bytebuddy.agent.builder.AgentBuilder.RedefinitionStrategy.RETRANSFORMATION)
            .with(net.bytebuddy.agent.builder.AgentBuilder.TypeStrategy.Default.REDEFINE)
            // КРИТИЧНО: .ignore() по умолчанию исключает JDK-классы
            // ElementMatchers.none() = не исключать ничего
            .ignore(net.bytebuddy.matcher.ElementMatchers.none())
            .type(net.bytebuddy.matcher.ElementMatchers.named("java.nio.file.Files"))
            .transform((builder, typeDescription, classLoader, module, protectionDomain) ->
                builder.visit(
                    net.bytebuddy.asm.Advice.to(FilesReadInterceptor.class)
                        .on(net.bytebuddy.matcher.ElementMatchers.named("readAllBytes")
                            .and(net.bytebuddy.matcher.ElementMatchers.takesArguments(
                                java.nio.file.Path.class)))
                )
            )
            .installOn(inst);
    }
}
```

### Класс FilesReadInterceptor

```java
package dev.cryo.agent;

import net.bytebuddy.asm.Advice;
import java.nio.file.Path;

public class FilesReadInterceptor {

    // skipOn = если enter() вернул НЕ null → оригинальный Files.readAllBytes() ПРОПУСКАЕТСЯ
    @Advice.OnMethodEnter(skipOn = Advice.OnNonDefaultValue.class)
    public static byte[] enter(@Advice.Argument(0) Path path) {
        String key = path.toAbsolutePath().toString();
        // null = кэш-промах → оригинальный метод выполняется
        // byte[] = кэш-попадание → оригинальный метод пропускается
        return PrefetchAgent.configCache.get(key);
    }

    @Advice.OnMethodExit
    public static void exit(
        @Advice.Return(readOnly = false) byte[] result,
        @Advice.Enter byte[] cached) {
        // Если enter() вернул данные из кэша — подменяем результат метода
        if (cached != null) {
            result = cached;
        }
        // Если cached == null → оригинальный метод отработал → result уже корректный
    }
}
```

### Обязательный JVM-флаг при запуске игры
```
--add-opens java.base/java.nio.file=ALL-UNNAMED
```
Без него AgentBuilder получит `InaccessibleObjectException` при попытке инструментировать java.base.

### Как подключить агент
В JvmArgs Prism Launcher:
```
-javaagent:path/to/cryo-agent.jar --add-opens java.base/java.nio.file=ALL-UNNAMED
```

### Как проверить работу (Чекпоинт Б1)
В логах должны появиться строки:
```
[Cryo-IO] Перехвачено чтение конфига из ОЗУ: C:\...\config\mymod-common.toml
```
Если строк нет → большинство модов читает конфиги через NightConfig, не через Files.readAllBytes.
В этом случае вторая цель для инструментации:
```java
// Целевой класс: com.electronwill.nightconfig.core.file.FileConfig
// Метод: load() или open()
```

---

## КОМПОНЕНТ 2: Mixin — Параллельный DataPack Executor

### Назначение
Заменить однопоточный `ForkJoinPool` (Util.backgroundExecutor()) на выделенный `FixedThreadPool` при загрузке датапаков. Реестр к этому моменту заморожен (frozen) → операции read-only → 100% thread-safe.

### Ожидаемый выигрыш
20–35 секунд (CPU-bound JSON-парсинг на всех ядрах)

### КРИТИЧЕСКИ ВАЖНО: правильный таргет Mixin

```
❌ НЕВЕРНО:
@Mixin(SimpleReloadInstance.class)
method = "create"                    ← ищет вызов create() внутри самого create()
                                       это рекурсия, которой нет → Mixin никогда не срабатывает

✓ ВЕРНО:
@Mixin(Minecraft.class)              ← класс, который ВЫЗЫВАЕТ SimpleReloadInstance.create()
method = "reloadResourcePacks"       ← метод внутри которого этот вызов происходит
```

### Предварительная проверка (обязательно перед написанием Mixin)
Открыть исходники NeoForge 1.21.1 и найти:
```
grep -r "SimpleReloadInstance.create" --include="*.java"
```
Убедиться что вызов действительно в `Minecraft.reloadResourcePacks()`.
Если NeoForge переопределил этот путь → найти реальный вызывающий класс.

### Код Mixin

```java
package dev.cryo.mixin;

import net.minecraft.client.Minecraft;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.ModifyArg;

import java.util.concurrent.Executor;
import java.util.concurrent.Executors;

@Mixin(Minecraft.class)
public class ParallelReloadMixin {

    // Выделенный пул создаётся ОДИН РАЗ при загрузке класса
    // daemon=true: потоки не удерживают JVM от завершения
    // NORM_PRIORITY: НЕ MAX_PRIORITY — иначе рендер-поток голодает → белый экран
    private static final Executor CRYO_DATAPACK_POOL = Executors.newFixedThreadPool(
        Runtime.getRuntime().availableProcessors(),
        runnable -> {
            Thread t = new Thread(runnable, "Cryo-DataPack-Worker");
            t.setDaemon(true);                        // не удерживаем JVM
            t.setPriority(Thread.NORM_PRIORITY);      // не душим рендер
            return t;
        }
    );

    // index = 2: третий аргумент SimpleReloadInstance.create()
    // Сигнатура: create(ResourceManager, List<PreparableReloadListener>,
    //                   Executor backgroundExecutor,   ← index 2 (нам нужен этот)
    //                   Executor gameExecutor,
    //                   CompletableFuture<Unit>,
    //                   boolean)
    @ModifyArg(
        method = "reloadResourcePacks",
        at = @At(
            value = "INVOKE",
            target = "Lnet/minecraft/server/packs/resources/SimpleReloadInstance;create(" +
                     "Lnet/minecraft/server/packs/resources/ResourceManager;" +
                     "Ljava/util/List;" +
                     "Ljava/util/concurrent/Executor;" +
                     "Ljava/util/concurrent/Executor;" +
                     "Ljava/util/concurrent/CompletableFuture;" +
                     "Z)" +
                     "Lnet/minecraft/server/packs/resources/SimpleReloadInstance;"
        ),
        index = 2
    )
    private Executor redirectPreparationExecutor(Executor original) {
        System.out.println("[Cryo-Core] DataPack executor перехвачен. Используется FixedThreadPool.");
        return CRYO_DATAPACK_POOL;
    }
}
```

### Регистрация Mixin в mixins.json
```json
{
  "required": true,
  "package": "dev.cryo.mixin",
  "compatibilityLevel": "JAVA_21",
  "mixins": [],
  "client": [
    "ParallelReloadMixin",
    "CdsTitleScreenMixin"
  ],
  "injectors": {
    "defaultRequire": 1
  }
}
```

### Как проверить (Чекпоинт Б2)
В логах должна появиться строка:
```
[Cryo-Core] DataPack executor перехвачен. Используется FixedThreadPool.
```
Если строки нет → `@ModifyArg` не нашёл вызов → неверный target.
Диагностика: добавить в `reloadResourcePacks` `@Inject(at=@At("HEAD"))` с логом — убедиться что метод вообще вызывается.

---

## КОМПОНЕНТ 3: Mixin — TitleScreen CDS Trigger

### Назначение
Автоматически завершить процесс игры при появлении главного меню во время CDS-профилировочного запуска.

### Почему halt(0), не exit(0)
- `System.exit(0)` запускает shutdown hooks → некоторые моды вешают долгие деструкторы → процесс может зависнуть
- `Runtime.getRuntime().halt(0)` — немедленное завершение JVM, classlist записывается мгновенно

### Код Mixin

```java
package dev.cryo.mixin;

import net.minecraft.client.gui.screens.TitleScreen;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(TitleScreen.class)
public class CdsTitleScreenMixin {

    @Inject(method = "init", at = @At("HEAD"))
    private void onInit(CallbackInfo ci) {
        // Флаг активен только во время CDS-профилировочного запуска
        // Python-скрипт передаёт -Dcryo.cds.profiling=true
        if ("true".equals(System.getProperty("cryo.cds.profiling"))) {
            System.out.println("[Cryo-CDS] TitleScreen инициализирован. Фиксируем classlist...");
            // Небольшая задержка чтобы classlist успел записаться полностью
            try { Thread.sleep(500); } catch (InterruptedException ignored) {}
            Runtime.getRuntime().halt(0);
        }
    }
}
```

---

## КОМПОНЕНТ 4: Mixin — Параллельный Model Baking

### Назначение
Выпекать модели блоков/предметов параллельно на всех ядрах CPU.
Это самая тяжёлая нетронутая фаза (~45 с).

### Предупреждение о рисках
Перед реализацией обязательно проверить:
1. Есть ли модели с взаимными ссылками (BlockModel → ItemModel)?
2. Является ли TextureAtlas.stitch() потокобезопасным в NeoForge 1.21.1?
3. Нет ли статических мутабельных кэшей внутри ModelBakery?

### Структура Mixin (псевдокод — требует верификации сигнатур)

```java
package dev.cryo.mixin;

import net.minecraft.client.resources.model.ModelBakery;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

import java.util.List;
import java.util.concurrent.ForkJoinPool;
import java.util.function.Consumer;

@Mixin(ModelBakery.class)
public class ParallelModelBakingMixin {

    // ВАЖНО: точное имя метода и сигнатуру нужно найти в исходниках NeoForge 1.21.1
    // Возможные кандидаты: bakeModels(), processLoading(), uploadTextures()
    // Поиск: grep -r "ModelBakery" --include="*.java" | grep "forEach\|stream\|for ("

    @Redirect(
        method = "bakeModels", // <- ЗАМЕНИТЬ на реальное имя метода после проверки исходников
        at = @At(
            value = "INVOKE",
            target = "Ljava/util/List;forEach(Ljava/util/function/Consumer;)V"
        )
    )
    private void parallelBakeModels(List<?> list, Consumer consumer) {
        // Параллельный стрим на всех ядрах
        // КРИТИЧНО: не использовать common ForkJoinPool — он шарится с рендером
        ForkJoinPool customPool = new ForkJoinPool(
            Runtime.getRuntime().availableProcessors()
        );
        try {
            customPool.submit(() ->
                list.parallelStream().forEach(consumer)
            ).get();
        } catch (Exception e) {
            // Fallback: если параллельно упало — делаем последовательно
            System.err.println("[Cryo-Baking] Ошибка параллельного baking, fallback: " + e.getMessage());
            list.forEach(consumer);
        } finally {
            customPool.shutdown();
        }
    }
}
```

### Как проверить
Async Profiler должен показать утилизацию всех ядер во время фазы baking.
Команда для профилировки:
```bash
java -agentpath:path/to/libasyncProfiler.so=start,event=cpu,file=baking_profile.html ...
```

---

## КОМПОНЕНТ 5: Python Orchestrator — AppCDS Lifecycle

### Назначение
Автоматически управлять жизненным циклом AppCDS-архива: генерировать при изменении модов, инжектировать флаги в instance.cfg, откатывать при ошибках.

### Требования
- Python 3.10+
- Prism Launcher (MultiMC совместим с минимальными правками)
- Java 21 с поддержкой Xshare

### Полный код скрипта

```python
#!/usr/bin/env python3
"""
Cryo-Launcher: AppCDS Lifecycle Manager для ATM10
Использование: python cryo_launcher.py
"""

import hashlib
import os
import subprocess
import sys
import shutil
from pathlib import Path

# ============================================================
# КОНФИГУРАЦИЯ — настроить под свою систему
# ============================================================
INSTANCE_PATH   = Path("instances/ATM10")
INSTANCE_CFG    = INSTANCE_PATH / "instance.cfg"
HASH_FILE       = INSTANCE_PATH / ".cryo_hash"
CLASSLIST_FILE  = INSTANCE_PATH / "atm.classlist"
JSA_FILE        = INSTANCE_PATH / "atm.jsa"

# JVM флаги — базовые (без CDS и без агента, они добавляются динамически)
BASE_JVM_FLAGS = (
    "-XX:+UseZGC -XX:+ZGenerational "
    "-Xms4G -Xmx12G "
    "-XX:+UnlockDiagnosticVMOptions "
    "--add-opens java.base/java.nio.file=ALL-UNNAMED"
)

CRYO_AGENT_PATH = Path("cryo-agent.jar")  # путь к собранному агенту
# ============================================================


def get_mods_hash() -> str:
    """
    Детерминированный хэш папки mods/ по метаданным файлов.
    Использует имя + mtime + size (быстро, без чтения содержимого).
    SHA256 содержимого надёжнее но медленнее для 400+ JAR-файлов.
    """
    mods_dir = INSTANCE_PATH / "mods"
    if not mods_dir.exists():
        return ""

    hash_obj = hashlib.sha256()
    for f in sorted(mods_dir.iterdir()):
        if f.is_file() and f.suffix == ".jar":
            stat = f.stat()
            meta = f"{f.name}_{stat.st_mtime}_{stat.st_size}"
            hash_obj.update(meta.encode())
    return hash_obj.hexdigest()


def read_stored_hash() -> str:
    if HASH_FILE.exists():
        return HASH_FILE.read_text().strip()
    return ""


def write_stored_hash(h: str):
    HASH_FILE.write_text(h)


def build_classpath() -> str:
    """
    Собирает classpath из библиотек Prism и модов.
    ВАЖНО: NeoForge использует сложную модульную систему —
    этот classpath только для шага Xshare:dump, не для запуска игры.
    Реальный запуск игры — всегда через prismlauncher --launch.
    """
    sep = ";" if os.name == "nt" else ":"
    jars = []

    libs_dir = INSTANCE_PATH / ".minecraft" / "libraries"
    if libs_dir.exists():
        for root, _, files in os.walk(libs_dir):
            for f in files:
                if f.endswith(".jar"):
                    jars.append(os.path.join(root, f))

    mods_dir = INSTANCE_PATH / "mods"
    if mods_dir.exists():
        for f in mods_dir.iterdir():
            if f.is_file() and f.suffix == ".jar":
                jars.append(str(f))

    return sep.join(jars)


def get_jvm_args() -> str:
    """Читает текущую строку JvmArgs= из instance.cfg"""
    if not INSTANCE_CFG.exists():
        return ""
    for line in INSTANCE_CFG.read_text(encoding="utf-8").splitlines():
        if line.startswith("JvmArgs="):
            return line[len("JvmArgs="):]
    return ""


def set_jvm_args(new_args: str):
    """
    Атомарно заменяет строку JvmArgs= в instance.cfg.
    Если строки нет — добавляет в конец.
    Использует временный файл для атомарности записи.
    """
    if not INSTANCE_CFG.exists():
        print(f"[Cryo] ОШИБКА: {INSTANCE_CFG} не найден", file=sys.stderr)
        return

    content = INSTANCE_CFG.read_text(encoding="utf-8")
    lines = content.splitlines(keepends=True)
    found = False
    new_lines = []
    for line in lines:
        if line.startswith("JvmArgs="):
            new_lines.append(f"JvmArgs={new_args}\n")
            found = True
        else:
            new_lines.append(line)
    if not found:
        new_lines.append(f"JvmArgs={new_args}\n")

    # Атомарная запись через временный файл
    tmp = INSTANCE_CFG.with_suffix(".cfg.tmp")
    tmp.write_text("".join(new_lines), encoding="utf-8")
    shutil.move(str(tmp), str(INSTANCE_CFG))


def launch_prism(instance_name: str = "ATM10") -> int:
    """Запускает инстанс через Prism Launcher. Ждёт завершения."""
    result = subprocess.run(
        ["prismlauncher", "--launch", instance_name],
        check=False
    )
    return result.returncode


def generate_classlist(jvm_flags: str, instance_name: str = "ATM10") -> bool:
    """
    Шаг 1 генерации AppCDS: запускает игру с DumpLoadedClassList.
    Игра сама завершится через CdsTitleScreenMixin.halt(0) при открытии TitleScreen.
    """
    profiling_flags = (
        f"{jvm_flags} "
        f"-XX:DumpLoadedClassList={CLASSLIST_FILE} "
        f"-Dcryo.cds.profiling=true"
    )

    original_args = get_jvm_args()
    set_jvm_args(profiling_flags)
    try:
        print("[Cryo] Шаг 1: Запуск для сбора classlist (игра закроется автоматически)...")
        returncode = launch_prism(instance_name)
        # halt(0) возвращает код 0 — это ожидаемо
        return CLASSLIST_FILE.exists()
    finally:
        set_jvm_args(original_args)  # восстанавливаем в любом случае


def generate_jsa_archive(jvm_flags: str) -> bool:
    """
    Шаг 2 генерации AppCDS: создаёт .jsa архив из classlist.
    Это НЕ запуск игры — это сервисный вызов JVM.
    """
    if not CLASSLIST_FILE.exists():
        print("[Cryo] ОШИБКА: classlist не найден, пропускаем dump", file=sys.stderr)
        return False

    cp = build_classpath()
    cmd = (
        f"java {jvm_flags} "
        f"-Xshare:dump "
        f"-XX:SharedClassListFile={CLASSLIST_FILE} "
        f"-XX:SharedArchiveFile={JSA_FILE} "
        f"-cp {cp} "
        f"-version"  # просто выйти после dump
    )

    print("[Cryo] Шаг 2: Генерация .jsa архива...")
    result = subprocess.run(cmd, shell=True, check=False)
    return JSA_FILE.exists() and result.returncode == 0


def run_pipeline():
    current_hash = get_mods_hash()
    stored_hash  = read_stored_hash()
    original_args = get_jvm_args()

    # --- ВЕТКА А: Хэш совпал — штатный ускоренный запуск ---
    if current_hash == stored_hash and JSA_FILE.exists():
        print("[Cryo] Моды не изменились. Запуск с AppCDS...")

        # -Xshare:auto (НЕ :on!) — если архив невалиден, JVM запустится без него
        boost_flags = (
            f"{BASE_JVM_FLAGS} "
            f"-Xshare:auto "
            f"-XX:SharedArchiveFile={JSA_FILE} "
            f"-javaagent:{CRYO_AGENT_PATH}"
        )

        try:
            set_jvm_args(boost_flags)
            launch_prism()
        finally:
            # ВСЕГДА восстанавливаем оригинальные флаги после закрытия игры
            set_jvm_args(original_args)
        return

    # --- ВЕТКА Б: Изменение модов — регенерация AppCDS архива ---
    print("[Cryo] Обнаружено изменение модов. Регенерация AppCDS архива...")

    success = False
    try:
        # Шаг 1: собрать classlist (запуск с CDS Mixin)
        if not generate_classlist(BASE_JVM_FLAGS):
            raise RuntimeError("Не удалось создать classlist")

        # Шаг 2: сгенерировать .jsa
        if not generate_jsa_archive(BASE_JVM_FLAGS):
            raise RuntimeError("Не удалось создать .jsa архив")

        success = True
        write_stored_hash(current_hash)
        print("[Cryo] AppCDS архив успешно создан.")

    except Exception as e:
        print(f"[Cryo] КРИТИЧЕСКИЙ СБОЙ: {e}", file=sys.stderr)

    finally:
        if not success:
            # Полный откат — игра запустится без AppCDS
            set_jvm_args(original_args)
            print("[Cryo] Откат к исходным флагам. AppCDS отключён для этой сессии.")

    # Запуск игры после генерации (или без AppCDS при ошибке)
    if success:
        boost_flags = (
            f"{BASE_JVM_FLAGS} "
            f"-Xshare:auto "
            f"-XX:SharedArchiveFile={JSA_FILE} "
            f"-javaagent:{CRYO_AGENT_PATH}"
        )
        try:
            set_jvm_args(boost_flags)
            launch_prism()
        finally:
            set_jvm_args(original_args)  # всегда чистим
    else:
        # Запуск без оптимизаций
        try:
            launch_prism()
        finally:
            set_jvm_args(original_args)


if __name__ == "__main__":
    run_pipeline()
```

---

## ИНТЕГРАЦИЯ: Структура Gradle-проекта

```
cryo-loader/
├── build.gradle
├── gradle.properties
├── src/
│   ├── main/
│   │   ├── java/dev/cryo/
│   │   │   ├── mixin/
│   │   │   │   ├── ParallelReloadMixin.java
│   │   │   │   ├── CdsTitleScreenMixin.java
│   │   │   │   └── ParallelModelBakingMixin.java
│   │   │   └── CryoMod.java
│   │   └── resources/
│   │       ├── META-INF/mods.toml
│   │       └── cryo.mixins.json
├── cryo-agent/
│   ├── build.gradle   ← отдельный subproject, отдельный jar
│   └── src/main/java/dev/cryo/agent/
│       ├── PrefetchAgent.java
│       └── FilesReadInterceptor.java
└── scripts/
    └── cryo_launcher.py
```

### build.gradle (агент — отдельный subproject)
```groovy
plugins {
    id 'java'
}

dependencies {
    implementation 'net.bytebuddy:byte-buddy:1.14.18'
    implementation 'net.bytebuddy:byte-buddy-agent:1.14.18'
}

jar {
    manifest {
        attributes(
            'Premain-Class': 'dev.cryo.agent.PrefetchAgent',
            'Can-Redefine-Classes': 'true',
            'Can-Retransform-Classes': 'true'
        )
    }
    // Fat jar: включаем ByteBuddy в агент
    from { configurations.runtimeClasspath.collect { it.isDirectory() ? it : zipTree(it) } }
    duplicatesStrategy = DuplicatesStrategy.EXCLUDE
}
```

---

## ЧЕКПОИНТЫ ВЕРИФИКАЦИИ (по порядку выполнения)

### Чекпоинт А — Слой 1 (AppCDS)
**Измерение:** сравнить время запуска с `-Xshare:off` vs `-Xshare:auto`
**Ожидание:** -8–15 с
**Провал:** если < 3 с → ModClassLoader не даёт AppCDS проецировать модовые классы в Metaspace

### Чекпоинт Б1 — Агент (I/O Prefetch)  
**Измерение:** подсчитать строки `[Cryo-IO] Перехвачено` в логе
**Ожидание:** > 50 перехватов → агент работает
**Провал:** 0 перехватов → инструментация не применилась, проверить --add-opens и MANIFEST.MF

### Чекпоинт Б2 — Mixin (DataPack)
**Измерение:** строка `[Cryo-Core] DataPack executor перехвачен` в логе
**Ожидание:** строка есть, время DataPack фазы сократилось на 20–35 с
**Провал:** строки нет → неверный target метода, проверить исходники NeoForge 1.21.1

### Чекпоинт В — Model Baking
**Измерение:** Async Profiler, событие cpu, смотреть thread utilization во время baking
**Ожидание:** все ядра загружены на 80%+ во время фазы baking
**Провал:** deadlock или краш → есть взаимные зависимости между моделями

---

## ИЗВЕСТНЫЕ РИСКИ И ОБХОДНЫЕ ПУТИ

| Риск | Вероятность | Обходной путь |
|------|-------------|---------------|
| reloadResourcePacks не вызывает SimpleReloadInstance.create() в NeoForge 1.21.1 | Средняя | Найти реальный call-site через -verbose:class + grep |
| Files.readAllBytes покрывает < 30% конфиг-чтения | Средняя | Переориентировать на NightConfig.FileConfig.load() |
| AppCDS даёт < 5 с выигрыша | Низкая | ModClassLoader частично блокирует CDS — нормально |
| ParallelModelBaking → deadlock на моделях с зависимостями | Средняя | Добавить try/catch с fallback на sequential |
| Mixin Bytecode Cache ломает dynamic Mixin selectors | Высокая | Кэшировать только статически определённые трансформации |

---

## ИНСТРУКЦИЯ ДЛЯ КОДОГЕНЕРАТОРА

Ты Senior Java/JVM архитектор. Реализуй следующие компоненты строго по спецификации выше:

1. `dev.cryo.agent.PrefetchAgent` — Java Agent с ByteBuddy инструментацией Files.readAllBytes
2. `dev.cryo.agent.FilesReadInterceptor` — Advice класс с @OnMethodEnter(skipOn) + @OnMethodExit
3. `dev.cryo.mixin.ParallelReloadMixin` — @ModifyArg на Minecraft.reloadResourcePacks, index=2
4. `dev.cryo.mixin.CdsTitleScreenMixin` — @Inject в TitleScreen.init() с System.getProperty проверкой
5. `dev.cryo.mixin.ParallelModelBakingMixin` — @Redirect на forEach в ModelBakery (найти точный метод)
6. `scripts/cryo_launcher.py` — Python 3.10+ orchestrator с транзакционным instance.cfg и двухшаговым AppCDS

**Жёсткие правила:**
- Thread.NORM_PRIORITY везде — MAX_PRIORITY убивает рендер-поток
- daemon=true на всех рабочих потоках
- -Xshare:auto везде — :on вызовет JVM ABORT при смене версии Java
- awaitTermination в premain — без него кэш пуст при старте NeoForge
- .ignore(ElementMatchers.none()) в AgentBuilder — без него JDK классы игнорируются
- try/finally с восстановлением instance.cfg в ОБЕИХ ветках Python-скрипта

**Не делать:**
- Не использовать Unsafe.allocateInstance() ни в каком виде
- Не параллелить конструирование @Mod классов
- Не использовать Thread.MAX_PRIORITY
- Не использовать -Xshare:on (только :auto)
- Не хардкодить classpath — собирать динамически через build_classpath()
- Не вызывать prismlauncher с несуществующими флагами (--dry-run, --autoclose не существуют)
