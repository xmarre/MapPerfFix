using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

[assembly: AssemblyVersion("7.6.8.2")]
[assembly: AssemblyFileVersion("7.6.8.2")]

namespace PlayerSettlementStartupDiagnostics
{
    public sealed class SubModule : MBSubModuleBase
    {
        private const string HarmonyId = "playersettlement.startup.diagnostics.7.6.8.2";
        private const string PlayerSettlementAssemblyName = "PlayerSettlement";
        private const string ButterLibAssemblyName = "Bannerlord.ButterLib";
        private static readonly object InstallSync = new object();
        private static readonly HashSet<MethodBase> PatchedMethods = new HashSet<MethodBase>();

        private static Harmony _harmony;
        private static bool _playerSettlementTargetsInstalled;
        private static bool _hotKeyTargetsInstalled;
        private static bool _assemblyLoadSubscribed;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            DiagnosticLog.Initialize();
            DiagnosticLog.Write("Diagnostic submodule load: begin");

            lock (InstallSync)
            {
                if (_harmony == null)
                    _harmony = new Harmony(HarmonyId);

                TryInstallAvailableTargets();

                if ((!_playerSettlementTargetsInstalled || !_hotKeyTargetsInstalled) && !_assemblyLoadSubscribed)
                {
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    _assemblyLoadSubscribed = true;
                    DiagnosticLog.Write("Assembly-load retry subscribed because one or more target assemblies are not available yet.");
                }
            }

            DiagnosticLog.Write(
                "Diagnostic submodule load: complete; PlayerSettlementTargets=" +
                _playerSettlementTargetsInstalled + "; HotKeyTargets=" + _hotKeyTargetsInstalled);
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            Assembly assembly = args != null ? args.LoadedAssembly : null;
            string name = SafeAssemblyName(assembly);
            if (!string.Equals(name, PlayerSettlementAssemblyName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, ButterLibAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (InstallSync)
            {
                DiagnosticLog.Write("Target assembly loaded; retrying diagnostics installation: " + name);
                TryInstallAvailableTargets();
                if (_playerSettlementTargetsInstalled && _hotKeyTargetsInstalled && _assemblyLoadSubscribed)
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                    _assemblyLoadSubscribed = false;
                    DiagnosticLog.Write("All diagnostics targets installed; assembly-load retry unsubscribed.");
                }
            }
        }

        private static void TryInstallAvailableTargets()
        {
            if (!_playerSettlementTargetsInstalled)
            {
                Assembly playerSettlement = FindLoadedAssembly(PlayerSettlementAssemblyName);
                if (playerSettlement != null)
                    _playerSettlementTargetsInstalled = InstallPlayerSettlementTargets(playerSettlement);
                else
                    DiagnosticLog.Write("PlayerSettlement assembly is not loaded yet.");
            }

            if (!_hotKeyTargetsInstalled)
            {
                Assembly butterLib = FindLoadedAssembly(ButterLibAssemblyName);
                if (butterLib != null)
                    _hotKeyTargetsInstalled = InstallHotKeyTargets(butterLib);
                else
                    DiagnosticLog.Write("Bannerlord.ButterLib assembly is not loaded yet.");
            }
        }

