using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace MapPerfProbe
{
    /// <summary>
    /// Campaign-map performance work that preserves the authoritative callback path.
    /// No campaign, AI, event, periodic, save, or UI callback is skipped or deferred.
    /// </summary>
    public sealed class SubModule : MBSubModuleBase
    {
        private const string HarmonyId = "mmq.mapperfprobe.safe";
        private const int FirstProbeFrame = 60;
        private const int ProbeRetryFrames = 120;
        private const int CompatibilityCheckFrames = 600;
        private const int MaxProbeAttempts = 10;

        private static readonly object PatchSync = new object();
        private static Harmony _harmony;
        private static int _frameSequence;
        private static int _nextProbeFrame = FirstProbeFrame;
        private static int _nextCompatibilityFrame;
        private static int _probeAttempts;
        private static int _configurationEnabled = 1;
        private static int _hiddenVisualOptimizationEnabled = 1;
        private static int _visualPrefixEnabled;
        private static bool _visualPatchInstalled;
        private static bool _visualProbeCompleted;
        private static MethodInfo _visualTickMethod;
        private static HiddenPartyVisualAccessors _hiddenVisualAccessors;
        private static bool _statusMessageShown;
        private static bool _gcModeCaptured;
        private static bool _gcModeOwned;
        private static bool _gcTuningFailed;
        private static GCLatencyMode _originalGcMode;
        private static GCLatencyMode _appliedGcMode;
        private static long _visualCalls;
        private static long _hiddenVisualSkips;
        private static long _visualFailOpen;
        private static long _nextVisualReportTimestamp;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            MapPerfLog.DebugEnabled = MapPerfConfig.DebugLogging;
            MapPerfLog.Initialize();
            CaptureGcMode();

            try
            {
                _harmony = new Harmony(HarmonyId);
                MapPerfLog.Info("MapPerfProbe 2.1.0 loaded. Authoritative campaign callbacks remain synchronous.");
            }
            catch (Exception exception)
            {
                MapPerfLog.Error("Harmony initialization failed", exception);
                _harmony = null;
            }
        }

        protected override void OnSubModuleUnloaded()
        {
            Volatile.Write(ref _visualPrefixEnabled, 0);

            try
            {
                if (_harmony != null)
                    _harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception exception)
            {
                MapPerfLog.Error("Harmony unpatch failed", exception);
            }

            RestoreGcMode();
            TorCallbackProfiler.Reset();
            MapPerfLog.Info("MapPerfProbe stopped.");
            base.OnSubModuleUnloaded();
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            var frame = Interlocked.Increment(ref _frameSequence);
            if (frame <= 0 || frame > 1_000_000_000)
            {
                Interlocked.Exchange(ref _frameSequence, 1);
                frame = 1;
            }

            RefreshConfiguration();

            if (frame >= Volatile.Read(ref _nextProbeFrame) &&
                (!_visualProbeCompleted || !TorCallbackProfiler.InstallationComplete))
            {
                Volatile.Write(ref _nextProbeFrame, frame + ProbeRetryFrames);
                ProbeRuntimeTargets();
            }

            if (_visualPatchInstalled && frame >= Volatile.Read(ref _nextCompatibilityFrame))
            {
                Volatile.Write(ref _nextCompatibilityFrame, frame + CompatibilityCheckFrames);
                DisableVisualPatchIfCompatibilityChanged();
            }

            UpdateGcMode();
            TorCallbackProfiler.ReportIfDue();
            ReportVisualStatsIfDue();

            if (!_statusMessageShown && frame >= FirstProbeFrame && IsCampaignMapActive())
            {
                _statusMessageShown = true;
                TryShowStatusMessage();
            }
        }

        private static void RefreshConfiguration()
        {
            MapPerfLog.DebugEnabled = MapPerfConfig.DebugLogging;
            Volatile.Write(ref _configurationEnabled, MapPerfConfig.Enabled ? 1 : 0);
            Volatile.Write(
                ref _hiddenVisualOptimizationEnabled,
                MapPerfConfig.OptimizeHiddenPartyVisuals ? 1 : 0);

            TorCallbackProfiler.Configure(
                MapPerfConfig.Enabled && MapPerfConfig.ProfileTorCampaignCallbacks,
                MapPerfConfig.SlowCallbackThresholdMs,
                MapPerfConfig.ProfilerReportIntervalSeconds);
        }

        private static void ProbeRuntimeTargets()
        {
            if (_harmony == null)
                return;

            lock (PatchSync)
            {
                if (!_visualProbeCompleted)
                    TryInstallHiddenPartyVisualPatch();

                if (!TorCallbackProfiler.InstallationComplete)
                    TorCallbackProfiler.TryInstall(_harmony);

                _probeAttempts++;
                if (_probeAttempts >= MaxProbeAttempts && !_visualProbeCompleted)
                {
                    _visualProbeCompleted = true;
                    MapPerfLog.Warn(
                        "No supported legacy SandBox.View.Map.PartyVisual tick was found after " +
                        MaxProbeAttempts + " probes. Hidden-party visual optimization is inactive.");
                }
            }
        }

        private static void TryInstallHiddenPartyVisualPatch()
        {
            var legacyType = AccessTools.TypeByName("SandBox.View.Map.PartyVisual");
            if (legacyType == null)
            {
                var modernType = AccessTools.TypeByName(
                    "SandBox.View.Map.Visuals.MobilePartyVisual");
                if (modernType != null)
                {
                    _visualProbeCompleted = true;
                    MapPerfLog.Info(
                        "Detected the modern MobilePartyVisual implementation. Bannerlord already gates " +
                        "hidden AgentVisuals in this code path; no redundant visual skip was installed.");
                }
                else
                {
                    MapPerfLog.Debug("SandBox party visual types are not loaded yet.");
                }
                return;
            }

            var method = FindLegacyPartyVisualTick(legacyType);
            if (method == null)
            {
                _visualProbeCompleted = true;
                MapPerfLog.Warn(
                    "Legacy PartyVisual was found, but its Tick signature is unsupported. " +
                    "The visual optimization was not installed.");
                return;
            }

            var accessors = HiddenPartyVisualAccessors.Create(legacyType);
            if (!accessors.IsSupported)
            {
                _visualProbeCompleted = true;
                MapPerfLog.Warn(
                    "Legacy PartyVisual optimization is unavailable: " + accessors.UnsupportedReason);
                return;
            }

            if (HasForeignPatches(method, out var owners))
            {
                _visualProbeCompleted = true;
                MapPerfLog.Warn(
                    "PartyVisual.Tick is already patched by: " + owners +
                    ". The hidden-party optimization was not installed.");
                return;
            }

            try
            {
                var prefixMethod = typeof(SubModule).GetMethod(
                    nameof(HiddenPartyVisualPrefix),
                    BindingFlags.Public | BindingFlags.Static);
                if (prefixMethod == null)
                    throw new MissingMethodException(nameof(HiddenPartyVisualPrefix));

                _harmony.Patch(method, prefix: new HarmonyMethod(prefixMethod));
                _hiddenVisualAccessors = accessors;
                _visualTickMethod = method;
                _visualPatchInstalled = true;
                _visualProbeCompleted = true;
                Volatile.Write(ref _visualPrefixEnabled, 1);
                _nextCompatibilityFrame =
                    Volatile.Read(ref _frameSequence) + CompatibilityCheckFrames;
                _nextVisualReportTimestamp =
                    Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30L;

                MapPerfLog.Info(
                    "Installed legacy hidden mobile-party visual optimization on " +
                    DescribeMethod(method) + ". Fully hidden parties resume immediately when visible.");
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _visualPrefixEnabled, 0);
                _visualProbeCompleted = true;
                MapPerfLog.Error("PartyVisual.Tick patch failed", exception);
            }
        }

        private static MethodInfo FindLegacyPartyVisualTick(Type type)
        {
            MethodInfo singleFloatFallback = null;
            var methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);

            for (var index = 0; index < methods.Length; index++)
            {
                var method = methods[index];
                if (!string.Equals(method.Name, "Tick", StringComparison.Ordinal) ||
                    method.IsStatic || method.IsAbstract || method.ContainsGenericParameters ||
                    method.ReturnType != typeof(void) || method.GetMethodBody() == null)
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(float) &&
                    parameters[1].ParameterType == typeof(int).MakeByRefType())
                {
                    var arrayByRef = parameters[2].ParameterType;
                    var arrayType = arrayByRef.IsByRef ? arrayByRef.GetElementType() : null;
                    if (arrayType != null && arrayType.IsArray &&
                        arrayType.GetElementType() == type)
                        return method;
                }

                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(float))
                    singleFloatFallback = method;
            }

            return singleFloatFallback;
        }

        [HarmonyPriority(Priority.Last)]
        public static bool HiddenPartyVisualPrefix(object __instance)
        {
            Interlocked.Increment(ref _visualCalls);

            if (Volatile.Read(ref _visualPrefixEnabled) == 0 ||
                Volatile.Read(ref _configurationEnabled) == 0 ||
                Volatile.Read(ref _hiddenVisualOptimizationEnabled) == 0 ||
                __instance == null)
                return true;

            try
            {
                var accessors = _hiddenVisualAccessors;
                if (accessors == null || !accessors.ShouldSkip(__instance))
                    return true;

                Interlocked.Increment(ref _hiddenVisualSkips);
                return false;
            }
            catch
            {
                Interlocked.Increment(ref _visualFailOpen);
                return true;
            }
        }

        private static void DisableVisualPatchIfCompatibilityChanged()
        {
            var method = _visualTickMethod;
            if (method == null || _harmony == null)
                return;

            if (!HasForeignPatches(method, out var owners))
                return;

            lock (PatchSync)
            {
                if (!_visualPatchInstalled || _visualTickMethod == null || _harmony == null)
                    return;

                Volatile.Write(ref _visualPrefixEnabled, 0);
                try
                {
                    _harmony.Unpatch(_visualTickMethod, HarmonyPatchType.Prefix, HarmonyId);
                }
                catch (Exception exception)
                {
                    MapPerfLog.Error(
                        "Could not remove the PartyVisual optimization after a compatibility change",
                        exception);
                }
                finally
                {
                    _visualTickMethod = null;
                    _hiddenVisualAccessors = null;
                    _visualPatchInstalled = false;
                }

                MapPerfLog.Warn(
                    "PartyVisual.Tick gained a foreign patch from " + owners +
                    "; the visual optimization was disabled and now fails open.");
            }
        }

        private static bool HasForeignPatches(MethodBase method, out string owners)
        {
            owners = string.Empty;
            try
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null)
                    return false;

                var foreignOwners = new HashSet<string>(StringComparer.Ordinal);
                CollectForeignOwners(info.Prefixes, foreignOwners);
                CollectForeignOwners(info.Postfixes, foreignOwners);
                CollectForeignOwners(info.Transpilers, foreignOwners);
                CollectForeignOwners(info.Finalizers, foreignOwners);
                if (foreignOwners.Count == 0)
                    return false;

                owners = string.Join(", ", foreignOwners.OrderBy(value => value));
                return true;
            }
            catch (Exception exception)
            {
                owners = "unknown (patch inspection failed: " + exception.Message + ")";
                return true;
            }
        }

        private static void CollectForeignOwners(
            IEnumerable<Patch> patches,
            ISet<string> owners)
        {
            if (patches == null)
                return;

            foreach (var patch in patches)
            {
                if (patch == null || string.Equals(patch.owner, HarmonyId, StringComparison.Ordinal))
                    continue;
                owners.Add(string.IsNullOrEmpty(patch.owner) ? "<unknown>" : patch.owner);
            }
        }

        private static void ReportVisualStatsIfDue()
        {
            if (!_visualPatchInstalled)
                return;

            var now = Stopwatch.GetTimestamp();
            var due = Volatile.Read(ref _nextVisualReportTimestamp);
            if (due != 0 && now < due)
                return;

            Volatile.Write(
                ref _nextVisualReportTimestamp,
                now + Stopwatch.Frequency * MapPerfConfig.ProfilerReportIntervalSeconds);

            var calls = Interlocked.Exchange(ref _visualCalls, 0);
            var skips = Interlocked.Exchange(ref _hiddenVisualSkips, 0);
            var failOpen = Interlocked.Exchange(ref _visualFailOpen, 0);
            if (calls == 0)
                return;

            MapPerfLog.Info(
                "Visual interval: calls=" + calls +
                ", fully-hidden-skips=" + skips +
                ", fail-open=" + failOpen +
                ", skip-rate=" + ((double)skips * 100.0 / calls).ToString("F1") + "%.");
        }

        private static string DescribeMethod(MethodBase method)
        {
            return method.DeclaringType.FullName + "." + method.Name + "(" +
                   string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";
        }

        private static void CaptureGcMode()
        {
            if (_gcModeCaptured)
                return;

            try
            {
                _originalGcMode = GCSettings.LatencyMode;
                _appliedGcMode = _originalGcMode;
                _gcModeCaptured = true;
            }
            catch (Exception exception)
            {
                _gcTuningFailed = true;
                MapPerfLog.Warn("GC latency mode is unavailable: " + exception.Message);
            }
        }

        private static void UpdateGcMode()
        {
            if (_gcTuningFailed)
                return;
            if (!_gcModeCaptured)
                CaptureGcMode();
            if (!_gcModeCaptured)
                return;

            if (Volatile.Read(ref _configurationEnabled) == 0 ||
                !MapPerfConfig.TuneGcLatency || !IsCampaignMapActive())
            {
                RestoreGcMode();
                return;
            }

            var desired = IsPaused()
                ? GCLatencyMode.Interactive
                : GCLatencyMode.SustainedLowLatency;
            if (_gcModeOwned && _appliedGcMode == desired && GCSettings.LatencyMode == desired)
                return;

            try
            {
                GCSettings.LatencyMode = desired;
                _appliedGcMode = desired;
                _gcModeOwned = true;
            }
            catch (Exception exception)
            {
                _gcTuningFailed = true;
                MapPerfLog.Warn("Disabling GC latency tuning: " + exception.Message);
                RestoreGcMode();
            }
        }

        private static void RestoreGcMode()
        {
            if (!_gcModeCaptured || !_gcModeOwned)
                return;

            try
            {
                GCSettings.LatencyMode = _originalGcMode;
                _appliedGcMode = _originalGcMode;
            }
            catch
            {
            }
            finally
            {
                _gcModeOwned = false;
            }
        }

        private static Type _gameStateManagerType;
        private static PropertyInfo _gameStateManagerCurrent;
        private static PropertyInfo _activeStateProperty;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsCampaignMapActive()
        {
            try
            {
                if (_gameStateManagerType == null)
                    _gameStateManagerType = Type.GetType(
                        "TaleWorlds.Core.GameStateManager, TaleWorlds.Core", false);
                if (_gameStateManagerType == null)
                    return false;

                if (_gameStateManagerCurrent == null)
                    _gameStateManagerCurrent = _gameStateManagerType.GetProperty(
                        "Current", BindingFlags.Static | BindingFlags.Public);
                var manager = _gameStateManagerCurrent?.GetValue(null, null);
                if (manager == null)
                    return false;

                if (_activeStateProperty == null ||
                    !_activeStateProperty.DeclaringType.IsInstanceOfType(manager))
                    _activeStateProperty = manager.GetType().GetProperty(
                        "ActiveState", BindingFlags.Instance | BindingFlags.Public);

                var state = _activeStateProperty?.GetValue(manager, null);
                return string.Equals(
                    state?.GetType().FullName,
                    "TaleWorlds.CampaignSystem.GameState.MapState",
                    StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPaused()
        {
            try
            {
                var campaign = Campaign.Current;
                return campaign != null &&
                       campaign.TimeControlMode == CampaignTimeControlMode.Stop;
            }
            catch
            {
                return false;
            }
        }

        private static void TryShowStatusMessage()
        {
            try
            {
                var messageType = Type.GetType(
                    "TaleWorlds.Core.InformationMessage, TaleWorlds.Core", false);
                var managerType = Type.GetType(
                    "TaleWorlds.Core.InformationManager, TaleWorlds.Core", false);
                if (messageType == null || managerType == null)
                    return;

                var constructor = messageType.GetConstructor(new[] { typeof(string) });
                if (constructor == null)
                    return;

                var display = managerType.GetMethod(
                    "DisplayMessage",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { messageType },
                    null);
                if (display == null)
                    return;

                var message = constructor.Invoke(new object[]
                {
                    "MapPerfProbe loaded. Log: " + MapPerfLog.CurrentPath
                });
                display.Invoke(null, new[] { message });
            }
            catch (Exception exception)
            {
                MapPerfLog.Debug("Could not display startup status: " + exception.Message);
            }
        }

        private sealed class HiddenPartyVisualAccessors
        {
            private readonly Func<object, float> _alpha;
            private readonly Func<object, object> _party;
            private readonly Func<object, bool> _isSettlement;
            private readonly Func<object, bool> _isVisible;
            private readonly Func<object, bool> _levelMaskIsDirty;

            internal bool IsSupported => string.IsNullOrEmpty(UnsupportedReason);
            internal string UnsupportedReason { get; }

            private HiddenPartyVisualAccessors(
                Func<object, float> alpha,
                Func<object, object> party,
                Func<object, bool> isSettlement,
                Func<object, bool> isVisible,
                Func<object, bool> levelMaskIsDirty,
                string unsupportedReason)
            {
                _alpha = alpha;
                _party = party;
                _isSettlement = isSettlement;
                _isVisible = isVisible;
                _levelMaskIsDirty = levelMaskIsDirty;
                UnsupportedReason = unsupportedReason;
            }

            internal bool ShouldSkip(object visual)
            {
                var party = _party(visual);
                if (party == null)
                    return false;
                if (_isSettlement(party) || _isVisible(party) || _levelMaskIsDirty(party))
                    return false;

                var alpha = _alpha(visual);
                return !float.IsNaN(alpha) && alpha <= 0.0001f;
            }

            internal static HiddenPartyVisualAccessors Create(Type visualType)
            {
                try
                {
                    var alphaField = FindField(visualType, "_entityAlpha", typeof(float));
                    var partyField = FindField(visualType, "PartyBase", null);
                    if (alphaField == null)
                        return Unsupported("_entityAlpha field was not found");
                    if (partyField == null)
                        return Unsupported("PartyBase field was not found");

                    var partyType = partyField.FieldType;
                    var isSettlement = FindProperty(partyType, "IsSettlement", typeof(bool));
                    var isVisible = FindProperty(partyType, "IsVisible", typeof(bool));
                    var levelMaskIsDirty = FindProperty(
                        partyType, "LevelMaskIsDirty", typeof(bool));
                    if (isSettlement == null)
                        return Unsupported("PartyBase.IsSettlement was not found");
                    if (isVisible == null)
                        return Unsupported("PartyBase.IsVisible was not found");
                    if (levelMaskIsDirty == null)
                        return Unsupported("PartyBase.LevelMaskIsDirty was not found");

                    return new HiddenPartyVisualAccessors(
                        CompileFieldGetter<float>(visualType, alphaField),
                        CompileFieldGetter<object>(visualType, partyField),
                        CompilePropertyGetter<bool>(partyType, isSettlement),
                        CompilePropertyGetter<bool>(partyType, isVisible),
                        CompilePropertyGetter<bool>(partyType, levelMaskIsDirty),
                        null);
                }
                catch (Exception exception)
                {
                    return Unsupported(exception.GetType().Name + ": " + exception.Message);
                }
            }

            private static HiddenPartyVisualAccessors Unsupported(string reason)
            {
                return new HiddenPartyVisualAccessors(
                    null, null, null, null, null, reason);
            }

            private static FieldInfo FindField(Type type, string name, Type fieldType)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                for (var current = type; current != null; current = current.BaseType)
                {
                    var field = current.GetField(name, flags);
                    if (field != null && (fieldType == null || field.FieldType == fieldType))
                        return field;
                }
                return null;
            }

            private static PropertyInfo FindProperty(Type type, string name, Type propertyType)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                for (var current = type; current != null; current = current.BaseType)
                {
                    var property = current.GetProperty(name, flags);
                    if (property != null && property.PropertyType == propertyType &&
                        property.GetGetMethod(true) != null)
                        return property;
                }
                return null;
            }

            private static Func<object, T> CompileFieldGetter<T>(
                Type declaringType,
                FieldInfo field)
            {
                var instance = Expression.Parameter(typeof(object), "instance");
                var cast = Expression.Convert(instance, declaringType);
                var access = Expression.Field(cast, field);
                var convert = Expression.Convert(access, typeof(T));
                return Expression.Lambda<Func<object, T>>(convert, instance).Compile();
            }

            private static Func<object, T> CompilePropertyGetter<T>(
                Type declaringType,
                PropertyInfo property)
            {
                var instance = Expression.Parameter(typeof(object), "instance");
                var cast = Expression.Convert(instance, declaringType);
                var access = Expression.Property(cast, property);
                var convert = Expression.Convert(access, typeof(T));
                return Expression.Lambda<Func<object, T>>(convert, instance).Compile();
            }
        }

        private static class TorCallbackProfiler
        {
            private static readonly HashSet<string> TargetMethodNames =
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "DailyCareerTickEvents",
                    "OnDailyTick",
                    "DailyTickClan",
                    "DailyTickParty",
                    "DailyTickEvents",
                    "OnAiTick",
                    "OnSettlementHourlyTick",
                    "HourlyTick",
                    "OnTick",
                    "wait_on_tick",
                    "HourlyPartyTick",
                    "raisingdeadtick",
                    "DailyTickSettlement",
                    "Tick"
                };

            private static readonly ConcurrentDictionary<MethodBase, ProfileSample> Samples =
                new ConcurrentDictionary<MethodBase, ProfileSample>();
            private static int _enabled = 1;
            private static long _slowThresholdTicks = Stopwatch.Frequency * 8L / 1000L;
            private static long _reportIntervalTicks = Stopwatch.Frequency * 30L;
            private static long _nextReportTimestamp;
            private static int _installationComplete;
            private static int _installedMethodCount;

            internal static bool InstallationComplete =>
                Volatile.Read(ref _installationComplete) != 0;

            internal static void Configure(bool enabled, int slowThresholdMs, int reportSeconds)
            {
                Volatile.Write(ref _enabled, enabled ? 1 : 0);
                Volatile.Write(
                    ref _slowThresholdTicks,
                    Math.Max(1L, Stopwatch.Frequency * slowThresholdMs / 1000L));
                Volatile.Write(
                    ref _reportIntervalTicks,
                    Math.Max(Stopwatch.Frequency, Stopwatch.Frequency * reportSeconds));
            }

            internal static void TryInstall(Harmony harmony)
            {
                if (InstallationComplete || harmony == null)
                    return;

                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                    candidate => string.Equals(
                        candidate.GetName().Name, "TOR_Core", StringComparison.Ordinal));
                if (assembly == null)
                {
                    MapPerfLog.Debug("TOR_Core is not loaded yet; profiler installation deferred.");
                    return;
                }

                try
                {
                    var prefix = new HarmonyMethod(
                        typeof(TorCallbackProfiler).GetMethod(
                            nameof(ProfilePrefix),
                            BindingFlags.Static | BindingFlags.Public));
                    var postfix = new HarmonyMethod(
                        typeof(TorCallbackProfiler).GetMethod(
                            nameof(ProfilePostfix),
                            BindingFlags.Static | BindingFlags.Public));

                    var count = 0;
                    foreach (var type in GetLoadableTypes(assembly))
                    {
                        var fullName = type.FullName;
                        if (string.IsNullOrEmpty(fullName) ||
                            !fullName.StartsWith("TOR_Core.Campaign", StringComparison.Ordinal))
                            continue;

                        var methods = type.GetMethods(
                            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                            BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        for (var index = 0; index < methods.Length; index++)
                        {
                            var method = methods[index];
                            if (!TargetMethodNames.Contains(method.Name) || method.IsAbstract ||
                                method.ContainsGenericParameters || method.IsSpecialName)
                                continue;

                            var body = method.GetMethodBody();
                            var bytes = body?.GetILAsByteArray();
                            if (bytes == null || bytes.Length < 4)
                                continue;

                            harmony.Patch(method, prefix: prefix, postfix: postfix);
                            count++;
                        }
                    }

                    Volatile.Write(ref _installedMethodCount, count);
                    Volatile.Write(ref _installationComplete, 1);
                    Volatile.Write(
                        ref _nextReportTimestamp,
                        Stopwatch.GetTimestamp() + Volatile.Read(ref _reportIntervalTicks));
                    MapPerfLog.Info(
                        "TOR callback profiler installed on " + count +
                        " campaign methods. It records timing only and never skips callbacks.");
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref _installationComplete, 1);
                    MapPerfLog.Error("TOR callback profiler installation failed", exception);
                }
            }

            public static void ProfilePrefix(out long __state)
            {
                __state = Volatile.Read(ref _enabled) != 0 ? Stopwatch.GetTimestamp() : 0L;
            }

            public static void ProfilePostfix(MethodBase __originalMethod, long __state)
            {
                if (__state == 0L || __originalMethod == null)
                    return;

                var elapsed = Stopwatch.GetTimestamp() - __state;
                if (elapsed < 0L)
                    return;

                var sample = Samples.GetOrAdd(__originalMethod, _ => new ProfileSample());
                Interlocked.Increment(ref sample.Calls);
                Interlocked.Add(ref sample.TotalTicks, elapsed);
                UpdateMaximum(ref sample.MaxTicks, elapsed);

                var threshold = Volatile.Read(ref _slowThresholdTicks);
                if (elapsed < threshold)
                    return;

                Interlocked.Increment(ref sample.SlowCalls);
                var now = Stopwatch.GetTimestamp();
                var previous = Volatile.Read(ref sample.LastSlowLogTimestamp);
                if (now - previous < Stopwatch.Frequency * 5L ||
                    Interlocked.CompareExchange(
                        ref sample.LastSlowLogTimestamp, now, previous) != previous)
                    return;

                MapPerfLog.Warn(
                    "Slow TOR callback: " + DescribeMethod(__originalMethod) + " took " +
                    TicksToMilliseconds(elapsed).ToString("F2") + " ms.");
            }

            internal static void ReportIfDue()
            {
                if (!InstallationComplete || Volatile.Read(ref _enabled) == 0)
                    return;

                var now = Stopwatch.GetTimestamp();
                var due = Volatile.Read(ref _nextReportTimestamp);
                if (due != 0L && now < due)
                    return;

                Volatile.Write(
                    ref _nextReportTimestamp,
                    now + Volatile.Read(ref _reportIntervalTicks));

                var snapshots = new List<ProfileSnapshot>();
                foreach (var pair in Samples)
                {
                    var sample = pair.Value;
                    var calls = Interlocked.Exchange(ref sample.Calls, 0L);
                    var total = Interlocked.Exchange(ref sample.TotalTicks, 0L);
                    var maximum = Interlocked.Exchange(ref sample.MaxTicks, 0L);
                    var slow = Interlocked.Exchange(ref sample.SlowCalls, 0L);
                    if (calls == 0L)
                        continue;
                    snapshots.Add(new ProfileSnapshot(pair.Key, calls, total, maximum, slow));
                }

                if (snapshots.Count == 0)
                    return;

                var top = snapshots
                    .OrderByDescending(snapshot => snapshot.TotalTicks)
                    .ThenByDescending(snapshot => snapshot.MaxTicks)
                    .Take(12)
                    .ToArray();

                MapPerfLog.Info(
                    "TOR callback profile interval; patched=" +
                    Volatile.Read(ref _installedMethodCount) + ", sampled=" + snapshots.Count + ".");
                for (var index = 0; index < top.Length; index++)
                {
                    var snapshot = top[index];
                    MapPerfLog.Info(
                        "  #" + (index + 1) + " " + DescribeMethod(snapshot.Method) +
                        " calls=" + snapshot.Calls +
                        " total=" + TicksToMilliseconds(snapshot.TotalTicks).ToString("F2") + "ms" +
                        " avg=" + TicksToMilliseconds(snapshot.TotalTicks / snapshot.Calls).ToString("F3") + "ms" +
                        " max=" + TicksToMilliseconds(snapshot.MaxTicks).ToString("F2") + "ms" +
                        " slow=" + snapshot.SlowCalls);
                }
            }

            internal static void Reset()
            {
                Samples.Clear();
                Volatile.Write(ref _installationComplete, 0);
                Volatile.Write(ref _installedMethodCount, 0);
            }

            private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    return exception.Types.Where(type => type != null);
                }
            }

            private static void UpdateMaximum(ref long location, long value)
            {
                while (true)
                {
                    var current = Volatile.Read(ref location);
                    if (value <= current)
                        return;
                    if (Interlocked.CompareExchange(ref location, value, current) == current)
                        return;
                }
            }

            private static double TicksToMilliseconds(long ticks) =>
                ticks * 1000.0 / Stopwatch.Frequency;

            private sealed class ProfileSample
            {
                internal long Calls;
                internal long TotalTicks;
                internal long MaxTicks;
                internal long SlowCalls;
                internal long LastSlowLogTimestamp;
            }

            private sealed class ProfileSnapshot
            {
                internal readonly MethodBase Method;
                internal readonly long Calls;
                internal readonly long TotalTicks;
                internal readonly long MaxTicks;
                internal readonly long SlowCalls;

                internal ProfileSnapshot(
                    MethodBase method,
                    long calls,
                    long totalTicks,
                    long maxTicks,
                    long slowCalls)
                {
                    Method = method;
                    Calls = calls;
                    TotalTicks = totalTicks;
                    MaxTicks = maxTicks;
                    SlowCalls = slowCalls;
                }
            }
        }
    }
}
