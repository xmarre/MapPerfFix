using System;
using System.IO;
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

            if (TryAppend(Path.Combine(
                    SafeTempPath(),
                    "MapPerfProbe",
                    "bootstrap.log"),
                line))
                return;

            var local = SafeFolder(Environment.SpecialFolder.LocalApplicationData);
            if (TryAppend(Path.Combine(local, "MapPerfProbe", "bootstrap.log"), line))
                return;

            TryAppend(Path.Combine(
                SafeBaseDirectory(),
                "MapPerfProbe-bootstrap.log"),
                line);
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

        private static string SafeAssemblyLocation()
        {
            try { return typeof(BootstrapSubModule).Assembly.Location; }
            catch { return "<unknown>"; }
        }
    }
}