        private static bool InstallPlayerSettlementTargets(Assembly assembly)
        {
            bool success = true;
            int installed = 0;

            try
            {
                Type mainType = assembly.GetType("BannerlordPlayerSettlement.Main", false);
                if (mainType == null)
                {
                    DiagnosticLog.Write("INSTALL FAILED: BannerlordPlayerSettlement.Main was not found.");
                    success = false;
                }
                else
                {
                    MethodInfo initialRoot = mainType.GetMethod(
                        "OnBeforeInitialModuleScreenSetAsRoot",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (PatchInstanceBoundary(initialRoot, "Initial module-screen setup"))
                        installed++;
                    else
                        success = false;

                    MethodInfo gatherTemplates = mainType.GetMethod(
                        "GatherTemplates",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (PatchInstanceBoundary(gatherTemplates, "Template gathering"))
                        installed++;
                    else
                        success = false;
                }

                Type compatibilityInterface = assembly.GetType(
                    "BannerlordPlayerSettlement.Patches.Compatibility.Interfaces.ICompatibilityPatch",
                    false);
                if (compatibilityInterface == null)
                {
                    DiagnosticLog.Write("INSTALL FAILED: ICompatibilityPatch was not found.");
                    success = false;
                }
                else
                {
                    List<Type> implementations = GetLoadableTypes(assembly)
                        .Where(type => type != null &&
                                       !type.IsAbstract &&
                                       !type.IsInterface &&
                                       compatibilityInterface.IsAssignableFrom(type))
                        .OrderBy(type => type.FullName, StringComparer.Ordinal)
                        .ToList();

                    if (implementations.Count == 0)
                    {
                        DiagnosticLog.Write("INSTALL FAILED: no concrete compatibility-patch implementations were found.");
                        success = false;
                    }

                    foreach (Type implementation in implementations)
                    {
                        MethodInfo method = implementation.GetMethod(
                            "PatchAfterMenus",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        string label = "Post-menu compatibility initializer: " + implementation.FullName;
                        if (PatchInstanceBoundary(method, label))
                            installed++;
                        else
                            success = false;
                    }
                }
            }
            catch (Exception ex)
            {
                success = false;
                DiagnosticLog.Write("INSTALL FAILED while discovering Player Settlement targets:\r\n" + ex);
            }

            DiagnosticLog.Write(
                "Player Settlement diagnostics installation finished: installed=" + installed + "; success=" + success);
            return success;
        }

        private static bool InstallHotKeyTargets(Assembly assembly)
        {
            bool success = true;
            int installed = 0;

            try
            {
                Type managerType = assembly.GetType("Bannerlord.ButterLib.HotKeys.HotKeyManager", false);
                if (managerType == null)
                {
                    DiagnosticLog.Write("INSTALL FAILED: Bannerlord.ButterLib.HotKeys.HotKeyManager was not found.");
                    return false;
                }

                List<MethodInfo> createMethods = managerType
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => string.Equals(
                        method.Name,
                        "CreateWithOwnCategory",
                        StringComparison.Ordinal))
                    .OrderBy(method => FormatMethod(method), StringComparer.Ordinal)
                    .ToList();

                if (createMethods.Count == 0)
                {
                    DiagnosticLog.Write("INSTALL FAILED: HotKeyManager.CreateWithOwnCategory was not found.");
                    success = false;
                }

                foreach (MethodInfo method in createMethods)
                {
                    if (PatchStaticBoundary(method, "Hotkey manager creation"))
                        installed++;
                    else
                        success = false;
                }

                List<MethodInfo> buildMethods = managerType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => string.Equals(method.Name, "Build", StringComparison.Ordinal) &&
                                     !method.IsAbstract)
                    .OrderBy(method => FormatMethod(method), StringComparer.Ordinal)
                    .ToList();

                if (buildMethods.Count == 0)
                {
                    DiagnosticLog.Write("INSTALL FAILED: HotKeyManager.Build was not found.");
                    success = false;
                }

                foreach (MethodInfo method in buildMethods)
                {
                    if (PatchInstanceBoundary(method, "Hotkey manager build"))
                        installed++;
                    else
                        success = false;
                }
            }
            catch (Exception ex)
            {
                success = false;
                DiagnosticLog.Write("INSTALL FAILED while discovering ButterLib hotkey targets:\r\n" + ex);
            }

            DiagnosticLog.Write(
                "Hotkey diagnostics installation finished: installed=" + installed + "; success=" + success);
            return success;
        }

        private static bool PatchInstanceBoundary(MethodInfo target, string label)
        {
            if (target == null)
            {
                DiagnosticLog.Write("INSTALL FAILED: missing instance target for " + label + ".");
                return false;
            }

            if (PatchedMethods.Contains(target))
                return true;

            try
            {
                _harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(typeof(SubModule), nameof(InstanceBoundaryPrefix)),
                    postfix: new HarmonyMethod(typeof(SubModule), nameof(InstanceBoundaryPostfix)),
                    finalizer: new HarmonyMethod(typeof(SubModule), nameof(InstanceBoundaryFinalizer)));
                PatchedMethods.Add(target);
                DiagnosticLog.Write("Installed boundary: " + label + " => " + FormatMethod(target));
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(
                    "INSTALL FAILED: " + label + " => " + FormatMethod(target) + "\r\n" + ex);
                return false;
            }
        }

