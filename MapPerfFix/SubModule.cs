using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Runtime;
using System.Text;
using TaleWorlds.MountAndBlade;

namespace MapPerfProbe
{
    public class SubModule : MBSubModuleBase
    {
        private const string HId = "mmq.mapperfprobe";
        private static readonly ConcurrentDictionary<MethodBase, PerfStat> Stats = new ConcurrentDictionary<MethodBase, PerfStat>();
        private static bool _didPatch;
        private static readonly ConcurrentDictionary<MethodBase, double> _allocCd =
            new ConcurrentDictionary<MethodBase, double>();
        private const double AllocLogCooldown = 2.0;
        private const double AllocLogTtl = 60.0;
        private static double _nextAllocPrune;
        private static int _sample;

        [ThreadStatic] private static int _callDepth;
        [ThreadStatic] private static MethodBase _rootPeriodic;
        [ThreadStatic] private static bool _traceMem;
        [ThreadStatic] private static Dictionary<MethodBase, (double sum, int n)> _rootBucket;
        [ThreadStatic] private static double _rootBurstTotal;
        [ThreadStatic] private static int _rootDepth;
        [ThreadStatic] private static bool _skipMapOnFrameTick;
        [ThreadStatic] private static bool _mapScreenFastTime;
        [ThreadStatic] private static bool _mapScreenFastTimeValid;
        [ThreadStatic] private static bool _mapScreenThrottleActive;
        private const double RootChildBaseCutoffMs = 0.5;
        private const double RootBurstCooldownSeconds = 0.25;
        private static double _nextRootBurstAllowed;
        private static readonly ConcurrentDictionary<MethodBase, double> RootAgg =
            new ConcurrentDictionary<MethodBase, double>();
        private static readonly ConcurrentDictionary<Type, Func<object, int?>> CountResolvers =
            new ConcurrentDictionary<Type, Func<object, int?>>();
        private static readonly object MapScreenProbeLock = new object();
        private const double MapScreenProbeDtThresholdMs = 12.0;
        private static List<MapFieldProbe> _mapScreenProbes;
        private static Type _mapScreenProbeType;
        private static Type _mapScreenType;
        private static double _mapScreenNextLog;
        private static readonly StringBuilder MapScreenLogBuilder = new StringBuilder(256);
        private static readonly HashSet<string> MapScreenFrameHooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OnFrameTick",
            "Tick",
            "OnTick"
        };
        private static readonly string[] MapScreenProbeWhitelist =
        {
            "partyvisual",
            "parties",
            "mapicon",
            "iconvisual",
            "particle",
            "effect"
        };
        private static readonly string[] MapScreenProbeKeywords = { "party", "icon", "particle", "effect", "visual" };
        private static readonly ConcurrentDictionary<MethodBase, ConcurrentDictionary<string, AllocBucketStat>> AllocBuckets =
            new ConcurrentDictionary<MethodBase, ConcurrentDictionary<string, AllocBucketStat>>();
        private const int AllocBucketsPerMethodLimit = 8;
        private static readonly long[] AllocBucketThresholds =
            { 1_000_000, 2_000_000, 4_000_000, 8_000_000, 16_000_000, 32_000_000, 64_000_000, 128_000_000 };
        private static Type _campaignType;
        private static PropertyInfo _campaignCurrentProp;
        private static PropertyInfo _campaignTimeControlProp;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private static bool IsPeriodic(MethodBase m)
        {
            var n = m.Name;
            if (n == null) return false;
            return n.IndexOf("DailyTick", StringComparison.OrdinalIgnoreCase) >= 0
                   || n.IndexOf("HourlyTick", StringComparison.OrdinalIgnoreCase) >= 0
                   || n.IndexOf("WeeklyTick", StringComparison.OrdinalIgnoreCase) >= 0
                   || string.Equals(n, "TickPeriodicEvents", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "PeriodicDailyTick", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "PeriodicHourlyTick", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "QuarterDailyPartyTick", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "TickPartialHourlyAi", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "AiHourlyTick", StringComparison.OrdinalIgnoreCase);
        }

        private static long _lastFrameTS = Stopwatch.GetTimestamp();
        private static readonly int[] _gcLast = new int[3];
        private static readonly int[] _gcAgg = new int[3];
        private static double _nextFlush = 0.0;
        private static long _lastAlloc = GC.GetTotalMemory(false);
        private static long _lastWs = GetWS();
        private static double _frameSpikeCD = 0.0;
        private static double _memSpikeCD = 0.0;

        // --- MapScreen throttling (real perf tweak) ---
        private static GCLatencyMode _prevGcMode = GCSettings.LatencyMode;
        private static int _mapScreenSkipFrames;
        private const double MapScreenBackoffMs1 = 12.0; // if a frame takes ≥12ms, skip next 1 frame
        private const double MapScreenBackoffMs2 = 18.0; // if a frame takes ≥18ms, skip next 2 frames
        private const int MapScreenSkipFrames1 = 1;
        private const int MapScreenSkipFrames2 = 2;

