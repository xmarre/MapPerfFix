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
        private const long PathRetryCooldownTicks = TimeSpan.TicksPerSecond * 5L;

        private static readonly object Sync = new object();
        private static readonly string[] CandidatePaths = BuildCandidatePaths();
        private static string _path;
        private static long _activePathSize;
        private static long _nextPathRetryUtcTicks;
        private static int _debugEnabled;
        private static int _initialized;
        private static MethodInfo _enginePrint;
        private static ParameterInfo[] _enginePrintParameters;
        private static int _enginePrintResolved;

        internal static string CurrentPath
        {
            get
            {
                var selected = _path;
                if (!string.IsNullOrEmpty(selected))
                    return selected;
                return CandidatePaths.Length > 0
                    ? CandidatePaths[0]
                    : "<filesystem logging unavailable>";
            }
        }

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

                if (string.IsNullOrEmpty(_path))
                    _nextPathRetryUtcTicks = DateTime.UtcNow.Ticks + PathRetryCooldownTicks;
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
                var now = DateTime.UtcNow.Ticks;
                if (now >= _nextPathRetryUtcTicks)
                {
                    if (!string.IsNullOrEmpty(_path))
                        written = TryAppend(_path, line);

                    if (!written)
                    {
                        for (var i = 0; i < CandidatePaths.Length; i++)
                        {
                            if (string.Equals(
                                CandidatePaths[i],
                                _path,
                                StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!TrySelectPath(CandidatePaths[i]))
                                continue;
                            written = TryAppend(_path, line);
                            if (written)
                                break;
                        }
                    }

                    _nextPathRetryUtcTicks = written
                        ? 0L
                        : now + PathRetryCooldownTicks;
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

                using (var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite))
                {
                }

                var info = new FileInfo(path);
                _path = path;
                _activePathSize = info.Exists ? info.Length : 0L;
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
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                RotateIfNeeded(path);
                using (var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
                    _activePathSize += bytes.Length;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void RotateIfNeeded(string path)
        {
            if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) ||
                _activePathSize < MaxBytes)
                return;

            try
            {
                if (!File.Exists(path))
                {
                    _activePathSize = 0L;
                    return;
                }

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
                _activePathSize = 0L;
            }
            catch
            {
            }
        }

        private static string[] BuildCandidatePaths()
        {
            try
            {
                var paths = new List<string>();
                AddCandidate(
                    paths,
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                AddCandidate(
                    paths,
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal));

                var local = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                    paths.Add(Path.Combine(local, "MapPerfProbe", "probe.log"));

                paths.Add(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "MapPerfProbe.log"));
                return paths.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
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
                var parameters = _enginePrintParameters;
                if (method == null || parameters == null)
                    return;

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
            var debugType = Type.GetType(
                "TaleWorlds.Library.Debug, TaleWorlds.Library",
                false);
            if (debugType == null)
            {
                _enginePrint = null;
                _enginePrintParameters = null;
                return;
            }

            MethodInfo best = null;
            ParameterInfo[] bestParameters = null;
            var methods = debugType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                if (!string.Equals(method.Name, "Print", StringComparison.Ordinal))
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(string))
                    continue;
                if (bestParameters != null && parameters.Length >= bestParameters.Length)
                    continue;

                best = method;
                bestParameters = parameters;
            }

            _enginePrint = best;
            _enginePrintParameters = bestParameters;
        }
    }
}