        private static bool PatchStaticBoundary(MethodInfo target, string label)
        {
            if (target == null)
            {
                DiagnosticLog.Write("INSTALL FAILED: missing static target for " + label + ".");
                return false;
            }

            if (PatchedMethods.Contains(target))
                return true;

            try
            {
                _harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(typeof(SubModule), nameof(StaticBoundaryPrefix)),
                    postfix: new HarmonyMethod(typeof(SubModule), nameof(StaticBoundaryPostfix)),
                    finalizer: new HarmonyMethod(typeof(SubModule), nameof(StaticBoundaryFinalizer)));
                PatchedMethods.Add(target);
                DiagnosticLog.Write("Installed boundary: " + label + " => " + FormatMethod(target));
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(
                    "INSTALL FAILED: " + label + " => " + FormatMethod(target) + "\r\n" + ex);
                return false;
            }
        }

        private static void InstanceBoundaryPrefix(
            MethodBase __originalMethod,
            object __instance,
            object[] __args,
            out BoundaryState __state)
        {
            __state = BoundaryState.Start(
                DescribeOperation(__originalMethod, __instance, __args),
                __originalMethod);
            DiagnosticLog.Write("BEGIN " + __state.Operation);
        }

        private static void InstanceBoundaryPostfix(BoundaryState __state)
        {
            if (__state == null)
                return;
            DiagnosticLog.Write("COMPLETE " + __state.Operation + "; elapsed_ms=" + __state.ElapsedMilliseconds);
        }

        private static Exception InstanceBoundaryFinalizer(Exception __exception, BoundaryState __state)
        {
            if (__exception != null)
            {
                string operation = __state != null ? __state.Operation : "unknown instance operation";
                string elapsed = __state != null ? __state.ElapsedMilliseconds : "unknown";
                DiagnosticLog.Write(
                    "FAILED " + operation + "; elapsed_ms=" + elapsed + "\r\n" + __exception);
            }
            return __exception;
        }

        private static void StaticBoundaryPrefix(
            MethodBase __originalMethod,
            object[] __args,
            out BoundaryState __state)
        {
            __state = BoundaryState.Start(
                DescribeOperation(__originalMethod, null, __args),
                __originalMethod);
            DiagnosticLog.Write("BEGIN " + __state.Operation);
        }

        private static void StaticBoundaryPostfix(BoundaryState __state)
        {
            if (__state == null)
                return;
            DiagnosticLog.Write("COMPLETE " + __state.Operation + "; elapsed_ms=" + __state.ElapsedMilliseconds);
        }

        private static Exception StaticBoundaryFinalizer(Exception __exception, BoundaryState __state)
        {
            if (__exception != null)
            {
                string operation = __state != null ? __state.Operation : "unknown static operation";
                string elapsed = __state != null ? __state.ElapsedMilliseconds : "unknown";
                DiagnosticLog.Write(
                    "FAILED " + operation + "; elapsed_ms=" + elapsed + "\r\n" + __exception);
            }
            return __exception;
        }

