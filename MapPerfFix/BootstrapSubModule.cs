using System;
using System.IO;
using System.Reflection;
using TaleWorlds.MountAndBlade;

namespace MapPerfProbe
{
    /// <summary>
    /// Minimal loader sentinel that runs before the MCM-dependent main submodule.
    /// It proves that Bannerlord loaded MapPerfProbe.dll and instantiated a submodule.
    /// </summary>
    public sealed class BootstrapSubModule : MBSubModuleBase
    {
        private static readonly string Version = ResolveVersion();

        internal static string VersionText => Version;

        protected override void OnSubModuleLoad()
        {
            TryWriteBootstrapSentinel("entered OnSubModuleLoad");

            try
            {
                MapPerfLog.DebugEnabled = true;
                MapPerfLog.Initialize();
                MapPerfLog.Info(
                    "MapPerfProbe bootstrap " + Version +
                    " loaded before MCM settings and Harmony initialization.");
            }
            catch (Exception exception)
            {
                TryWriteBootstrapSentinel(
                    "MapPerfLog bootstrap failed: " + exception.GetType().FullName +
                    ": " + exception.Message);
            }

            try
            {
                base.OnSubModuleLoad();
            }
            catch (Exception exception)
            {
                TryWriteBootstrapSentinel(
                    "MBSubModuleBase.OnSubModuleLoad failed: " +
                    exception.GetType().FullName + ": " + exception.Message);
            }
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            try
            {
                base.OnBeforeInitialModuleScreenSetAsRoot();
            }
            catch (Exception exception)
            {
                TryWriteBootstrapSentinel(
                    "MBSubModuleBase.OnBeforeInitialModuleScreenSetAsRoot failed: " +
                    exception.GetType().FullName + ": " + exception.Message);
            }

            TryWriteBootstrapSentinel("entered OnBeforeInitialModuleScreenSetAsRoot");
            TryShowStatusMessage(
                "MapPerfProbe " + Version + " LOADED. Log: " + MapPerfLog.CurrentPath);
        }

        private static void TryWriteBootstrapSentinel(string message)
        {
            try
            {
                WriteBootstrapSentinel(message);
            }
            catch
            {
                // Fail open: bootstrap diagnostics must never block module loading.
            }
        }

        private static void WriteBootstrapSentinel(string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                       " [MapPerfProbe " + Version + "] " + message +
                       " | assembly=" + SafeAssemblyLocation() +
                       Environment.NewLine;

            var assemblyDirectory = SafeAssemblyDirectory();
            if (!string.IsNullOrEmpty(assemblyDirectory))
            {
                TryWriteText(
                    Path.Combine(assemblyDirectory, "MapPerfProbe.loaded.txt"),
                    line);
                TryAppend(
                    Path.Combine(assemblyDirectory, "MapPerfProbe-bootstrap.log"),
                    line);
            }

            var temp = SafeTempPath();
            if (!string.IsNullOrEmpty(temp))
                TryAppend(Path.Combine(temp, "MapPerfProbe", "bootstrap.log"), line);

            var local = SafeFolder(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
                TryAppend(Path.Combine(local, "MapPerfProbe", "bootstrap.log"), line);

            var baseDirectory = SafeBaseDirectory();
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                TryAppend(
                    Path.Combine(baseDirectory, "MapPerfProbe-bootstrap.log"),
                    line);
            }
        }

        private static bool TryAppend(string path, string line)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.AppendAllText(path, line);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWriteText(string path, string text)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(path, text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void TryShowStatusMessage(string text)
        {
            try
            {
                var messageType = Type.GetType(
                    "TaleWorlds.Library.InformationMessage, TaleWorlds.Library",
                    false);
                var managerType = Type.GetType(
                    "TaleWorlds.Library.InformationManager, TaleWorlds.Library",
                    false);
                if (messageType == null || managerType == null)
                {
                    TryWriteBootstrapSentinel(
                        "startup status API was not found in TaleWorlds.Library");
                    return;
                }

                var constructor = messageType.GetConstructor(new[] { typeof(string) });
                if (constructor == null)
                {
                    TryWriteBootstrapSentinel(
                        "InformationMessage(string) constructor was not found");
                    return;
                }

                var display = managerType.GetMethod(
                    "DisplayMessage",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { messageType },
                    null);
                if (display == null)
                {
                    TryWriteBootstrapSentinel(
                        "InformationManager.DisplayMessage was not found");
                    return;
                }

                var status = constructor.Invoke(new object[] { text });
                display.Invoke(null, new[] { status });
            }
            catch (Exception exception)
            {
                TryWriteBootstrapSentinel(
                    "startup status display failed: " + exception.GetType().FullName +
                    ": " + exception.Message);
            }
        }

        private static string ResolveVersion()
        {
            try
            {
                var version = typeof(BootstrapSubModule).Assembly.GetName().Version;
                return version == null
                    ? "unknown"
                    : version.Major + "." + version.Minor + "." + version.Build;
            }
            catch
            {
                return "unknown";
            }
        }

        private static string SafeTempPath()
        {
            try { return Path.GetTempPath(); }
            catch { return string.Empty; }
        }

        private static string SafeFolder(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch { return string.Empty; }
        }

        private static string SafeBaseDirectory()
        {
            try { return AppDomain.CurrentDomain.BaseDirectory; }
            catch { return string.Empty; }
        }

        private static string SafeAssemblyDirectory()
        {
            try
            {
                var location = typeof(BootstrapSubModule).Assembly.Location;
                return string.IsNullOrEmpty(location)
                    ? string.Empty
                    : Path.GetDirectoryName(location) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeAssemblyLocation()
        {
            try { return typeof(BootstrapSubModule).Assembly.Location; }
            catch { return "<unknown>"; }
        }
    }
}
