using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Runtime.CompilerServices;
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
        private static readonly ConcurrentDictionary<MethodBase, AllocWarnBudget> _allocBudget =
            new ConcurrentDictionary<MethodBase, AllocWarnBudget>();
        private const double AllocLogCooldown = 2.0;
        private const double AllocLogTtl = 60.0;
        private const double AllocWarnWindowSeconds = 5.0;
        private const int AllocWarnBurstLimit = 3;
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
        [ThreadStatic] private static bool _mapHotGate;
        private static double _frameBudgetMs = 1000.0 / 60.0;
        private static double _frameBudgetEmaMs;
        private const double BudgetAlpha = 0.05;
        private const double BudgetHeadroom = 1.20;
        private const double BudgetMinMs = 4.0;
        private const double BudgetMaxMs = 33.5;
        private static readonly double[] CommonPeriodsMs =
        {
            1000.0 / 240.0,
            1000.0 / 200.0,
            1000.0 / 180.0,
            1000.0 / 175.0,
            1000.0 / 170.0,
            1000.0 / 165.0,
            1000.0 / 160.0,
            1000.0 / 150.0,
            1000.0 / 144.0,
            1000.0 / 120.0,
            1000.0 / 100.0,
            1000.0 / 90.0,
            1000.0 / 85.0,
            1000.0 / 75.0,
            1000.0 / 72.0,
            1000.0 / 70.0,
            1000.0 / 60.0,
            1000.0 / 50.0,
            1000.0 / 48.0,
            1000.0 / 40.0,
            1000.0 / 30.0
        };
        private const double SnapEpsMs = 0.40;
        private const int OverBudgetStreakCap = 1_000;
        private const int HotEnableStreak = 2;
        private const double SpikeRunMs = 50.0;
        private const double SpikePausedMs = 80.0;
        private const double FlushOnHugeFrameMs = 200.0;
        private const long AllocSpikeBytes = 25_000_000;
        private const long WsSpikeBytes = 75_000_000;
        private const long ForceFlushAllocBytes = 150_000_000;
        private const long ForceFlushWsBytes = 250_000_000;
        private static int _overBudgetStreak;
        private static readonly ConcurrentDictionary<MethodBase, long> MapHotCooldowns =
            new ConcurrentDictionary<MethodBase, long>();
        private static long _mapHotLastPruneTs;
        private static Func<long> _getAllocForThread;
        private const double MapHotDurationMsThreshold = 1.0;
        private const long MapHotAllocThresholdBytes = 128_000;
        private const double MapHotCooldownSeconds = 0.05;
        private const int MapHotCooldownPruneLimit = 2_000;
        private const double MapHotCooldownPruneWindowMultiplier = 10.0;
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
        private const double BytesPerKiB = 1024.0;

        private struct AllocWarnBudget
        {
            public double NextReset;
            public int Count;
        }

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
        private const double MapScreenBackoffMs1 = 10.0; // if a frame takes ≥10ms, skip next 1 frame
        private const double MapScreenBackoffMs2 = 14.0; // if a frame takes ≥14ms, skip next 2 frames
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
                SafePatch("PatchMapScreenThrottle", () => PatchMapScreenThrottle(harmony));

                // High-level map/UI hooks (already working)
                SafePatch("TryPatchType(MapState)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.GameState.MapState", new[] { "OnTick", "OnMapModeTick", "OnFrameTick" }));
                SafePatch("TryPatchType(MapState2)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.MapState", new[] { "OnTick", "OnMapModeTick", "OnFrameTick" }));
                SafePatch("TryPatchType(CampaignEventDispatcher)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.CampaignEventDispatcher", new[] { "OnTick", "DailyTick", "HourlyTick" }));
                SafePatch("Slice Daily/Hourly",
                    () =>
                    {
                        TryPatchType(
                            harmony,
                            "TaleWorlds.CampaignSystem.CampaignEventDispatcher",
                            new[] { "DailyTick", "HourlyTick" },
                            prefix: typeof(SubModule).GetMethod(nameof(OnDailyTick_Prefix), HookBindingFlags),
                            prefix2: typeof(SubModule).GetMethod(nameof(OnHourlyTick_Prefix), HookBindingFlags));
                    });
                SafePatch("Slice CampaignEvents Daily/Hourly",
                    () =>
                    {
                        TryPatchType(
                            harmony,
                            "TaleWorlds.CampaignSystem.CampaignEvents",
                            new[] { "DailyTick", "HourlyTick", "OnDailyTick", "OnHourlyTick" },
                            prefix: typeof(SubModule).GetMethod(nameof(OnDailyTick_Prefix), HookBindingFlags),
                            prefix2: typeof(SubModule).GetMethod(nameof(OnHourlyTick_Prefix), HookBindingFlags));
                    });
                SafePatch("TryPatchType(MapScreen)", () => TryPatchType(harmony, "SandBox.View.Map.MapScreen", new[] { "OnFrameTick", "Tick", "OnTick" }));
                SafePatch("TryPatchType(UI)", () => TryPatchType(harmony, "TaleWorlds.GauntletUI.UIContext", new[] { "Update", "Tick" }));
                SafePatch("TryPatchType(Layer)", () => TryPatchType(harmony, "TaleWorlds.GauntletUI.GauntletLayer", new[] { "OnLateUpdate", "Tick" }));
                SafePatch("PatchMapScreenHotspots", () => PatchMapScreenHotspots(harmony));

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double SnapToVsyncIfClose(double ms)
        {
            var best = ms;
            var bestDiff = double.MaxValue;
            var periods = CommonPeriodsMs;
            for (int i = 0; i < periods.Length; i++)
            {
                var diff = Math.Abs(periods[i] - ms);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = periods[i];
                }
            }

            return bestDiff <= SnapEpsMs ? best : ms;
        }

        protected override void OnApplicationTick(float dt)
        {
            var paused = IsPaused();
            var onMap = IsOnMap();
            var desired = paused ? GCLatencyMode.Interactive : GCLatencyMode.SustainedLowLatency;
            if (GCSettings.LatencyMode != desired)
            {
                try { GCSettings.LatencyMode = desired; }
                catch { /* best-effort */ }
            }
            var nowTs = Stopwatch.GetTimestamp();
            double frameMs = (nowTs - _lastFrameTS) * TicksToMs;
            _lastFrameTS = nowTs;
            if (frameMs <= 0.0) frameMs = dt * 1000.0;

            var overBudget = false;
            if (onMap && !paused)
            {
                if (_frameBudgetEmaMs <= 0.0)
                {
                    _frameBudgetEmaMs = frameMs;
                }
                else
                {
                    var candidate = Math.Min(frameMs, _frameBudgetEmaMs * 1.5);
                    _frameBudgetEmaMs += (candidate - _frameBudgetEmaMs) * BudgetAlpha;
                }

                var snapped = SnapToVsyncIfClose(_frameBudgetEmaMs);
                var clamped = snapped;
                if (clamped < BudgetMinMs) clamped = BudgetMinMs;
                else if (clamped > BudgetMaxMs) clamped = BudgetMaxMs;
                _frameBudgetMs = clamped;

                overBudget = frameMs > (_frameBudgetMs * BudgetHeadroom);
                if (overBudget)
                {
                    var v = Interlocked.Increment(ref _overBudgetStreak);
                    if (v > OverBudgetStreakCap) Interlocked.Exchange(ref _overBudgetStreak, OverBudgetStreakCap);
                }
                else
                {
                    Interlocked.Exchange(ref _overBudgetStreak, 0);
                }
            }
            else
            {
                Interlocked.Exchange(ref _overBudgetStreak, 0);
            }
            var spikeLimit = paused ? SpikePausedMs : SpikeRunMs;
            if (onMap && frameMs > spikeLimit && _frameSpikeCD <= 0.0)
            {
                MapPerfLog.Warn($"FRAME spike {frameMs:F1} ms [{(paused ? "PAUSED" : "RUN")}]");
                if (frameMs > FlushOnHugeFrameMs) FlushSummary(true);
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

            if (onMap && allocDelta > AllocSpikeBytes && _memSpikeCD <= 0.0)
            {
                MapPerfLog.Warn($"ALLOC spike +{allocDelta / 1_000_000.0:F1} MB");
                _memSpikeCD = 5.0;
            }

            if (onMap && wsDelta > WsSpikeBytes && _memSpikeCD <= 0.0)
            {
                MapPerfLog.Warn($"WS spike +{wsDelta / 1_000_000.0:F1} MB");
                _memSpikeCD = 5.0;
            }

            if (allocDelta > ForceFlushAllocBytes || wsDelta > ForceFlushWsBytes)
                FlushSummary(true);

            _nextFlush -= dt;
            if (_frameSpikeCD > 0.0)
                _frameSpikeCD = Math.Max(0.0, _frameSpikeCD - dt);
            if (_memSpikeCD > 0.0)
                _memSpikeCD = Math.Max(0.0, _memSpikeCD - dt);
            if (_nextFlush <= 0.0)
            {
                _nextFlush = paused ? 5.0 : 2.0;
                if (onMap) FlushSummary(force: false);
            }

            // Drain slices even while paused to avoid backlog cliffs
            if (onMap)
            {
                var budget = paused ? 3.0 : (IsFastTime() ? 6.0 : 2.0);
                PeriodicSlicer.Pump(msBudget: budget);
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
                methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                return;
            }

            foreach (var m in methods)
            {
                if (m.IsSpecialName) continue;
                if (!methodNames.Any(n => MatchesMethodAlias(m.Name, n))) continue;
                if (m.ReturnType != typeof(void)) continue;
                if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true) continue;
                var declName = m.DeclaringType?.Name;
                if (declName != null && declName.IndexOf("d__", StringComparison.Ordinal) >= 0) continue;
                var ps = m.GetParameters();
                if (ps.Length > 2) continue;
                bool byref = false;
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (p.IsOut || p.ParameterType.IsByRef) { byref = true; break; }
                }
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

        private static void TryPatchType(object harmony, string fullName, string[] methodNames, MethodInfo prefix, MethodInfo prefix2)
        {
            if (prefix == null || prefix2 == null) return;
            var t = FindType(fullName);
            if (t == null) return;

            var ht = harmony.GetType();
            var harmonyAsm = ht.Assembly;
            var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (hmCtor == null || patchMi == null) return;

            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.IsSpecialName) continue;
                if (!methodNames.Any(n => MatchesMethodAlias(m.Name, n)) || m.ReturnType != typeof(void)) continue;
                var pre =
                    MatchesMethodAlias(m.Name, "OnDailyTick")
                        ? prefix
                        : MatchesMethodAlias(m.Name, "OnHourlyTick")
                            ? prefix2
                            : null;
                if (pre == null) continue;
                var ps = m.GetParameters();
                if (ps.Length != 0) continue;
                bool byref = false;
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (p.IsOut || p.ParameterType.IsByRef) { byref = true; break; }
                }
                if (byref) continue;
                var preHM = hmCtor.Invoke(new object[] { pre });
                try
                {
                    var hmInstType = preHM?.GetType();
                    var prProp = hmInstType?.GetProperty("priority");
                    var prField = hmInstType?.GetField("priority");
                    var targetType = prProp?.PropertyType ?? prField?.FieldType;
                    object highest = 400;
                    if (targetType != null && targetType.IsEnum)
                        highest = Enum.ToObject(targetType, 400);
                    try { if (prProp != null) prProp.SetValue(preHM, highest); } catch { }
                    try { if (prField != null) prField.SetValue(preHM, highest); } catch { }
                }
                catch
                {
                    // best-effort priority bump
                }
                try
                {
                    patchMi.Invoke(harmony, new object[] { m, preHM, null, null, null });
                }
                catch (Exception ex)
                {
                    MapPerfLog.Error($"Slice patch fail {t.FullName}.{m.Name}", ex);
                }
            }
        }

        // Handle both instance hubs (dispatcher) and static hubs (CampaignEvents) with reentry guard
        public static bool OnDailyTick_Prefix(object __instance, MethodBase __originalMethod)
        {
            if (PeriodicSlicer.ShouldBypass(__originalMethod)) return true;
            return !PeriodicSlicer.RedirectAny(__instance, __originalMethod, "OnDailyTick");
        }

        public static bool OnHourlyTick_Prefix(object __instance, MethodBase __originalMethod)
        {
            if (PeriodicSlicer.ShouldBypass(__originalMethod)) return true;
            return !PeriodicSlicer.RedirectAny(__instance, __originalMethod, "OnHourlyTick");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string GetMethodAlias(string methodName)
        {
            if (methodName == null) return null;
            if (string.Equals(methodName, "OnDailyTick", StringComparison.OrdinalIgnoreCase)) return "DailyTick";
            if (string.Equals(methodName, "DailyTick", StringComparison.OrdinalIgnoreCase)) return "OnDailyTick";
            if (string.Equals(methodName, "OnHourlyTick", StringComparison.OrdinalIgnoreCase)) return "HourlyTick";
            if (string.Equals(methodName, "HourlyTick", StringComparison.OrdinalIgnoreCase)) return "OnHourlyTick";
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool MatchesMethodAlias(string candidate, string methodName)
        {
            if (candidate == null || methodName == null) return false;
            if (string.Equals(candidate, methodName, StringComparison.OrdinalIgnoreCase)) return true;

            var alias = GetMethodAlias(methodName);
            if (alias != null && string.Equals(candidate, alias, StringComparison.OrdinalIgnoreCase))
                return true;

            var candidateAlias = GetMethodAlias(candidate);
            return candidateAlias != null
                   && string.Equals(candidateAlias, methodName, StringComparison.OrdinalIgnoreCase);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static MethodInfo GetZeroParamMethod(Type type, string methodName, BindingFlags flags)
        {
            if (type == null || methodName == null) return null;

            MethodInfo Lookup(string name)
            {
                if (name == null) return null;

                for (var search = type; search != null && search != typeof(object); search = search.BaseType)
                {
                    var found = search.GetMethod(name, flags, null, Type.EmptyTypes, null);
                    if (found != null) return found;
                }

                return null;
            }

            var mi = Lookup(methodName);
            if (mi != null) return mi;

            var alias = GetMethodAlias(methodName);
            return alias != null ? Lookup(alias) : null;
        }

        // --- Targeted perf tweak: throttle MapScreen.OnFrameTick under load ---
        private static void PatchMapScreenThrottle(object harmony)
        {
            try
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

                    // Try to set highest priority, but only if the shape matches
                    try
                    {
                        var prioProp = hmType.GetProperty("priority", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        var prioField = hmType.GetField("priority", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                        object highestBoxed = 400; // default to int
                        var prioEnum = harmonyAsm.GetType("HarmonyLib.Priority");
                        if (prioEnum != null && prioEnum.IsEnum)
                        {
                            try { highestBoxed = Enum.ToObject(prioEnum, 400); }
                            catch { highestBoxed = 400; }
                        }

                        if (prioProp != null)
                        {
                            var pt = prioProp.PropertyType;
                            if (pt.IsEnum) prioProp.SetValue(preHM, highestBoxed);
                            else if (pt == typeof(int)) prioProp.SetValue(preHM, 400);
                        }
                        if (prioField != null)
                        {
                            var ft = prioField.FieldType;
                            if (ft.IsEnum) prioField.SetValue(preHM, highestBoxed);
                            else if (ft == typeof(int)) prioField.SetValue(preHM, 400);
                        }
                    }
                    catch
                    {
                        /* priority is best-effort; ignore shape mismatches */
                    }

                    patchMi.Invoke(harmony, new object[] { target, preHM, null, null, null });
                    MapPerfLog.Info($"Patched MapScreen.{name} (throttle)");
                }
            }
            catch (Exception ex)
            {
                MapPerfLog.Error("PatchMapScreenThrottle failed", ex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldEnableMapHot(bool fastTime)
        {
            if (fastTime) return true;
            return Volatile.Read(ref _overBudgetStreak) >= HotEnableStreak;
        }

        // bool-prefix: return false to skip original map frame hooks when throttling
        public static bool MapScreenOnFrameTickPrefix(object __instance, MethodBase __originalMethod)
        {
            var methodName = __originalMethod?.Name;
            if (methodName == null) return true;

            if (string.Equals(methodName, "OnFrameTick", StringComparison.OrdinalIgnoreCase))
            {
                var paused = IsPaused();
                _mapScreenFastTimeValid = false;
                _mapScreenThrottleActive = false;
                _mapHotGate = false;

                if (__instance == null || paused)
                {
                    _skipMapOnFrameTick = false;
                    return true;
                }

                var fastTime = IsFastTime();
                _mapHotGate = ShouldEnableMapHot(fastTime);
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
                    _mapHotGate = false;
                    return false;
                }

                _skipMapOnFrameTick = false;
                return true;
            }

            if (!MapScreenFrameHooks.Contains(methodName)) return true;

            var hadFastTime = _mapScreenFastTimeValid;
            var cachedFastTime = _mapScreenFastTime;
            _mapScreenFastTimeValid = false;

            if (__instance == null || IsPaused())
            {
                _mapScreenThrottleActive = false;
                _skipMapOnFrameTick = false;
                _mapHotGate = false;
                return true;
            }

            bool fastTime2 = hadFastTime ? cachedFastTime : IsFastTime();
            if (!fastTime2)
            {
                _mapScreenThrottleActive = false;
                _skipMapOnFrameTick = false;
                _mapScreenSkipFrames = 0;
                _mapHotGate = ShouldEnableMapHot(false);
                return true;
            }

            if (_mapScreenThrottleActive || _mapScreenSkipFrames > 0)
            {
                _mapScreenThrottleActive = true;
                _skipMapOnFrameTick = true;
                _mapHotGate = false;
                return false;
            }

            _mapHotGate = ShouldEnableMapHot(true);
            return true;
        }

        private static void InitAllocCounter()
        {
            if (Volatile.Read(ref _getAllocForThread) != null) return;

            var mi = typeof(GC).GetMethod(
                "GetAllocatedBytesForCurrentThread",
                BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return;
            if (mi.ReturnType != typeof(long) || mi.GetParameters().Length != 0) return;

            try
            {
                var created = mi.CreateDelegate(typeof(Func<long>));
                if (created is Func<long> func)
                    Interlocked.CompareExchange(ref _getAllocForThread, func, null);
            }
            catch
            {
                // best-effort — fall back to total process allocs
            }
        }

        private static long GetThreadAllocs()
        {
            var func = Volatile.Read(ref _getAllocForThread);
            if (func == null)
            {
                InitAllocCounter();
                func = Volatile.Read(ref _getAllocForThread);
            }

            if (func != null)
            {
                try { return func(); }
                catch { Interlocked.Exchange(ref _getAllocForThread, null); }
            }

            return GC.GetTotalMemory(false);
        }

        public readonly struct MapHotState
        {
            public MapHotState(long t0, long a0)
            {
                this.t0 = t0;
                this.a0 = a0;
            }

            public long t0 { get; }
            public long a0 { get; }
        }

        public static void MapHotPrefix(out MapHotState __state)
        {
            if (!_mapHotGate)
            {
                __state = default;
                return;
            }

            var t0 = Stopwatch.GetTimestamp();
            var func = Volatile.Read(ref _getAllocForThread);
            if (func == null)
            {
                InitAllocCounter();
                func = Volatile.Read(ref _getAllocForThread);
            }

            var a0 = func != null ? GetThreadAllocs() : 0;
            __state = new MapHotState(t0, a0);
        }

        public static void MapHotPostfix(MethodBase __originalMethod, MapHotState __state)
        {
            if (__state.t0 == 0 || __originalMethod == null) return;

            var dtMs = (Stopwatch.GetTimestamp() - __state.t0) * TicksToMs;

            long dAlloc = 0;
            var allocFunc = Volatile.Read(ref _getAllocForThread);
            var hasAllocCounter = false;
            if (allocFunc != null)
            {
                dAlloc = GetThreadAllocs() - __state.a0;
                if (dAlloc < 0) dAlloc = 0;
                hasAllocCounter = Volatile.Read(ref _getAllocForThread) != null;
            }

            var durationThreshold = MapHotDurationMsThreshold;
            var allocThreshold = MapHotAllocThresholdBytes;

            if (hasAllocCounter)
            {
                if (dAlloc < allocThreshold && dtMs < durationThreshold) return;
            }
            else if (dtMs < durationThreshold)
            {
                return;
            }

            var nowTs = Stopwatch.GetTimestamp();
            var cooldownTicks = (long)(Stopwatch.Frequency * MapHotCooldownSeconds);

            if (cooldownTicks > 0 && MapHotCooldowns.Count > MapHotCooldownPruneLimit)
            {
                var lastPrune = Volatile.Read(ref _mapHotLastPruneTs);
                if (nowTs - lastPrune >= cooldownTicks &&
                    Interlocked.CompareExchange(ref _mapHotLastPruneTs, nowTs, lastPrune) == lastPrune)
                {
                    var pruneBefore = nowTs - (long)(cooldownTicks * MapHotCooldownPruneWindowMultiplier);
                    foreach (var kvp in MapHotCooldowns)
                    {
                        if (kvp.Value < pruneBefore)
                        {
                            MapHotCooldowns.TryRemove(kvp.Key, out _);
                        }
                    }
                }
            }

            var shouldLog = false;
            var newDeadline = nowTs + cooldownTicks;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (!MapHotCooldowns.TryGetValue(__originalMethod, out var next))
                {
                    if (MapHotCooldowns.TryAdd(__originalMethod, newDeadline))
                    {
                        shouldLog = true;
                        break;
                    }
                    continue;
                }

                if (nowTs < next) break;

                if (MapHotCooldowns.TryUpdate(__originalMethod, newDeadline, next))
                {
                    shouldLog = true;
                    break;
                }
            }

            if (shouldLog)
            {
                var owner = __originalMethod?.DeclaringType?.FullName ?? "<global>";
                var name = __originalMethod?.Name ?? "<unknown>";
                if (hasAllocCounter)
                {
                    MapPerfLog.Info(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "[maphot] {0}.{1}  {2:F2} ms, +{3:F1} KiB",
                            owner,
                            name,
                            dtMs,
                            dAlloc / BytesPerKiB));
                }
                else
                {
                    MapPerfLog.Info(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "[maphot] {0}.{1}  {2:F2} ms",
                            owner,
                            name,
                            dtMs));
                }
            }
        }

        private static void PatchMapScreenHotspots(object harmony)
        {
            var mapT = GetMapScreenType();
            if (mapT == null) return;

            var ht = harmony.GetType();
            var asm = ht.Assembly;
            var hmType = asm.GetType("HarmonyLib.HarmonyMethod")
                         ?? Type.GetType($"HarmonyLib.HarmonyMethod, {asm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (hmCtor == null || patchMi == null) return;

            bool Hit(string n) => n != null &&
                (n.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 n.IndexOf("party", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 n.IndexOf("visual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 n.IndexOf("effect", StringComparison.OrdinalIgnoreCase) >= 0);

            var preHM = hmCtor.Invoke(new object[] { typeof(SubModule).GetMethod(nameof(MapHotPrefix), HookBindingFlags) });
            var postHM = hmCtor.Invoke(new object[] { typeof(SubModule).GetMethod(nameof(MapHotPostfix), HookBindingFlags) });

            var seen = new HashSet<MethodBase>();

            void PatchType(Type t, BindingFlags flags)
            {
                var tn = t.Name;
                if (tn != null && (tn.IndexOf("<>", StringComparison.Ordinal) >= 0 ||
                                   tn.IndexOf("DisplayClass", StringComparison.OrdinalIgnoreCase) >= 0))
                    return; // skip compiler-generated containers
                MethodInfo[] methods;
                try
                {
                    methods = t.GetMethods(flags);
                }
                catch { return; }

                var declaredOnly = (flags & BindingFlags.DeclaredOnly) == BindingFlags.DeclaredOnly;

                foreach (var m in methods)
                {
                    if (!seen.Add(m)) continue;
                    if (declaredOnly && m.DeclaringType != t) continue;
                    if (m.IsSpecialName) continue;
                    var dn = m.DeclaringType?.Name;
                    if (dn != null && dn.IndexOf("d__", StringComparison.Ordinal) >= 0) continue;
                    if (string.Equals(m.Name, "MoveNext", StringComparison.Ordinal)) continue;
                    if (m.ReturnType != typeof(void)) continue;
                    var n = m.Name;
                    if (n != null && (n.StartsWith("get_", StringComparison.Ordinal) ||
                                       n.StartsWith("set_", StringComparison.Ordinal) ||
                                       n.StartsWith("add_", StringComparison.Ordinal) ||
                                       n.StartsWith("remove_", StringComparison.Ordinal)))
                        continue;
                    if (!Hit(n)) continue;
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    MethodBody body;
                    try { body = m.GetMethodBody(); }
                    catch { continue; }
                    if (body == null) continue;
                    try { patchMi.Invoke(harmony, new object[] { m, preHM, postHM, null, null }); }
                    catch (Exception ex) { MapPerfLog.Error($"MapHot patch fail {t.FullName}.{n}", ex); }
                }
            }

            const BindingFlags DeclaredInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            const BindingFlags InstanceAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            PatchType(mapT, DeclaredInstance);

            for (var bt = mapT.BaseType; bt != null && bt != typeof(object); bt = bt.BaseType)
            {
                PatchType(bt, InstanceAll);
            }
            Type[] allTypes;
            try { allTypes = mapT.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { allTypes = ex.Types.Where(x => x != null).ToArray(); }
            foreach (var nt in allTypes)
            {
                if (nt.Namespace == null) continue;
                if (!nt.Namespace.StartsWith("SandBox", StringComparison.Ordinal)) continue;
                if (Hit(nt.Name)) PatchType(nt, DeclaredInstance);
            }
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
                        _mapHotGate = false;
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
                        _mapHotGate = false;
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
                if (alloc > 8_000_000)
                {
                    if (TryConsumeAllocBudget(__originalMethod, tNow)
                        && (!_allocCd.TryGetValue(__originalMethod, out var next) || tNow >= next))
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

        private static bool TryConsumeAllocBudget(MethodBase method, double now)
        {
            if (method == null) return false;
            var allowed = false;
            _allocBudget.AddOrUpdate(
                method,
                _ =>
                {
                    allowed = true;
                    return new AllocWarnBudget { NextReset = now + AllocWarnWindowSeconds, Count = 1 };
                },
                (_, existing) =>
                {
                    if (now >= existing.NextReset)
                    {
                        allowed = true;
                        existing.NextReset = now + AllocWarnWindowSeconds;
                        existing.Count = 1;
                    }
                    else if (existing.Count < AllocWarnBurstLimit)
                    {
                        allowed = true;
                        existing.Count++;
                    }
                    else
                    {
                        existing.Count++;
                    }

                    return existing;
                });

            if (!allowed) return false;

            if (_allocBudget.Count > 1024)
            {
                foreach (var kv in _allocBudget)
                {
                    if (now >= kv.Value.NextReset + AllocLogTtl)
                        _allocBudget.TryRemove(kv.Key, out _);
                }
            }

            return true;
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

            foreach (var kv in _allocBudget)
            {
                if (now >= kv.Value.NextReset + AllocLogTtl)
                    _allocBudget.TryRemove(kv.Key, out _);
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
                        return i >= 0 ? i : 0;
                    case long l:
                        return ToInt(l);
                    case uint ui:
                        return ToInt(ui);
                    case ushort us:
                        return us;
                    case short s:
                        return s >= 0 ? (int)s : 0;
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

    static class PeriodicSlicer
    {
        private static readonly Queue<Action> _q = new Queue<Action>(4096);
        private static readonly object _lock = new object();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, int> _bypass
            = new System.Collections.Concurrent.ConcurrentDictionary<MethodBase, int>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _recentHubs
            = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        private static long _nextPumpLogTs;
        private static long _nextQueuePressureLogTs;
        private const double PumpLogCooldownSeconds = 0.5;
        private const double QueuePressureCooldownSeconds = 0.5;
        private const int QueuePressureThreshold = 15_000;

        public static bool RedirectDaily(object dispatcher)
            => Redirect(dispatcher, "OnDailyTick");

        public static bool RedirectHourly(object dispatcher)
            => Redirect(dispatcher, "OnHourlyTick");

        public static bool RedirectAny(object instance, MethodBase original, string methodName)
        {
            if (instance != null)
                return Redirect(instance, methodName);
            var t = original?.DeclaringType;
            return t != null && RedirectStatic(t, original, methodName);
        }

        private static bool Redirect(object dispatcher, string methodName)
        {
            try
            {
                if (dispatcher == null) return false;
                var actions = DiscoverSubscriberActions(dispatcher, methodName);
                if (actions.Count == 0) return false;

                int enq = 0;
                int afterCount = -1;
                lock (_lock)
                {
                    const int MaxQueued = 20000;
                    int room = Math.Max(0, MaxQueued - _q.Count);
                    enq = Math.Min(room, actions.Count);
                    for (int i = 0; i < enq; i++) _q.Enqueue(actions[i]);
                    if (enq < actions.Count)
                    {
                        var dropped = actions.Count - enq;
                        var shown = Math.Min(dropped, 5000);
                        var suffix = dropped > shown ? "+" : string.Empty;
                        MapPerfLog.Warn($"[slice] dropped {shown}{suffix} (queue full)");
                    }
                    afterCount = _q.Count;
                }
                if (afterCount >= QueuePressureThreshold) TryLogQueuePressure_NoLock(afterCount);
                if (enq > 0)
                {
                    var shown = Math.Min(enq, 5000);
                    var suffix = enq > shown ? "+" : string.Empty;
                    MapPerfLog.Info($"[slice] queued {shown}{suffix} {methodName} handlers");
                }
                return enq > 0;
            }
            catch (Exception ex)
            {
                MapPerfLog.Error($"slice redirect {methodName} failed", ex);
                return false;
            }
        }

        private static bool RedirectStatic(Type hubType, MethodBase original, string methodName)
        {
            try
            {
                if (hubType == null) return false;
                var actions = DiscoverSubscriberActions(hubType, methodName);
                var key = $"{hubType.FullName}:{methodName}";
                if (actions.Count == 0)
                {
                    if (original is MethodInfo mi)
                    {
                        if (mi.IsAbstract || mi.ContainsGenericParameters || mi.GetParameters().Length != 0)
                            return false;
                    }
                    else
                    {
                        return false;
                    }

                    if (!EnqueueOnce(key, 50.0))
                        return true;

                    int afterCount;
                    lock (_lock)
                    {
                        _q.Enqueue(() =>
                        {
                            EnterBypass(original);
                            try { mi.Invoke(null, null); }
                            finally { ExitBypass(original); }
                        });
                        afterCount = _q.Count;
                    }
                    if (afterCount >= QueuePressureThreshold) TryLogQueuePressure_NoLock(afterCount);
                    MapPerfLog.Info($"[slice] deferred hub {hubType.FullName}.{methodName} (fallback)");
                    return true;
                }

                if (!EnqueueOnce(key, 50.0))
                    return true;

                int enq = 0;
                int after;
                lock (_lock)
                {
                    const int MaxQueued = 20000;
                    int room = Math.Max(0, MaxQueued - _q.Count);
                    enq = Math.Min(room, actions.Count);
                    for (int i = 0; i < enq; i++) _q.Enqueue(actions[i]);
                    if (enq < actions.Count)
                    {
                        var dropped = actions.Count - enq;
                        var shown = Math.Min(dropped, 5000);
                        var suffix = dropped > shown ? "+" : string.Empty;
                        MapPerfLog.Warn($"[slice] dropped {shown}{suffix} (queue full)");
                    }
                    after = _q.Count;
                }
                if (after >= QueuePressureThreshold) TryLogQueuePressure_NoLock(after);
                if (enq > 0)
                {
                    var shown = Math.Min(enq, 5000);
                    var suffix = enq > shown ? "+" : string.Empty;
                    MapPerfLog.Info($"[slice] queued {shown}{suffix} {methodName} handlers (static hub)");
                }
                return enq > 0;
            }
            catch (Exception ex)
            {
                MapPerfLog.Error($"slice redirect static {methodName} failed", ex);
                return false;
            }
        }

        public static void Pump(double msBudget = 2.0, int hardCap = 256)
        {
            if (msBudget <= 0.0) return;
            lock (_lock)
            {
                if (_q.Count == 0) return;
            }
            long start = Stopwatch.GetTimestamp();
            long ticksBudget = (long)(msBudget * (Stopwatch.Frequency / 1000.0));
            int done = 0;
            // allow larger batch while paused to drain backlog safely
            if (msBudget >= 3.0 && hardCap < 1024) hardCap = 1024;
            if (msBudget >= 6.0 && hardCap < 2048) hardCap = 2048;

            while (done < hardCap)
            {
                Action a = null;
                lock (_lock)
                {
                    if (_q.Count == 0) break;
                    a = _q.Dequeue();
                }
                try { a(); }
                catch (Exception ex) { MapPerfLog.Error("[slice] handler error", ex); }
                done++;
                if ((Stopwatch.GetTimestamp() - start) > ticksBudget) break;
            }

            int remaining;
            lock (_lock)
            {
                remaining = _q.Count;
            }

            if (done > 0 && (remaining == 0 || done >= 64))
            {
                var now = Stopwatch.GetTimestamp();
                var nextAllowed = Volatile.Read(ref _nextPumpLogTs);
                if (now >= nextAllowed)
                {
                    MapPerfLog.Info($"[slice] pumped {done} (rem {remaining})");
                    Volatile.Write(ref _nextPumpLogTs,
                        now + (long)(Stopwatch.Frequency * PumpLogCooldownSeconds));
                }
            }
        }

        private static bool EnqueueOnce(string key, double windowMs)
        {
            if (string.IsNullOrEmpty(key) || windowMs <= 0.0) return true;
            long now = Stopwatch.GetTimestamp();
            long windowTicks = (long)(windowMs * (Stopwatch.Frequency / 1000.0));

            while (true)
            {
                if (_recentHubs.TryGetValue(key, out var prev))
                {
                    if (now - prev <= windowTicks)
                        return false;
                    if (_recentHubs.TryUpdate(key, now, prev))
                        break;
                    continue;
                }

                if (_recentHubs.TryAdd(key, now))
                    break;
            }

            if (_recentHubs.Count > 2048)
            {
                foreach (var kv in _recentHubs)
                {
                    if (now - kv.Value > windowTicks * 4)
                        _recentHubs.TryRemove(kv.Key, kv.Value);
                }
            }

            return true;
        }

        private static void TryLogQueuePressure_NoLock(int count)
        {
            if (count < QueuePressureThreshold) return;
            var now = Stopwatch.GetTimestamp();
            var next = Volatile.Read(ref _nextQueuePressureLogTs);
            if (now < next) return;
            MapPerfLog.Info($"[slice] queue pressure {count}");
            Volatile.Write(ref _nextQueuePressureLogTs,
                now + (long)(Stopwatch.Frequency * QueuePressureCooldownSeconds));
        }

        private static List<Action> DiscoverSubscriberActions(object dispatcher, string methodName)
        {
            var ret = new List<Action>(256);
            var t = dispatcher.GetType();
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            bool NameIsTarget(string n) => SubModule.MatchesMethodAlias(n, methodName);

            void AddIfInvokable(object target)
            {
                if (target == null) return;

                if (target is Delegate del)
                {
                    foreach (var one in del.GetInvocationList())
                    {
                        if (one.Method.GetParameters().Length != 0) continue;
                        try
                        {
                            var action = (Action)one.Method.CreateDelegate(typeof(Action), one.Target);
                            ret.Add(action);
                        }
                        catch
                        {
                            var o = one;
                            ret.Add(() => o.DynamicInvoke(Array.Empty<object>()));
                        }
                    }
                    return;
                }

                var mi = SubModule.GetZeroParamMethod(target.GetType(), methodName, F);
                if (mi != null && !mi.IsAbstract)
                {
                    try
                    {
                        var action = (Action)mi.CreateDelegate(typeof(Action), target);
                        ret.Add(action);
                    }
                    catch
                    {
                        ret.Add(() => mi.Invoke(target, null));
                    }
                    return;
                }

                TryExpand(target, inner =>
                {
                    if (!ReferenceEquals(inner, target)) AddIfInvokable(inner);
                });
            }

            bool NameLooksRelevant(string n)
                => n != null && (n.IndexOf("daily", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("hourly", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("tick", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("subscriber", StringComparison.OrdinalIgnoreCase) >= 0);

            foreach (var f in t.GetFields(F))
            {
                if (!NameLooksRelevant(f.Name)) continue;
                var val = f.GetValue(dispatcher);
                TryExpand(val, AddIfInvokable);
            }

            foreach (var p in t.GetProperties(F))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                if (!NameLooksRelevant(p.Name)) continue;
                object val = null;
                try { val = p.GetValue(dispatcher, null); }
                catch { }
                TryExpand(val, AddIfInvokable);
            }

            if (ret.Count == 0)
            {
                foreach (var f in t.GetFields(F))
                    TryExpand(f.GetValue(dispatcher), AddIfInvokable);
            }

            return ret;
        }

        private static List<Action> DiscoverSubscriberActions(Type hubType, string methodName)
        {
            var ret = new List<Action>(256);
            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            bool NameIsTarget(string n) => SubModule.MatchesMethodAlias(n, methodName);

            void AddIfInvokable(object target)
            {
                if (target == null) return;
                if (target is Delegate del)
                {
                    foreach (var one in del.GetInvocationList())
                    {
                        try
                        {
                            var a = (Action)one;
                            ret.Add(a);
                        }
                        catch
                        {
                            var o = one;
                            ret.Add(() => o.DynamicInvoke(Array.Empty<object>()));
                        }
                    }
                    return;
                }

                var mi = SubModule.GetZeroParamMethod(target.GetType(), methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi != null && !mi.IsAbstract)
                {
                    try
                    {
                        var action = (Action)mi.CreateDelegate(typeof(Action), target);
                        ret.Add(action);
                    }
                    catch
                    {
                        ret.Add(() => mi.Invoke(target, null));
                    }
                    return;
                }

                TryExpand(target, inner =>
                {
                    if (!ReferenceEquals(inner, target)) AddIfInvokable(inner);
                });
            }

            EventInfo[] events;
            try { events = hubType.GetEvents(F); }
            catch { events = Array.Empty<EventInfo>(); }

            foreach (var ev in events)
            {
                if (!NameIsTarget(ev.Name)) continue;
                var fi = hubType.GetField(ev.Name, F)
                         ?? hubType.GetField($"_{ev.Name}", F)
                         ?? hubType.GetField($"m_{ev.Name}", F);
                if (fi != null) TryExpand(fi.GetValue(null), AddIfInvokable);
            }

            FieldInfo[] fields;
            try { fields = hubType.GetFields(F); }
            catch { fields = Array.Empty<FieldInfo>(); }

            foreach (var f in fields)
            {
                var ft = f.FieldType;
                if (NameIsTarget(f.Name)
                    || ft.Name.IndexOf("Event", StringComparison.OrdinalIgnoreCase) >= 0
                    || typeof(Delegate).IsAssignableFrom(ft))
                {
                    TryExpand(f.GetValue(null), AddIfInvokable);
                }
            }

            return ret;
        }

        public static bool ShouldBypass(MethodBase m)
            => m != null && _bypass.TryGetValue(m, out var c) && c > 0;

        private static void EnterBypass(MethodBase m)
        {
            if (m == null) return;
            _bypass.AddOrUpdate(m, 1, (_, v) => v + 1);
        }

        private static void ExitBypass(MethodBase m)
        {
            if (m == null) return;
            _bypass.AddOrUpdate(m, 0, (_, v) => Math.Max(0, v - 1));
        }

        private static void TryExpand(object container, Action<object> accept)
        {
            if (container == null) return;

            accept(container);

            if (container is string)
                return;

            if (container is System.Collections.IEnumerable en)
            {
                foreach (var item in en) accept(item);
                return;
            }

            var t = container.GetType();
            var idxProp = t.GetProperty("Values", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (idxProp != null)
            {
                try
                {
                    if (idxProp.GetValue(container) is System.Collections.IEnumerable vals)
                        foreach (var v in vals) accept(v);
                }
                catch { }
            }
        }
    }
}