        private static string DescribeOperation(MethodBase method, object instance, object[] args)
        {
            string declaringType = method != null && method.DeclaringType != null
                ? method.DeclaringType.FullName
                : "<unknown-type>";
            string methodName = method != null ? method.Name : "<unknown-method>";

            if (string.Equals(methodName, "PatchAfterMenus", StringComparison.Ordinal))
                return "Post-menu compatibility initializer [" + declaringType + "]";

            if (string.Equals(methodName, "GatherTemplates", StringComparison.Ordinal))
                return "Template gathering [" + declaringType + "]";

            if (string.Equals(methodName, "OnBeforeInitialModuleScreenSetAsRoot", StringComparison.Ordinal))
                return "Initial module-screen setup [" + declaringType + "]";

            if (string.Equals(methodName, "CreateWithOwnCategory", StringComparison.Ordinal))
            {
                return "Hotkey manager creation [" + declaringType + "]" + DescribeStringArguments(args);
            }

            if (string.Equals(methodName, "Build", StringComparison.Ordinal))
            {
                return "Hotkey manager build [" + declaringType + "]" + DescribeHotKeyManager(instance);
            }

            return FormatMethod(method) + DescribeStringArguments(args);
        }

        private static string DescribeStringArguments(object[] args)
        {
            if (args == null || args.Length == 0)
                return string.Empty;

            var values = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string value = args[i] as string;
                if (value != null)
                    values.Add("arg" + i + "='" + Limit(value, 160) + "'");
            }