        protected override void OnSubModuleLoad()
        {
            if (_didPatch) return;
            _didPatch = true;
            MapPerfLog.Info("=== MapPerfProbe start ===");

            try
            {
                var harmony = CreateHarmony();
                if (harmony == null)
                {
                    MapPerfLog.Warn("Harmony not found; only frame/GC logging.");
                    _didPatch = false;
                    return;
                }

                // GC latency tuning – reduce full-blocking pauses during map play
                _prevGcMode = GCSettings.LatencyMode;
                try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; }
                catch { /* best-effort on Mono/older runtimes */ }

                // IMPORTANT: throttle patch must be applied before broad instrumentation,
                // so its bool-prefix can skip the original when needed.
                PatchMapScreenThrottle(harmony);

                // High-level map/UI hooks (already working)
                SafePatch("TryPatchType(MapState)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.GameState.MapState", new[] { "OnTick", "OnMapModeTick", "OnFrameTick" }));
                SafePatch("TryPatchType(MapState2)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.MapState", new[] { "OnTick", "OnMapModeTick", "OnFrameTick" }));
                SafePatch("TryPatchType(CampaignEventDispatcher)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.CampaignEventDispatcher", new[] { "OnTick", "OnHourlyTick", "OnDailyTick" }));
                SafePatch("TryPatchType(MapScreen)", () => TryPatchType(harmony, "SandBox.View.Map.MapScreen", new[] { "OnFrameTick", "Tick", "OnTick" }));
                SafePatch("TryPatchType(UI)", () => TryPatchType(harmony, "TaleWorlds.GauntletUI.UIContext", new[] { "Update", "Tick" }));
                SafePatch("TryPatchType(Layer)", () => TryPatchType(harmony, "TaleWorlds.GauntletUI.GauntletLayer", new[] { "OnLateUpdate", "Tick" }));

                // 🔎 NEW: instrument the actual campaign behaviors that daily/hourly logic calls into
                SafePatch("PatchBehaviorTicks", () => PatchBehaviorTicks(harmony));
                SafePatch("PatchCampaignCoreTicks", () => PatchCampaignCoreTicks(harmony));
                SafePatch("PatchDispatcherFallback", () => PatchDispatcherFallback(harmony));
            }
            catch (Exception ex)
            {
                MapPerfLog.Error("OnSubModuleLoad fatal", ex);
                try { GCSettings.LatencyMode = _prevGcMode; }
                catch { /* best-effort restore */ }
                _didPatch = false; // fail safe, keep game running
            }
        }

        protected override void OnSubModuleUnloaded()
        {
            try
            {
                var harmony = CreateHarmony();
                harmony?.GetType().GetMethod("UnpatchAll", new[] { typeof(string) })?.Invoke(harmony, new object[] { HId });
            }
            catch (Exception ex)
            {
                MapPerfLog.Error("Unpatch error", ex);
            }
            try { GCSettings.LatencyMode = _prevGcMode; }
            catch { /* restore best-effort */ }
            _didPatch = false;
            FlushSummary(force: true);
            MapPerfLog.Info("=== MapPerfProbe stop ===");
        }

        protected override void OnApplicationTick(float dt)
        {
            var nowTs = Stopwatch.GetTimestamp();
            double frameMs = (nowTs - _lastFrameTS) * TicksToMs;
            _lastFrameTS = nowTs;
            if (IsOnMap() && frameMs > 50.0 && _frameSpikeCD <= 0.0)
            {
                MapPerfLog.Warn($"FRAME spike {frameMs:F1} ms [{(IsPaused() ? "PAUSED" : "RUN")}]");
                if (frameMs > 200.0) FlushSummary(true);
                _frameSpikeCD = 1.0;
            }

            for (int g = 0; g < 3; g++)
            {
                int c = GC.CollectionCount(g);
                if (c != _gcLast[g])
                {
                    _gcAgg[g] += c - _gcLast[g];
                    _gcLast[g] = c;
                }
            }

            var curAlloc = GC.GetTotalMemory(false);
            var allocDelta = curAlloc - _lastAlloc;
            _lastAlloc = curAlloc;

            var ws = GetWS();
            var wsDelta = ws - _lastWs;
            _lastWs = ws;

            if (IsOnMap() && allocDelta > 25_000_000 && _memSpikeCD <= 0.0)
            {
                MapPerfLog.Warn($"ALLOC spike +{allocDelta / 1_000_000.0:F1} MB");
                _memSpikeCD = 5.0;
            }

            if (IsOnMap() && wsDelta > 75_000_000 && _memSpikeCD <= 0.0)
            {
                MapPerfLog.Warn($"WS spike +{wsDelta / 1_000_000.0:F1} MB");
                _memSpikeCD = 5.0;
            }

            if (allocDelta > 150_000_000 || wsDelta > 250_000_000)
                FlushSummary(true);

            _nextFlush -= dt;
            if (_frameSpikeCD > 0.0)
                _frameSpikeCD = Math.Max(0.0, _frameSpikeCD - dt);
            if (_memSpikeCD > 0.0)
                _memSpikeCD = Math.Max(0.0, _memSpikeCD - dt);
            if (_nextFlush <= 0.0)
            {
                _nextFlush = 2.0;
                if (IsOnMap()) FlushSummary(force: false);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll")]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
            public ulong QuotaPeakPagedPoolUsage;
            public ulong QuotaPagedPoolUsage;
            public ulong QuotaPeakNonPagedPoolUsage;
            public ulong QuotaNonPagedPoolUsage;
            public ulong PagefileUsage;
            public ulong PeakPagefileUsage;
        }

        private static long GetWS()
        {
            try
            {
                if (GetProcessMemoryInfo(GetCurrentProcess(), out var counters, Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS))))
                    return (long)counters.WorkingSetSize;
            }
            catch { }
            return 0;
        }

        private static object CreateHarmony()
        {
            // Bind to Harmony only if Bannerlord.Harmony loaded 0Harmony into AppDomain
            var ht = Type.GetType("HarmonyLib.Harmony, 0Harmony", throwOnError: false);
            if (ht == null) return null;
            return Activator.CreateInstance(ht, new object[] { HId });
        }

        private static void SafePatch(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { MapPerfLog.Error($"Patch phase failed: {name}", ex); }
        }

        private const BindingFlags HookBindingFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static void TryPatchType(object harmony, string typeName, string[] methodNames)
        {
            var t = FindType(typeName);
            if (t == null) return;

            var ht = harmony.GetType();
            var harmonyAsm = ht.Assembly;
            var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (patchMi == null)
            {
                MapPerfLog.Error("Harmony Patch() method not found — aborting this patch phase.");
                return;
            }

            var pre = typeof(SubModule).GetMethod(nameof(PerfPrefix), HookBindingFlags);
            var post = typeof(SubModule).GetMethod(nameof(PerfPostfix), HookBindingFlags);
            var fin = typeof(SubModule).GetMethod(nameof(PerfFinalizer), HookBindingFlags);
            if (hmType == null || hmCtor == null || pre == null || post == null || fin == null)
            {
                MapPerfLog.Error($"HarmonyMethod ctor or prefix/postfix/finalizer not found (type={hmType != null}, ctor={hmCtor != null}, pre={pre != null}, post={post != null}, fin={fin != null}).");
                return;
            }
            var preHM = hmCtor.Invoke(new object[] { pre });
            var postHM = hmCtor.Invoke(new object[] { post });
            var finHM = hmCtor.Invoke(new object[] { fin });

            MethodInfo[] methods;
            try
            {
                methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                return;
            }

            foreach (var m in methods)
            {
                if (!NameIn(methodNames, m.Name)) continue;
                if (m.ReturnType != typeof(void)) continue;
                if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true) continue;
                var declName = m.DeclaringType?.Name;
                if (declName != null && declName.IndexOf("d__", StringComparison.Ordinal) >= 0) continue;
                var ps = m.GetParameters();
                if (ps.Length > 2) continue;
                bool byref = false;
                for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) { byref = true; break; }
                if (byref) continue;

                try
                {
                    patchMi.Invoke(harmony, new object[] { m, preHM, postHM, null, finHM });
                    MapPerfLog.Info($"Patched {t.FullName}.{m.Name}");
                }
                catch (Exception ex)
                {
                    MapPerfLog.Error($"Patch fail {t.FullName}.{m.Name}", ex);
                }
            }
        }

        private static bool NameIn(string[] names, string s)
        {
            if (s == null) return false;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], s, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // --- Targeted perf tweak: throttle MapScreen.OnFrameTick under load ---
        private static void PatchMapScreenThrottle(object harmony)
        {
            var ht = harmony.GetType();
            var harmonyAsm = ht.Assembly;
            var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (patchMi == null || hmCtor == null || hmType == null) return;

            var mapT = GetMapScreenType();
            if (mapT == null) return;
            var pre = typeof(SubModule).GetMethod(nameof(MapScreenOnFrameTickPrefix), HookBindingFlags);
            if (pre == null) return;

            foreach (var name in MapScreenFrameHooks)
            {
                var target = mapT.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null) continue;

                var preHM = hmCtor.Invoke(new object[] { pre });
                var prioProp = hmType.GetProperty("priority", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                var prioField = hmType.GetField("priority", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                var prioEnum = harmonyAsm.GetType("HarmonyLib.Priority");
                var highest = prioEnum != null ? Enum.ToObject(prioEnum, 400) : (object)400; // Harmony v2+ enum or raw int fallback
                if (prioProp != null)
                {
                    try { prioProp.SetValue(preHM, highest); }
                    catch { /* ignore Harmony variants without setter */ }
                }
                if (prioField != null)
                {
                    try { prioField.SetValue(preHM, highest); }
                    catch { /* ignore Harmony variants without setter */ }
                }

                try
                {
                    // no postfix/finalizer here – we only gate execution
                    patchMi.Invoke(harmony, new object[] { target, preHM, null, null, null });
                    MapPerfLog.Info($"Patched MapScreen.{name} (throttle)");
                }
                catch (Exception ex)
                {
                    MapPerfLog.Error($"Patch fail MapScreen.{name} (throttle)", ex);
                }
            }
        }

        // bool-prefix: return false to skip original map frame hooks when throttling
        public static bool MapScreenOnFrameTickPrefix(object __instance, MethodBase __originalMethod)
        {
            var methodName = __originalMethod?.Name;
            if (methodName == null) return true;

            if (string.Equals(methodName, "OnFrameTick", StringComparison.OrdinalIgnoreCase))
            {
                _mapScreenFastTimeValid = false;
                _mapScreenThrottleActive = false;
                if (__instance == null || IsPaused())
                {
                    _skipMapOnFrameTick = false;
                    return true;
                }

                var fastTime = IsFastTime();
                _mapScreenFastTime = fastTime;
                _mapScreenFastTimeValid = true;
                if (!fastTime)
                {
                    _skipMapOnFrameTick = false;
                    _mapScreenSkipFrames = 0;
                    return true;
                }

                if (_mapScreenSkipFrames > 0)
                {
                    _mapScreenSkipFrames--;
                    _mapScreenThrottleActive = true;
                    _skipMapOnFrameTick = true;
                    return false;
                }

                _skipMapOnFrameTick = false;
                return true;
            }

            if (methodName == null || !MapScreenFrameHooks.Contains(methodName))
                return true;

            var hadFastTime = _mapScreenFastTimeValid;
            var cachedFastTime = _mapScreenFastTime;
            _mapScreenFastTimeValid = false;
            if (__instance == null || IsPaused())
            {
                _mapScreenThrottleActive = false;
                _skipMapOnFrameTick = false;
                return true;
            }

            var fastTime = hadFastTime ? cachedFastTime : IsFastTime();
            if (!fastTime)
            {
                _mapScreenThrottleActive = false;
                _skipMapOnFrameTick = false;
                _mapScreenSkipFrames = 0;
                return true;
            }

            if (_mapScreenThrottleActive || _mapScreenSkipFrames > 0)
            {
                _mapScreenThrottleActive = true;
                _skipMapOnFrameTick = true;
                return false;
            }

            return true;
        }

        private static bool IsFastTime()
        {
            try
            {
                var campT = _campaignType ?? (_campaignType =
                    Type.GetType("TaleWorlds.CampaignSystem.Campaign, TaleWorlds.CampaignSystem", false));
                if (campT == null) return false;
                var currentProp = _campaignCurrentProp ??
                                  (_campaignCurrentProp = campT.GetProperty("Current", BindingFlags.Public | BindingFlags.Static));
                var current = currentProp?.GetValue(null);
                if (current == null) return false;
                var tcmProp = _campaignTimeControlProp ??
                              (_campaignTimeControlProp = campT.GetProperty("TimeControlMode", BindingFlags.Public | BindingFlags.Instance));
                var mode = tcmProp?.GetValue(current);
                if (mode == null) return false;
                var modeName = mode.ToString();
                return !string.Equals(modeName, "Normal", StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(modeName, "Stop", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var tt = asm.GetType(fullName, false);
                if (tt != null) return tt;
            }
            return Type.GetType(fullName, false);
        }

        // -------- NEW: broad behavior/dispatcher instrumentation ----------
        private static void PatchBehaviorTicks(object harmony)
        {
            var ht = harmony.GetType();
            var harmonyAsm = ht.Assembly;
            var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (patchMi == null)
            {
                MapPerfLog.Error("Harmony Patch() method not found — aborting this patch phase.");
                return;
            }

            var pre = typeof(SubModule).GetMethod(nameof(PerfPrefix), HookBindingFlags);
            var post = typeof(SubModule).GetMethod(nameof(PerfPostfix), HookBindingFlags);
            var fin = typeof(SubModule).GetMethod(nameof(PerfFinalizer), HookBindingFlags);
            if (hmType == null || hmCtor == null || pre == null || post == null || fin == null)
            {
                MapPerfLog.Error($"HarmonyMethod ctor or prefix/postfix/finalizer not found (type={hmType != null}, ctor={hmCtor != null}, pre={pre != null}, post={post != null}, fin={fin != null}).");
                return;
            }
            var preHM = hmCtor.Invoke(new object[] { pre });
            var postHM = hmCtor.Invoke(new object[] { post });
            var finHM = hmCtor.Invoke(new object[] { fin });

            string[] nameHits = { "DailyTick", "HourlyTick", "WeeklyTick", "OnDailyTick", "OnHourlyTick" };

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name;
                if (!(an.StartsWith("TaleWorlds") || an.StartsWith("SandBox"))) continue;

                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    if (!IsSubclassOfByName(t, "TaleWorlds.CampaignSystem.CampaignBehaviorBase")) continue;
                    if (t.Namespace != null &&
                        (t.Namespace.StartsWith("TaleWorlds.Gauntlet", StringComparison.Ordinal) ||
                         t.Namespace.StartsWith("TaleWorlds.TwoDimension", StringComparison.Ordinal)))
                        continue;

                    MethodInfo[] methods;
                    try
                    {
                        methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var m in methods)
                    {
                        if (!AnyNameMatch(m.Name, nameHits)) continue;
                        if (m.ReturnType != typeof(void)) continue;
                        if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true) continue;
                        var declName = m.DeclaringType?.Name;
                        if (declName != null && declName.IndexOf("d__", StringComparison.Ordinal) >= 0) continue;
                        var ps = m.GetParameters();
                        if (ps.Length > 2) continue;
                        bool byref = false;
                        for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) { byref = true; break; }
                        if (byref) continue;
                        try
                        {
                            patchMi.Invoke(harmony, new object[] { m, preHM, postHM, null, finHM });
                            MapPerfLog.Info($"Patched {t.FullName}.{m.Name}");
                        }
                        catch (Exception ex)
                        {
                            MapPerfLog.Error($"Patch fail {t.FullName}.{m.Name}", ex);
                        }
                    }
                }
            }
        }

        private static void PatchCampaignCoreTicks(object harmony)
        {
            var ht = harmony.GetType();
            var harmonyAsm = ht.Assembly;
            var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (patchMi == null)
            {
                MapPerfLog.Error("Harmony Patch() method not found — aborting this patch phase.");
                return;
            }

            var pre = typeof(SubModule).GetMethod(nameof(PerfPrefix), HookBindingFlags);
            var post = typeof(SubModule).GetMethod(nameof(PerfPostfix), HookBindingFlags);
            var fin = typeof(SubModule).GetMethod(nameof(PerfFinalizer), HookBindingFlags);
            if (hmType == null || hmCtor == null || pre == null || post == null || fin == null)
            {
                MapPerfLog.Error($"HarmonyMethod ctor or prefix/postfix/finalizer not found (type={hmType != null}, ctor={hmCtor != null}, pre={pre != null}, post={post != null}, fin={fin != null}).");
                return;
            }
            var preHM = hmCtor.Invoke(new object[] { pre });
            var postHM = hmCtor.Invoke(new object[] { post });
            var finHM = hmCtor.Invoke(new object[] { fin });

            // Focused set: core campaign + party/settlement/AI/economy
            string[] typeHits =
            {
                ".CampaignSystem.Campaign",
                ".CampaignSystem.CampaignAiManager",
                ".CampaignSystem.MobileParty",
                ".CampaignSystem.Party.PartyAi",
                ".CampaignSystem.Settlements.SettlementComponent",
                ".CampaignSystem.Settlements.Town",
                ".CampaignSystem.Settlements.Village",
                ".CampaignSystem.Army",
                ".CampaignSystem.Clan",
                ".CampaignSystem.Kingdom",
                ".CampaignSystem.Economy",
                ".CampaignSystem.Pathfinding",
            };

            bool HitType(string full)
            {
                if (string.IsNullOrEmpty(full)) return false;
                for (int i = 0; i < typeHits.Length; i++)
                    if (full.IndexOf(typeHits[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name;
                if (!an.StartsWith("TaleWorlds")) continue;
                Type[] types; try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    if (!HitType(t.FullName)) continue;
                    if (t.Namespace != null &&
                        (t.Namespace.StartsWith("TaleWorlds.Gauntlet", StringComparison.Ordinal) ||
                         t.Namespace.StartsWith("TaleWorlds.TwoDimension", StringComparison.Ordinal)))
                        continue;

                    MethodInfo[] methods;
                    try
                    {
                        methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var m in methods)
                    {
                        var name = m.Name;
                        bool nameHit = string.Equals(name, "Tick", StringComparison.OrdinalIgnoreCase)
                                       || name.EndsWith("Tick", StringComparison.OrdinalIgnoreCase)
                                       || name.StartsWith("Tick", StringComparison.OrdinalIgnoreCase)
                                       || name.StartsWith("Update", StringComparison.OrdinalIgnoreCase)
                                       || name.EndsWith("Update", StringComparison.OrdinalIgnoreCase);
                        if (!nameHit) continue;
                        if (m.ReturnType != typeof(void)) continue;
                        if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true) continue;
                        var declName = m.DeclaringType?.Name;
                        if (declName != null && declName.IndexOf("d__", StringComparison.Ordinal) >= 0) continue;
                        var ps = m.GetParameters();
                        if (ps.Length > 2) continue;
                        bool byref = false;
                        for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) { byref = true; break; }
                        if (byref) continue;

                        try { patchMi.Invoke(harmony, new object[] { m, preHM, postHM, null, finHM }); MapPerfLog.Info($"Patched {t.FullName}.{name}"); }
                        catch (Exception ex) { MapPerfLog.Error($"Patch fail {t.FullName}.{name}", ex); }
                    }
                }
            }
        }

        private static void PatchDispatcherFallback(object harmony)
        {
            // Some builds rename/move the dispatcher; this scans for an obvious OnDaily/OnHourly hub.
            var ht = harmony.GetType();
            var harmonyAsm = ht.Assembly;
            var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (patchMi == null)
            {
                MapPerfLog.Error("Harmony Patch() method not found — aborting this patch phase.");
                return;
            }

            var pre = typeof(SubModule).GetMethod(nameof(PerfPrefix), HookBindingFlags);
            var post = typeof(SubModule).GetMethod(nameof(PerfPostfix), HookBindingFlags);
            var fin = typeof(SubModule).GetMethod(nameof(PerfFinalizer), HookBindingFlags);
            if (hmType == null || hmCtor == null || pre == null || post == null || fin == null)
            {
                MapPerfLog.Error($"HarmonyMethod ctor or prefix/postfix/finalizer not found (type={hmType != null}, ctor={hmCtor != null}, pre={pre != null}, post={post != null}, fin={fin != null}).");
                return;
            }
            var preHM = hmCtor.Invoke(new object[] { pre });
            var postHM = hmCtor.Invoke(new object[] { post });
            var finHM = hmCtor.Invoke(new object[] { fin });

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name;
                if (!an.StartsWith("TaleWorlds")) continue;
                Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    var fullName = t.FullName;
                    if (string.IsNullOrEmpty(fullName)) continue;
                    if (fullName.IndexOf("Campaign", StringComparison.OrdinalIgnoreCase) < 0 ||
                        fullName.IndexOf("Dispatch", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    MethodInfo[] methods;
                    try
                    {
                        methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var m in methods)
                    {
                        if (!(m.Name == "OnDailyTick" || m.Name == "OnHourlyTick")) continue;
                        if (m.ReturnType != typeof(void)) continue;
                        if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true) continue;
                        var declName = m.DeclaringType?.Name;
                        if (declName != null && declName.IndexOf("d__", StringComparison.Ordinal) >= 0) continue;
                        var ps = m.GetParameters();
                        if (ps.Length > 2) continue;
                        bool byref = false;
                        for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) { byref = true; break; }
                        if (byref) continue;
                        try { patchMi.Invoke(harmony, new object[] { m, preHM, postHM, null, finHM }); MapPerfLog.Info($"Patched {t.FullName}.{m.Name}"); }
                        catch (Exception ex) { MapPerfLog.Error($"Patch fail {t.FullName}.{m.Name}", ex); }
                    }
                }
            }
        }

        private static bool IsSubclassOfByName(Type t, string baseFullName)
        {
            for (var cur = t; cur != null; cur = cur.BaseType) if (cur.FullName == baseFullName) return true;
            return false;
        }

        private static bool AnyNameMatch(string name, string[] set)
        {
            if (name == null) return false;
            for (int i = 0; i < set.Length; i++)
            {
                if (string.Equals(name, set[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        // -------------------------------------------------------------------

        public struct State { public long ts; public long mem; }

        public static void PerfPrefix(object __instance, MethodBase __originalMethod, out State __state)
        {
            __state = default;
            if (_skipMapOnFrameTick)
            {
                var mapScreenType = GetMapScreenType();
                if (__instance != null && mapScreenType != null && mapScreenType.IsInstanceOfType(__instance))
                {
                    var methodName = __originalMethod?.Name;
                    if (methodName != null && MapScreenFrameHooks.Contains(methodName))
                    {
                        _skipMapOnFrameTick = false;
                        _mapScreenFastTimeValid = false;
                        __state = default;
                        return;
                    }
                }
                _skipMapOnFrameTick = false;
            }
            _callDepth++;
            // Choose the first periodic seen as the root this burst
            if (_rootPeriodic == null && IsPeriodic(__originalMethod))
            {
                var nowSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                if (nowSeconds >= Interlocked.CompareExchange(ref _nextRootBurstAllowed, 0.0, 0.0))
                {
                    _rootPeriodic = __originalMethod;
                    _rootDepth = _callDepth;
                    _traceMem = true; // turn on per-call alloc sampling for this burst
                    _rootBucket = new Dictionary<MethodBase, (double, int)>(64);
                    _rootBurstTotal = 0.0;
                }
            }

            // Burst sample memory inside periodic roots; otherwise 1/16 sampling
            bool sample = _traceMem || ((Interlocked.Increment(ref _sample) & 0xF) == 0);
            if (sample) __state.mem = GC.GetTotalMemory(false);
            __state.ts = Stopwatch.GetTimestamp();
        }

        public static void PerfPostfix(object __instance, MethodBase __originalMethod, State __state)
        {
            if (__state.ts == 0) return;
            var dt = (Stopwatch.GetTimestamp() - __state.ts) * TicksToMs;
            if (__originalMethod == null)
            {
                _mapScreenFastTimeValid = false;
                return;
            }

            var stat = Stats.GetOrAdd(__originalMethod, _ => new PerfStat(__originalMethod));
            stat.Add(dt);

            if (__instance != null)
            {
                var mapScreenType = GetMapScreenType();
                if (mapScreenType != null && mapScreenType.IsInstanceOfType(__instance))
                {
                    var methodName = __originalMethod?.Name;
                    if (methodName != null && string.Equals(methodName, "OnFrameTick", StringComparison.OrdinalIgnoreCase))
                    {
                        CollectMapScreenStats(__instance, dt);
                        var fastTime = _mapScreenFastTimeValid ? _mapScreenFastTime : IsFastTime();
                        _mapScreenFastTimeValid = false;
                        if (fastTime)
                        {
                            if (dt >= MapScreenBackoffMs2)
                                _mapScreenSkipFrames = Math.Max(_mapScreenSkipFrames, MapScreenSkipFrames2);
                            else if (dt >= MapScreenBackoffMs1)
                                _mapScreenSkipFrames = Math.Max(_mapScreenSkipFrames, MapScreenSkipFrames1);
                        }
                    }
                    else if (methodName != null && MapScreenFrameHooks.Contains(methodName))
                    {
                        _mapScreenFastTimeValid = false;
                    }
                }
            }

            // Attribute to current periodic root
            var root = _rootPeriodic;
            if (root != null)
            {
                if (ReferenceEquals(__originalMethod, root))
                {
                    // window total (inclusive) + burst self
                    RootAgg.AddOrUpdate(root, dt, (_, v) => v + dt);
                    _rootBurstTotal += dt;
                }
                else if (_rootBucket != null && _callDepth == _rootDepth + 1)
                {
                    // direct children only (no double count)
                    if (!_rootBucket.TryGetValue(__originalMethod, out var ag)) ag = default;
                    ag.sum += dt; ag.n++;
                    _rootBucket[__originalMethod] = ag;
                }
            }

            if (__state.mem != 0)
            {
                var tNow = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                var alloc = GC.GetTotalMemory(false) - __state.mem;
                if (alloc > 0)
                    RecordAllocBucket(__originalMethod, alloc, dt);
                if (alloc > 20_000_000)
                {
                    if (!_allocCd.TryGetValue(__originalMethod, out var next) || tNow >= next)
                    {
                        _allocCd[__originalMethod] = tNow + AllocLogCooldown;
                        var owner = __originalMethod.DeclaringType?.FullName ?? "<global>";
                        var methodName = __originalMethod?.Name ?? "<unknown>";
                        MapPerfLog.Warn($"ALLOC+ {alloc / 1_000_000.0:F1} MB @ {owner}.{methodName}");
                    }
                }

                PruneAllocCooldowns(tNow);
            }

            if (_rootPeriodic != null && _callDepth == _rootDepth)
            {
                // Dump top children once per burst (limit spam)
                if (_rootBucket != null)
                {
                    var list = new List<KeyValuePair<MethodBase, (double sum, int n)>>(_rootBucket);
                    list.Sort((a, b) => b.Value.sum.CompareTo(a.Value.sum));
                    var rOwner = root?.DeclaringType?.FullName ?? "<global>";
                    var rName = root?.Name ?? "<none>";
                    int take = Math.Min(8, list.Count);
                    MapPerfLog.Info($"[root-burst] {rOwner}.{rName} — top children:");
                    var includePct = _rootBurstTotal >= 1.0 && _rootBurstTotal > 0.0;
                    var dynamicCutoff = Math.Max(RootChildBaseCutoffMs, 0.02 * _rootBurstTotal);
                    var printed = new HashSet<MethodBase>();
                    for (int i = 0; i < take; i++)
                    {
                        var kv = list[i];
                        var pct = includePct ? (100.0 * kv.Value.sum / _rootBurstTotal) : 0.0;
                        if (kv.Value.sum >= dynamicCutoff || (includePct && pct >= 10.0))
                        {
                            LogRootChild(kv.Key, kv.Value.sum, pct, kv.Value.n, includePct);
                            printed.Add(kv.Key);
                        }
                    }
                    if (includePct)
                    {
                        for (int i = take; i < list.Count; i++)
                        {
                            var kv = list[i];
                            var pct = 100.0 * kv.Value.sum / _rootBurstTotal;
                            if (pct < 10.0) continue;
                            if (printed.Add(kv.Key))
                                LogRootChild(kv.Key, kv.Value.sum, pct, kv.Value.n, includePct);
                        }
                    }
                    if (printed.Count == 0)
                    {
                        for (int i = 0; i < take; i++)
                        {
                            var kv = list[i];
                            var pct = includePct ? (100.0 * kv.Value.sum / _rootBurstTotal) : 0.0;
                            LogRootChild(kv.Key, kv.Value.sum, pct, kv.Value.n, includePct);
                            printed.Add(kv.Key);
                        }
                    }
                    double childSum = 0;
                    for (int i = 0; i < list.Count; i++) childSum += list[i].Value.sum;
                    var selfMs = _rootBurstTotal - childSum;
                    if (selfMs < 0) selfMs = 0;
                    if (printed.Count > 0 || selfMs >= dynamicCutoff)
                        LogRootSelf(selfMs, _rootBurstTotal);
                }
                var nowSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                Interlocked.Exchange(ref _nextRootBurstAllowed, nowSeconds + RootBurstCooldownSeconds);
                _rootBucket = null;
                _rootPeriodic = null;
                _traceMem = false;
                _rootDepth = 0;
                _rootBurstTotal = 0.0;
            }
        }

        public static Exception PerfFinalizer(object __instance, MethodBase __originalMethod, State __state, Exception __exception)
        {
            if (_mapScreenFastTimeValid &&
                __instance != null &&
                GetMapScreenType()?.IsInstanceOfType(__instance) == true &&
                (__originalMethod?.Name != null && MapScreenFrameHooks.Contains(__originalMethod.Name)))
            {
                _mapScreenFastTimeValid = false;
            }
            if (__state.ts == 0) return __exception;
            if (__exception != null && _rootBucket != null && _rootPeriodic != null && _callDepth == _rootDepth)
            {
                var list = new List<KeyValuePair<MethodBase, (double sum, int n)>>(_rootBucket);
                list.Sort((a, b) => b.Value.sum.CompareTo(a.Value.sum));
                var rOwner = _rootPeriodic?.DeclaringType?.FullName ?? "<global>";
                var rName = _rootPeriodic?.Name ?? "<none>";
                int take = Math.Min(8, list.Count);
                MapPerfLog.Info($"[root-burst:EX] {rOwner}.{rName} — top children:");
                var includePct = _rootBurstTotal >= 1.0 && _rootBurstTotal > 0.0;
                var dynamicCutoff = Math.Max(RootChildBaseCutoffMs, 0.02 * _rootBurstTotal);
                var printed = new HashSet<MethodBase>();
                for (int i = 0; i < take; i++)
                {
                    var kv = list[i];
                    var pct = includePct ? (100.0 * kv.Value.sum / _rootBurstTotal) : 0.0;
                    if (kv.Value.sum >= dynamicCutoff || (includePct && pct >= 10.0))
                    {
                        LogRootChild(kv.Key, kv.Value.sum, pct, kv.Value.n, includePct);
                        printed.Add(kv.Key);
                    }
                }
                if (includePct)
                {
                    for (int i = take; i < list.Count; i++)
                    {
                        var kv = list[i];
                        var pct = 100.0 * kv.Value.sum / _rootBurstTotal;
                        if (pct < 10.0) continue;
                        if (printed.Add(kv.Key))
                            LogRootChild(kv.Key, kv.Value.sum, pct, kv.Value.n, includePct);
                    }
                }
                if (printed.Count == 0)
                {
                    for (int i = 0; i < take; i++)
                    {
                        var kv = list[i];
                        var pct = includePct ? (100.0 * kv.Value.sum / _rootBurstTotal) : 0.0;
                        LogRootChild(kv.Key, kv.Value.sum, pct, kv.Value.n, includePct);
                        printed.Add(kv.Key);
                    }
                }
                double childSum = 0;
                for (int i = 0; i < list.Count; i++) childSum += list[i].Value.sum;
                var selfMs = _rootBurstTotal - childSum;
                if (selfMs < 0) selfMs = 0;
                if (printed.Count > 0 || selfMs >= dynamicCutoff)
                    LogRootSelf(selfMs, _rootBurstTotal);
            }

            // ensure depth + burst cleanup even if the original threw
            if (--_callDepth <= 0 || (_rootPeriodic != null && _callDepth < _rootDepth))
            {
                var nowSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                if (_rootPeriodic != null)
                    Interlocked.Exchange(ref _nextRootBurstAllowed, nowSeconds + RootBurstCooldownSeconds);
                _callDepth = 0;
                _rootBucket = null;
                _rootPeriodic = null;
                _traceMem = false;
                _rootDepth = 0;
                _rootBurstTotal = 0.0;
            }
            return __exception; // don't swallow
        }

        private static void PruneAllocCooldowns(double now)
        {
            double scheduled;
            do
            {
                scheduled = Interlocked.CompareExchange(ref _nextAllocPrune, 0.0, 0.0);
                if (scheduled > now) return;
            }
            while (Interlocked.CompareExchange(ref _nextAllocPrune, now + 10.0, scheduled) != scheduled);

            foreach (var kv in _allocCd)
            {
                var lastLogTime = kv.Value - AllocLogCooldown;
                if (now - lastLogTime > AllocLogTtl)
                    _allocCd.TryRemove(kv.Key, out _);
            }
        }

        private static void FlushSummary(bool force)
        {
            var list = new List<Snapshot>();
            foreach (var kv in Stats)
            {
                var snap = kv.Value.SnapshotAndReset();
                if (snap.Count > 0) list.Add(snap);
            }
            if (list.Count == 0 && !force) return;
            list.Sort((a, b) => b.P95.CompareTo(a.P95));
            int take = Math.Min(8, list.Count);
            MapPerfLog.Info($"-- bucket summary [{(IsPaused() ? "PAUSED" : "RUN")}] --");
            for (int i = 0; i < take; i++)
            {
                var s = list[i];
                MapPerfLog.Info($"{s.Name,-48} avg {s.Avg:F1} ms | p95 {s.P95:F1} | max {s.Max:F1} | n {s.Count}");
            }
            // NEW: show top periodic roots (inclusive time over window)
            if (!RootAgg.IsEmpty)
            {
                var roots = new List<KeyValuePair<MethodBase, double>>(RootAgg);
                RootAgg.Clear();
                roots.Sort((a, b) => b.Value.CompareTo(a.Value));
                int rtake = Math.Min(5, roots.Count);
                for (int i = 0; i < rtake; i++)
                {
                    var r = roots[i];
                    var owner = r.Key.DeclaringType?.FullName ?? "<global>";
                    MapPerfLog.Info($"[root] {owner}.{r.Key.Name}  total {r.Value:F1} ms");
                }
            }
            if (_gcAgg[0] + _gcAgg[1] + _gcAgg[2] > 0)
            {
                MapPerfLog.Info($"GC window: Gen0 +{_gcAgg[0]}, Gen1 +{_gcAgg[1]}, Gen2 +{_gcAgg[2]}");
                _gcAgg[0] = _gcAgg[1] = _gcAgg[2] = 0;
            }
            if (!AllocBuckets.IsEmpty)
            {
                var entries = new List<AllocBucketLog>();
                var toRemove = new List<MethodBase>();
                foreach (var kv in AllocBuckets)
                {
                    var methodLabel = FormatMethod(kv.Key);
                    foreach (var bucket in kv.Value)
                        entries.Add(new AllocBucketLog(methodLabel, bucket.Key, bucket.Value));
                    toRemove.Add(kv.Key);
                }
                for (int i = 0; i < toRemove.Count; i++)
                    AllocBuckets.TryRemove(toRemove[i], out _);
                if (entries.Count > 0)
                {
                    entries.Sort((a, b) => b.Stat.Bytes.CompareTo(a.Stat.Bytes));
                    int btake = Math.Min(6, entries.Count);
                    for (int i = 0; i < btake; i++)
                    {
                        var entry = entries[i];
                        double totalMb = entry.Stat.Bytes / 1_000_000.0;
                        double avgMb = entry.Stat.Count > 0 ? totalMb / entry.Stat.Count : 0.0;
                        double maxMb = entry.Stat.MaxBytes / 1_000_000.0;
                        double rateMbPerMs = entry.Stat.DtMs > 0.0 ? totalMb / entry.Stat.DtMs : 0.0;
                        MapPerfLog.Info($"[alloc] {entry.Method} {entry.Bucket}: count {entry.Stat.Count}, total {totalMb:F1} MB, avg {avgMb:F2} MB, max {maxMb:F1} MB, rate {rateMbPerMs:F3} MB/ms");
                    }
                }
            }
        }

        private static Type GetMapScreenType()
        {
            if (_mapScreenType != null) return _mapScreenType;
            _mapScreenType = FindType("SandBox.View.Map.MapScreen")
                               ?? Type.GetType("SandBox.View.Map.MapScreen, SandBox.View", false)
                               ?? Type.GetType("SandBox.View.Map.MapScreen, SandBox", false);
            return _mapScreenType;
        }

        private static void CollectMapScreenStats(object instance, double dt)
        {
            if (instance == null || dt < MapScreenProbeDtThresholdMs) return;
            if (IsPaused()) return;
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (now < _mapScreenNextLog) return;
            lock (MapScreenProbeLock)
            {
                if (now < _mapScreenNextLog) return;
                _mapScreenNextLog = now + 1.0;
                EnsureMapScreenProbes(instance);
                if (_mapScreenProbes == null || _mapScreenProbes.Count == 0)
                {
                    MapPerfLog.Info($"[MapScreen RUN] OnFrameTick {dt:F1} ms — no probe targets found.");
                    return;
                }

                MapScreenLogBuilder.Clear();
                MapScreenLogBuilder.Append("[MapScreen RUN] OnFrameTick ");
                MapScreenLogBuilder.Append(dt.ToString("F1", CultureInfo.InvariantCulture));
                MapScreenLogBuilder.Append(" ms — ");

                var first = true;
                foreach (var probe in _mapScreenProbes)
                {
                    int? count = null;
                    try { count = probe.Getter(instance); }
                    catch { }
                    if (!count.HasValue) continue;
                    if (!first) MapScreenLogBuilder.Append(", ");
                    MapScreenLogBuilder.Append(probe.Name);
                    MapScreenLogBuilder.Append('=');
                    MapScreenLogBuilder.Append(count.Value.ToString(CultureInfo.InvariantCulture));
                    first = false;
                }

                if (first)
                    MapScreenLogBuilder.Append("<no counts>");

                MapPerfLog.Info(MapScreenLogBuilder.ToString());
                MapScreenLogBuilder.Clear();
            }
        }

        private static void EnsureMapScreenProbes(object instance)
        {
            var type = instance.GetType();
            if (_mapScreenProbes != null && _mapScreenProbeType == type) return;

            var preferred = new List<MapFieldProbe>();
            var fallback = new List<MapFieldProbe>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var cur = type; cur != null; cur = cur.BaseType)
            {
                foreach (var field in cur.GetFields(flags))
                {
                    if (!seen.Add(field.Name)) continue;
                    var preferredMatch = MatchesMapScreenWhitelist(field.Name);
                    if (!ShouldProbe(field.Name, field.FieldType, preferredMatch)) continue;
                    var probe = CreateFieldProbe(field);
                    if (probe == null) continue;
                    if (preferredMatch) preferred.Add(probe); else fallback.Add(probe);
                }
                foreach (var prop in cur.GetProperties(flags))
                {
                    if (prop.GetIndexParameters().Length != 0) continue;
                    if (!seen.Add(prop.Name)) continue;
                    var preferredMatch = MatchesMapScreenWhitelist(prop.Name);
                    if (!ShouldProbe(prop.Name, prop.PropertyType, preferredMatch)) continue;
                    var probe = CreatePropertyProbe(prop);
                    if (probe == null) continue;
                    if (preferredMatch) preferred.Add(probe); else fallback.Add(probe);
                }
            }

            if (preferred.Count == 0 && fallback.Count > 0)
                preferred.AddRange(fallback);

            _mapScreenProbes = preferred;
            _mapScreenProbeType = type;
        }

        private static MapFieldProbe CreateFieldProbe(FieldInfo field)
        {
            return new MapFieldProbe(NormalizeProbeName(field.Name), instance =>
            {
                try
                {
                    var value = field.GetValue(instance);
                    return TryGetCount(value, out var count) ? (int?)count : null;
                }
                catch { return null; }
            });
        }

        private static MapFieldProbe CreatePropertyProbe(PropertyInfo prop)
        {
            if (prop == null) return null;
            var getter = prop.GetGetMethod(true);
            if (getter == null || getter.IsAbstract || getter.IsStatic) return null;
            var body = getter.GetMethodBody();
            if (body == null) return null;
            var il = body.GetILAsByteArray();
            if (il == null || il.Length > 64) return null;
            return new MapFieldProbe(NormalizeProbeName(prop.Name), instance =>
            {
                try
                {
                    var value = prop.GetValue(instance, null);
                    return TryGetCount(value, out var count) ? (int?)count : null;
                }
                catch { return null; }
            });
        }

        private static string NormalizeProbeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "<unnamed>";
            var trimmed = raw.Trim('_');
            return trimmed.Length == 0 ? raw : trimmed;
        }

        private static bool MatchesMapScreenWhitelist(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < MapScreenProbeWhitelist.Length; i++)
            {
                if (name.IndexOf(MapScreenProbeWhitelist[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool ShouldProbe(string name, Type type, bool forceNameMatch)
        {
            if (string.IsNullOrEmpty(name) || type == null) return false;
            if (type == typeof(string)) return false;
            if (!forceNameMatch)
            {
                bool keyword = false;
                for (int i = 0; i < MapScreenProbeKeywords.Length; i++)
                {
                    if (name.IndexOf(MapScreenProbeKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        keyword = true;
                        break;
                    }
                }
                if (!keyword) return false;
            }
            if (type.IsArray) return true;
            if (typeof(IEnumerable).IsAssignableFrom(type)) return true;
            if (HasCountAccessor(type)) return true;
            return false;
        }

        private static bool HasCountAccessor(Type type)
        {
            if (type == null) return false;
            if (type.IsArray) return true;
            if (typeof(IEnumerable).IsAssignableFrom(type)) return true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var prop = type.GetProperty("Count", flags);
            if (prop != null && prop.GetIndexParameters().Length == 0) return true;
            var lenProp = type.GetProperty("Length", flags);
            if (lenProp != null && lenProp.GetIndexParameters().Length == 0) return true;
            if (type.GetField("Count", flags) != null) return true;
            if (type.GetField("Length", flags) != null) return true;
            return false;
        }

        private static bool TryGetCount(object value, out int count)
        {
            count = 0;
            if (value == null) return false;
            if (value is Array arr)
            {
                count = arr.Length;
                return true;
            }
            if (value is ICollection coll)
            {
                count = coll.Count;
                return true;
            }
            var type = value.GetType();
            var resolver = CountResolvers.GetOrAdd(type, BuildCountResolver);
            if (resolver == null) return false;
            var resolved = resolver(value);
            if (!resolved.HasValue) return false;
            count = resolved.Value;
            return true;
        }

        private static Func<object, int?> BuildCountResolver(Type type)
        {
            if (type == null) return _ => null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var prop = type.GetProperty("Count", flags);
            if (prop != null && prop.GetIndexParameters().Length == 0)
            {
                return obj => ConvertCount(() => prop.GetValue(obj, null));
            }
            var lenProp = type.GetProperty("Length", flags);
            if (lenProp != null && lenProp.GetIndexParameters().Length == 0)
            {
                return obj => ConvertCount(() => lenProp.GetValue(obj, null));
            }
            var field = type.GetField("Count", flags);
            if (field != null)
            {
                return obj => ConvertCount(() => field.GetValue(obj));
            }
            var lenField = type.GetField("Length", flags);
            if (lenField != null)
            {
                return obj => ConvertCount(() => lenField.GetValue(obj));
            }
            return _ => null;
        }

        private static int? ConvertCount(Func<object> getter)
        {
            try
            {
                var raw = getter();
                if (raw == null) return null;

                int ToInt(long value)
                {
                    if (value < 0) return 0;
                    if (value > int.MaxValue) return int.MaxValue;
                    return (int)value;
                }

                switch (raw)
                {
                    case int i:
                        return Math.Max(0, i);
                    case long l:
                        return ToInt(l);
                    case uint ui:
                        return ToInt(ui);
                    case ushort us:
                        return us;
                    case short s:
                        return Math.Max(0, s);
                    case byte b:
                        return b;
                }
            }
            catch { }
            return null;
        }

        private static void RecordAllocBucket(MethodBase method, long bytes, double dt)
        {
            if (method == null || bytes <= 0) return;
            var bucket = GetAllocBucketLabel(bytes);
            var map = AllocBuckets.GetOrAdd(method, _ => new ConcurrentDictionary<string, AllocBucketStat>());
            map.AddOrUpdate(bucket,
                _ => new AllocBucketStat(bytes, 1, Math.Max(0.0, dt), bytes),
                (_, stat) => stat.Add(bytes, dt));
            TrimAllocBuckets(map);
        }

        private static void TrimAllocBuckets(ConcurrentDictionary<string, AllocBucketStat> map)
        {
            if (map == null) return;
            if (map.Count <= AllocBucketsPerMethodLimit) return;
            var snapshot = map.ToArray();
            if (snapshot.Length <= AllocBucketsPerMethodLimit) return;
            Array.Sort(snapshot, (a, b) => a.Value.Bytes.CompareTo(b.Value.Bytes));
            for (int i = 0; i < snapshot.Length - AllocBucketsPerMethodLimit; i++)
                map.TryRemove(snapshot[i].Key, out _);
        }

        private static void LogRootChild(MethodBase method, double totalMs, double pct, int count, bool includePct)
        {
            var owner = method?.DeclaringType?.FullName ?? "<global>";
            var name = method?.Name ?? "<unknown>";
            var pctText = includePct ? $" ({pct:F0}%)" : string.Empty;
            MapPerfLog.Info($"  ↳ {owner}.{name}  total {totalMs:F1} ms{pctText} (n {count})");
        }

        private static void LogRootSelf(double totalMs, double burstTotal)
        {
            var includePct = burstTotal >= 1.0;
            var pct = includePct && burstTotal > 0.0 ? (100.0 * totalMs / burstTotal) : 0.0;
            var pctText = includePct ? $" ({pct:F0}%)" : string.Empty;
            MapPerfLog.Info($"  ↳ self total {totalMs:F1} ms{pctText}");
        }

        private static string GetAllocBucketLabel(long bytes)
        {
            if (bytes <= 1_000_000)
                return "≤1 MB";
            for (int i = 0; i < AllocBucketThresholds.Length; i++)
            {
                var limit = AllocBucketThresholds[i];
                if (limit <= 1_000_000) continue;
                if (bytes <= limit)
                {
                    double mb = limit / 1_000_000.0;
                    return $"≤{mb:F0} MB";
                }
            }
            double top = AllocBucketThresholds[AllocBucketThresholds.Length - 1] / 1_000_000.0;
            return $">{top:F0} MB";
        }

        private static string FormatMethod(MethodBase method)
        {
            if (method == null) return "<unknown>";
            var name = method.Name ?? "<unknown>";
            var type = method.DeclaringType;
            string owner;
            if (type == null)
            {
                owner = "<global>";
            }
            else
            {
                owner = type.FullName;
                if (string.IsNullOrEmpty(owner))
                {
                    owner = type.Name ?? "<anon>";
                }
            }
            return $"{owner}.{name}";
        }

        private static bool IsOnMap()
        {
            try
            {
                var gsmType = Type.GetType("TaleWorlds.Core.GameStateManager, TaleWorlds.Core", throwOnError: false);
                var current = gsmType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var active = current?.GetType().GetProperty("ActiveState", BindingFlags.Public | BindingFlags.Instance)?.GetValue(current);
                var name = active?.GetType().FullName ?? string.Empty;
                return name.EndsWith(".MapState", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, "MapState", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPaused()
        {
            try
            {
                var campT = _campaignType ?? (_campaignType =
                    Type.GetType("TaleWorlds.CampaignSystem.Campaign, TaleWorlds.CampaignSystem", false));
                if (campT == null) return false;
                var currentProp = _campaignCurrentProp ??
                                  (_campaignCurrentProp = campT.GetProperty("Current", BindingFlags.Public | BindingFlags.Static));
                var current = currentProp?.GetValue(null);
                if (current == null) return false;
                var tcmProp = _campaignTimeControlProp ??
                              (_campaignTimeControlProp = campT.GetProperty("TimeControlMode", BindingFlags.Public | BindingFlags.Instance));
                var mode = tcmProp?.GetValue(current);
                if (mode == null) return false;
                return string.Equals(mode.ToString(), "Stop", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private sealed class PerfStat
        {
            private double _sum, _max; private int _n;
            private readonly double[] _ring = new double[128]; private int _i;
            public string Name { get; }
            public PerfStat(MethodBase m) => Name = $"{m.DeclaringType?.FullName ?? "<global>"}.{m.Name}";
            public void Add(double ms) { _sum += ms; if (ms > _max) _max = ms; _ring[_i++ & 127] = ms; _n++; }
            public Snapshot SnapshotAndReset()
            {
                int cnt = Math.Min(_n, _ring.Length); var arr = new double[cnt];
                for (int k = 0; k < cnt; k++) arr[k] = _ring[(_i - 1 - k) & 127];
                Array.Sort(arr); int idx = cnt == 0 ? 0 : Math.Max(0, Math.Min(cnt - 1, (int)Math.Floor(cnt * 0.95) - 1));
                var s = new Snapshot { Name = Name, Avg = _n > 0 ? _sum / _n : 0.0, Max = _max, P95 = cnt > 0 ? arr[idx] : 0.0, Count = _n };
                _sum = 0; _max = 0; _n = 0; return s;
            }
        }
        private sealed class MapFieldProbe
        {
            public string Name { get; }
            public Func<object, int?> Getter { get; }
            public MapFieldProbe(string name, Func<object, int?> getter)
            {
                Name = name;
                Getter = getter;
            }
        }
        private readonly struct AllocBucketStat
        {
            public long Bytes { get; }
            public int Count { get; }
            public double DtMs { get; }
            public long MaxBytes { get; }
            public AllocBucketStat(long bytes, int count, double dtMs, long maxBytes)
            {
                Bytes = bytes;
                Count = count;
                DtMs = dtMs;
                MaxBytes = maxBytes;
            }
            public AllocBucketStat Add(long bytes, double dtMs)
                => new AllocBucketStat(Bytes + bytes, Count + 1, DtMs + Math.Max(0.0, dtMs), Math.Max(MaxBytes, bytes));
        }
        private readonly struct AllocBucketLog
        {
            public string Method { get; }
            public string Bucket { get; }
            public AllocBucketStat Stat { get; }
            public AllocBucketLog(string method, string bucket, AllocBucketStat stat)
            {
                Method = method;
                Bucket = bucket;
                Stat = stat;
            }
        }
        private struct Snapshot { public string Name; public double Avg, Max, P95; public int Count; }
    }
}
