package dev.vspeed.agent;

import net.bytebuddy.asm.Advice;
import java.io.ByteArrayInputStream;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;

public class FilesReadInterceptor {

    public static class BytesAdvice {
        @Advice.OnMethodEnter(skipOn = Advice.OnNonDefaultValue.class)
        public static byte[] enter(@Advice.Argument(0) Path path) {
            byte[] cached = VSpeedAgent.configCache.get(VSpeedAgent.normalize(path));
            if (cached != null) {
                VSpeedAgent.cacheHits.incrementAndGet();
                VSpeedAgent.updateHits();
                return cached;
            }
            return null;
        }
        @Advice.OnMethodExit
        public static void exit(@Advice.Return(readOnly = false) byte[] result, @Advice.Enter byte[] cached) {
            if (cached != null) result = cached;
        }
    }

    public static class StreamAdvice {
        @Advice.OnMethodEnter(skipOn = Advice.OnNonDefaultValue.class)
        public static InputStream enter(@Advice.Argument(0) Path path) {
            byte[] cached = VSpeedAgent.configCache.get(VSpeedAgent.normalize(path));
            if (cached != null) {
                VSpeedAgent.cacheHits.incrementAndGet();
                VSpeedAgent.updateHits();
                return new ByteArrayInputStream(cached);
            }
            return null;
        }
        @Advice.OnMethodExit
        public static void exit(@Advice.Return(readOnly = false) InputStream result, @Advice.Enter InputStream cached) {
            if (cached != null) result = cached;
        }
    }

    public static class StringAdvice {
        @Advice.OnMethodEnter(skipOn = Advice.OnNonDefaultValue.class)
        public static String enter(@Advice.Argument(0) Path path) {
            byte[] cached = VSpeedAgent.configCache.get(VSpeedAgent.normalize(path));
            if (cached != null) {
                VSpeedAgent.cacheHits.incrementAndGet();
                VSpeedAgent.updateHits();
                return new String(cached, StandardCharsets.UTF_8);
            }
            return null;
        }
        @Advice.OnMethodExit
        public static void exit(@Advice.Return(readOnly = false) String result, @Advice.Enter String cached) {
            if (cached != null) result = cached;
        }
    }
}
