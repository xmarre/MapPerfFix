using System;
using System.IO;

namespace MapPerfProbe
{
    internal static class MapPerfLog
    {
        private static readonly string DefaultPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "Mount and Blade II Bannerlord", "Logs", "MapPerfProbe", "probe.log");

        private static readonly object _sync = new object();
        private const long MaxBytes = 5L * 1024 * 1024;
        private const int MaxBackups = 3;
        private static string _path = DefaultPath;

        static MapPerfLog()
        {
            try { EnsureDir(); RotateIfNeeded(); } catch { }
        }

        public static void Info(string msg) => Write("INFO", msg, null);
        public static void Warn(string msg) => Write("WARN", msg, null);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", msg, ex);

        private static void Write(string level, string msg, Exception ex)
        {
            lock (_sync)
            {
                try
                {
                    EnsureDir(); RotateIfNeeded();
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
                    if (ex != null) line += $" :: {ex.GetType().Name}: {ex.Message}";
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch { }
            }
        }

        private static void EnsureDir()
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (!fi.Exists || fi.Length < MaxBytes) return;

                for (int i = MaxBackups - 1; i >= 1; i--)
                {
                    var src = _path + "." + i;
                    var dst = _path + "." + (i + 1);
                    if (File.Exists(dst)) File.Delete(dst);
                    if (File.Exists(src)) File.Move(src, dst);
                }
                var first = _path + ".1";
                if (File.Exists(first)) File.Delete(first);
                File.Move(_path, first);
            }
            catch { }
        }
    }
}