            return values.Count == 0 ? string.Empty : "; " + string.Join(", ", values);
        }

        private static string DescribeHotKeyManager(object instance)
        {
            if (instance == null)
                return "; instance=<null>";

            string category = TryReadStringMember(
                instance,
                "Category",
                "CategoryId",
                "CategoryName",
                "Name",
                "Id");
            return string.IsNullOrEmpty(category)
                ? "; instance_type=" + instance.GetType().FullName
                : "; category='" + Limit(category, 160) + "'";
        }

        private static string TryReadStringMember(object instance, params string[] names)
        {
            Type type = instance.GetType();
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = type.GetProperty(
                        name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property != null && property.GetIndexParameters().Length == 0 &&
                        property.PropertyType == typeof(string))
                    {
                        return property.GetValue(instance, null) as string;
                    }
                }
                catch
                {
                }

                try
                {
                    FieldInfo field = type.GetField(
                        name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null && field.FieldType == typeof(string))
                        return field.GetValue(instance) as string;
                }
                catch
                {
                }
            }
            return null;
        }

        private static string FormatMethod(MethodBase method)
        {
            if (method == null)
                return "<null-method>";

            try
            {
                string declaringType = method.DeclaringType != null
                    ? method.DeclaringType.FullName
                    : "<unknown-type>";
                string parameters = string.Join(
                    ", ",
                    method.GetParameters().Select(parameter => parameter.ParameterType.FullName));
                return declaringType + "." + method.Name + "(" + parameters + ")";
            }
            catch
            {
                return method.Name;
            }
        }

        private static List<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes().Where(type => type != null).ToList();
            }
            catch (ReflectionTypeLoadException ex)
            {
                DiagnosticLog.Write("Partial type-load failure while discovering targets:\r\n" + ex);
                return ex.Types.Where(type => type != null).ToList();
            }
        }

        private static Assembly FindLoadedAssembly(string simpleName)
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                assembly => string.Equals(
                    SafeAssemblyName(assembly),
                    simpleName,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeAssemblyName(Assembly assembly)
        {
            try
            {
                return assembly != null ? assembly.GetName().Name : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Limit(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value ?? string.Empty;
            return value.Substring(0, maxLength) + "...";
        }

        private sealed class BoundaryState
        {
            private readonly long _startedTimestamp;

            private BoundaryState(string operation, MethodBase method)
            {
                Operation = operation + "; method=" + FormatMethod(method);
                _startedTimestamp = Stopwatch.GetTimestamp();
            }

            public string Operation { get; private set; }

            public string ElapsedMilliseconds
            {
                get
                {
                    long elapsed = Stopwatch.GetTimestamp() - _startedTimestamp;
                    double milliseconds = elapsed * 1000.0 / Stopwatch.Frequency;
                    return milliseconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            public static BoundaryState Start(string operation, MethodBase method)
            {
                return new BoundaryState(operation, method);
            }
        }

        private static class DiagnosticLog
        {
            private static readonly object Sync = new object();
            private static string _path;
            private static bool _initialized;

            public static void Initialize()
            {
                lock (Sync)
                {
                    if (_initialized)
                        return;

                    _path = ResolveWritablePath();
                    string header =
                        "Player Settlement startup diagnostics 7.6.8.2\r\n" +
                        "StartedUtc=" + DateTime.UtcNow.ToString("O") + "\r\n" +
                        "Process=" + SafeProcessName() + "\r\n" +
                        "Assembly=" + typeof(SubModule).Assembly.FullName + "\r\n" +
                        "AssemblyPath=" + SafeAssemblyLocation() + "\r\n" +
                        "LogPath=" + _path + "\r\n" +
                        new string('=', 96) + "\r\n";

                    try
                    {
                        File.WriteAllText(_path, header, new UTF8Encoding(false));
                    }
                    catch
                    {
                        _path = Path.Combine(
                            Path.GetTempPath(),
                            "PlayerSettlementStartupDiagnostics.log");
                        File.WriteAllText(_path, header, new UTF8Encoding(false));
                    }

                    _initialized = true;
                }
            }

            public static void Write(string message)
            {
                try
                {
                    if (!_initialized)
                        Initialize();

                    string line =
                        DateTime.UtcNow.ToString("O") +
                        " | thread=" + Thread.CurrentThread.ManagedThreadId +
                        " | " + (message ?? string.Empty) + "\r\n";
                    lock (Sync)
                    {
                        File.AppendAllText(_path, line, new UTF8Encoding(false));
                    }
                }
                catch
                {
                    // Diagnostics must never alter Player Settlement startup behavior.
                }
            }

            private static string ResolveWritablePath()
            {
                string assemblyDirectory = null;
                try
                {
                    assemblyDirectory = Path.GetDirectoryName(SafeAssemblyLocation());
                }
                catch
                {
                }

                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(assemblyDirectory))
                {
                    try
                    {
                        DirectoryInfo binDirectory = Directory.GetParent(assemblyDirectory);
                        DirectoryInfo moduleDirectory = binDirectory != null ? binDirectory.Parent : null;
                        if (moduleDirectory != null)
                        {
                            candidates.Add(Path.Combine(
                                moduleDirectory.FullName,
                                "PlayerSettlementStartupDiagnostics.log"));
                        }
                    }
                    catch
                    {
                    }

                    candidates.Add(Path.Combine(
                        assemblyDirectory,
                        "PlayerSettlementStartupDiagnostics.log"));
                }

                candidates.Add(Path.Combine(
                    Path.GetTempPath(),
                    "PlayerSettlementStartupDiagnostics.log"));

                foreach (string candidate in candidates)
                {
                    try
                    {
                        string directory = Path.GetDirectoryName(candidate);
                        if (!string.IsNullOrEmpty(directory))
                            Directory.CreateDirectory(directory);
                        using (FileStream stream = new FileStream(
                                   candidate,
                                   FileMode.Append,
                                   FileAccess.Write,
                                   FileShare.ReadWrite))
                        {
                        }
                        return candidate;
                    }
                    catch
                    {
                    }
                }

                return Path.Combine(Path.GetTempPath(), "PlayerSettlementStartupDiagnostics.log");
            }

            private static string SafeAssemblyLocation()
            {
                try
                {
                    return typeof(SubModule).Assembly.Location ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }

            private static string SafeProcessName()
            {
                try
                {
                    return Process.GetCurrentProcess().ProcessName;
                }
                catch
                {
                    return "<unknown>";
                }
            }
        }
    }
}
