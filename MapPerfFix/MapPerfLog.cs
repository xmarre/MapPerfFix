using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace MapPerfProbe
{
    internal static class MapPerfLog
    {
        private const long MaxBytes = 5L * 1024 * 1024;
        private const int MaxBackups = 3;
        private static readonly object Sync = new object();
        private static readonly string[] CandidatePaths = BuildCandidatePaths();
        private static string _path;
        private static int _debugEnabled;
        private static int _initialized;
        private static MethodInfo _enginePrint;
        private static int _enginePrintResolved;

        internal static string CurrentPath => _path ?? CandidatePaths[0];

        internal static bool DebugEnabled
        {
            get => Volatile.Read(ref _debugEnabled) != 0;
            set => Interlocked.Exchange(ref _debugEnabled, value ? 1 : 0);
        }

        internal static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0)
                return;

            lock (Sync)
            {
                for (var i = 0; i < CandidatePaths.Length; i++)
                {
                    if (TrySelectPath(CandidatePaths[i]))
                        break;
                }
            }

            Info("Logger initialized. File: " + CurrentPath);
        }

        internal static void Debug(string message)
        {
            if (DebugEnabled)
                Write("DEBUG", message, null);
        }

        internal static void Info(string message) => Write("INFO", message, null);
        internal static void Warn(string message) => Write("WARN", message, null);
        internal static void Error(string message, Exception exception = null) =>
            Write("ERROR", message, exception);

        private static void Write(string level, string message, Exception exception)
        {
            if (Volatile.Read(ref _initialized) == 0)
                Initialize();

            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                       " [" + level + "] " + message;
            if (exception != null)
                line += " :: " + exception.GetType().FullName + ": " + exception.Message +
                        Environment.NewLine + exception.StackTrace;

            var written = false;
            lock (Sync)
            {
                if (!string.IsNullOrEmpty(_path))
                    written = TryAppend(_path, line);

                if (!written)
                {
                    for (var i = 0; i < CandidatePaths.Length; i++)
                    {
                        if (string.Equals(CandidatePaths[i], _path, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!TrySelectPath(CandidatePaths[i]))
                            continue;
                        written = TryAppend(_path, line);
                        if (written)
                            break;
                    }
                }
            }

            TryMirrorToEngine("[MapPerfProbe] " + line);
            if (!written)
            {
                try { Console.Error.WriteLine("[MapPerfProbe] " + line); }
                catch { }
            }
        }

        private static bool TrySelectPath(string path)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                }

                _path = path;
                RotateIfNeeded(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryAppend(string path, string line)
        {
            try
            {
                RotateIfNeeded(path);
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    stream.Write(bytes, 0, bytes.Length);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MaxBytes)
                    return;

                for (var index = MaxBackups - 1; index >= 1; index--)
                {
                    var source = path + "." + index;
                    var destination = path + "." + (index + 1);
                    if (File.Exists(destination))
                        File.Delete(destination);
                    if (File.Exists(source))
                        File.Move(source, destination);
                }

                var first = path + ".1";
                if (File.Exists(first))
                    File.Delete(first);
                File.Move(path, first);
            }
            catch
            {
            }
        }

        private static string[] BuildCandidatePaths()
        {
            var paths = new List<string>();
            AddCandidate(paths, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            AddCandidate(paths, Environment.GetFolderPath(Environment.SpecialFolder.Personal));

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
                paths.Add(Path.Combine(local, "MapPerfProbe", "probe.log"));

            paths.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MapPerfProbe.log"));
            return paths.ToArray();
        }

        private static void AddCandidate(ICollection<string> paths, string documents)
        {
            if (string.IsNullOrEmpty(documents))
                return;

            var candidate = Path.Combine(
                documents,
                "Mount and Blade II Bannerlord",
                "Logs",
                "MapPerfProbe",
                "probe.log");

            foreach (var existing in paths)
            {
                if (string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            paths.Add(candidate);
        }

        private static void TryMirrorToEngine(string line)
        {
            try
            {
                if (Volatile.Read(ref _enginePrintResolved) == 0)
                {
                    ResolveEnginePrint();
                    Volatile.Write(ref _enginePrintResolved, 1);
                }

                var method = _enginePrint;
                if (method == null)
                    return;

                var parameters = method.GetParameters();
                var arguments = new object[parameters.Length];
                arguments[0] = line;
                for (var i = 1; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    if (parameter.HasDefaultValue)
                        arguments[i] = parameter.DefaultValue;
                    else if (parameter.ParameterType.IsValueType)
                        arguments[i] = Activator.CreateInstance(parameter.ParameterType);
                    else
                        arguments[i] = null;
                }

                method.Invoke(null, arguments);
            }
            catch
            {
            }
        }

        private static void ResolveEnginePrint()
        {
            var debugType = Type.GetType("TaleWorlds.Library.Debug, TaleWorlds.Library", false);
            if (debugType == null)
                return;

            MethodInfo best = null;
            var methods = debugType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (!string.Equals(method.Name, "Print", StringComparison.Ordinal))
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(string))
                    continue;
                if (best == null || parameters.Length < best.GetParameters().Length)
                    best = method;
            }

            _enginePrint = best;
        }
    }
}
