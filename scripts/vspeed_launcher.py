#!/usr/bin/env python3
"""
vspeed-Launcher: AppCDS Lifecycle Manager для ATM10
Использование: python vspeed_launcher.py
"""

import hashlib
import os
import subprocess
import sys
import shutil
import re
import shlex
from pathlib import Path

# ============================================================
# КОНФИГУРАЦИЯ
# ============================================================
INSTANCE_ROOT   = Path(r"C:\Users\xponer\AppData\Roaming\PrismLauncher\instances\All the Mods 10 - ATM10")
INSTANCE_PATH   = INSTANCE_ROOT / "minecraft"
PRISM_EXE       = r"C:\Users\xponer\AppData\Local\Programs\PrismLauncher\prismlauncher.exe"

INSTANCE_CFG    = INSTANCE_ROOT / "instance.cfg"
HASH_FILE       = INSTANCE_PATH / ".vspeed_hash"
CLASSLIST_FILE  = INSTANCE_PATH / "atm.classlist"
JSA_FILE        = INSTANCE_PATH / "atm.jsa"

PROJECT_ROOT    = Path(__file__).parent.parent.absolute()
VSPEED_AGENT_PATH = PROJECT_ROOT / "vspeed-agent/build/libs/vspeed-agent.jar"
VSPEED_MOD_PATH   = PROJECT_ROOT / "build/libs/vspeed-v2-1.0-SNAPSHOT.jar"

INSTANCE_NAME   = "All the Mods 10 - ATM10" 

def get_jvm_args() -> str:
    if not INSTANCE_CFG.exists(): return ""
    try:
        content = INSTANCE_CFG.read_text(encoding="utf-8")
        for line in content.splitlines():
            if line.startswith("JvmArgs="):
                return line[len("JvmArgs="):].strip()
    except: pass
    return ""

def get_clean_jvm_args() -> str:
    """Возвращает текущие аргументы, очищенные от наших флагов через shlex."""
    raw = get_jvm_args()
    try:
        # shlex правильно обработает кавычки
        tokens = shlex.split(raw)
        # Фильтруем наши флаги
        cleaned = [t for t in tokens if not (
            t.startswith("-Dvspeed.") or 
            "-javaagent" in t and "vspeed-agent" in t or
            t.startswith("-Xshare:") or
            t.startswith("-XX:SharedArchiveFile") or
            t.startswith("-XX:SharedClassListFile") or
            t.startswith("-XX:DumpLoadedClassList")
        )]
        return shlex.join(cleaned)
    except:
        # Fallback если shlex не справился (например, битая строка)
        return raw

def set_jvm_args(args_string: str):
    if not INSTANCE_CFG.exists(): return
    # Prism Launcher ожидает строку в конфиге, часто в лапках если есть пробелы
    # Мы сохраняем "как есть", так как shlex.join уже добавит нужные лапки внутри
    
    content = INSTANCE_CFG.read_text(encoding="utf-8")
    lines = content.splitlines(keepends=True)
    new_lines = []
    found = False
    for line in lines:
        if line.startswith("JvmArgs="):
            new_lines.append(f"JvmArgs={args_string}\n")
            found = True
        else:
            new_lines.append(line)
    if not found:
        new_lines.append(f"JvmArgs={args_string}\n")
    
    tmp = INSTANCE_CFG.with_suffix(".cfg.tmp")
    tmp.write_text("".join(new_lines), encoding="utf-8")
    shutil.move(str(tmp), str(INSTANCE_CFG))

def run_pipeline():
    base_args = get_clean_jvm_args()
    # Просто запускаем игру с базовыми настройками для проверки
    print(f"[VSpeed] Запуск {INSTANCE_NAME}...")
    launch_prism(INSTANCE_NAME)

def launch_prism(instance_name: str) -> int:
    try:
        # Мы НЕ ждем завершения здесь, так как Prism может просто передать команду
        subprocess.run([PRISM_EXE, "--launch", instance_name], check=False)
        return 0
    except: return 1

if __name__ == "__main__":
    run_pipeline()
