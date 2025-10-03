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
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.InputSystem;

namespace MapPerfProbe
{
    public class SubModule : MBSubModuleBase
    {
        private const string HId = "mmq.mapperfprobe";
        internal static string HarmonyId => HId;
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
        [ThreadStatic] private static int _uiContextDepth;
        private static bool _debugToggleComboActive;
        private static bool _debugOverride;
        private static double _frameBudgetMs = 1000.0 / 60.0;
        private static double _frameBudgetEmaMs;
        private static double BudgetAlpha => MapPerfConfig.BudgetAlpha;
        private static double BudgetHeadroom => MapPerfConfig.BudgetHeadroom;
        private static double BudgetMinMs => MapPerfConfig.BudgetMinMs;
        private static double BudgetMaxMs => MapPerfConfig.BudgetMaxMs;
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
        private static double SnapEpsMs => MapPerfConfig.SnapEpsMs;
        private const int OverBudgetStreakCap = 1_000;
        private static int HotEnableStreak => MapPerfConfig.HotEnableStreak;
        private static double SpikeRunMs => MapPerfConfig.SpikeRunMs;
        private static double SpikePausedMs => MapPerfConfig.SpikePausedMs;
        private static double PostSpikeNoPumpSec => MapPerfConfig.PostSpikeNoPumpSec;
        private static double FlushOnHugeFrameMs => MapPerfConfig.FlushOnHugeFrameMs;
        private static long AllocSpikeBytes => MapPerfConfig.AllocSpikeBytes;
        private static long WsSpikeBytes => MapPerfConfig.WsSpikeBytes;
        private static long ForceFlushAllocBytes => MapPerfConfig.ForceFlushAllocBytes;
        private static long ForceFlushWsBytes => MapPerfConfig.ForceFlushWsBytes;
        private static int _overBudgetStreak;
        private static readonly ConcurrentDictionary<MethodBase, long> MapHotCooldowns =
            new ConcurrentDictionary<MethodBase, long>();
        private static long _mapHotLastPruneTs;
        private static Func<long> _getAllocForThread;
        private static double MapHotDurationMsThreshold => MapPerfConfig.MapHotDurationMsThreshold;
        private static long MapHotAllocThresholdBytes => MapPerfConfig.MapHotAllocThresholdBytes;
        private static double MapHotCooldownSeconds => MapPerfConfig.MapHotCooldownSeconds;
        private const int MapHotCooldownPruneLimit = 2_000;
        private const double MapHotCooldownPruneWindowMultiplier = 10.0;
        private const double RootChildBaseCutoffMs = 0.5;
        private const double RootBurstMinTotalMs = 8.0;
        private const double RootBurstCooldownSeconds = 0.25;
        private static double _nextRootBurstAllowed;
        private static readonly ConcurrentDictionary<MethodBase, double> RootAgg =
            new ConcurrentDictionary<MethodBase, double>();
        private static readonly ConcurrentDictionary<Type, Func<object, int?>> CountResolvers =
            new ConcurrentDictionary<Type, Func<object, int?>>();
        private static readonly ConcurrentDictionary<MethodInfo, long> _lastDailyIdx =
            new ConcurrentDictionary<MethodInfo, long>();
        private static readonly ConcurrentDictionary<MethodInfo, long> _lastHourlyIdx =
            new ConcurrentDictionary<MethodInfo, long>();
        private static readonly ConcurrentDictionary<MethodInfo, long> _lastWeeklyIdx =
            new ConcurrentDictionary<MethodInfo, long>();
        private static readonly ConcurrentDictionary<string, double> _slowLogNext =
            new ConcurrentDictionary<string, double>();
        private static double _slowLogPruneNext;
        private const int SlowLogPruneLimit = 2_048;
        private const double SlowLogPruneIntervalSeconds = 30.0;
        private const double SlowLogRetainSeconds = 30.0;
        private static long _dailyPrefixHits;
        private static double _recentOverBudgetUntilSec;
        private static int HardNoEnqueueQLen => MapPerfConfig.PeriodicQueueHardCap;
        private static double _drainBoostUntilSec;
        private static double _noPumpUntilSec;
        private static Type _campaignBehaviorManagerType;
        private static PropertyInfo _campaignBehaviorManagerProp;
        private static PropertyInfo _campaignBehaviorsProp;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double NowSec() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BoostDrain(double seconds = 3.0)
        {
            var dur = Math.Max(0.0, seconds);
            var until = NowSec() + dur;
            Volatile.Write(ref _drainBoostUntilSec, until);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldDeferPeriodic(MethodBase hub)
        {
            PeriodicSlicer.GetQueueStats(out var queueLength, out _, out _);
            if (queueLength >= HardNoEnqueueQLen) return false;

            // Continuations/drain use null hub – keep draining while backlog exists.
            if (hub == null)
                return queueLength > 0;

            if (!MapPerfConfig.EnableMapThrottle) return false;
            if (!IsOnMap()) return false;

            return HotOrRecent();
        }

        // Global enqueue gate for PeriodicSlicer
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool MayEnqueueNow()
        {
            PeriodicSlicer.GetQueueStats(out var qlen, out _, out _);
            if (qlen >= HardNoEnqueueQLen) return false;
            return qlen < MapPerfConfig.PumpBacklogBoostThreshold;
        }

        internal static int MaxQueueForEnqueue => HardNoEnqueueQLen;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool HotOrRecent()
        {
            var recent = NowSec() < Volatile.Read(ref _recentOverBudgetUntilSec);
            return Volatile.Read(ref _overBudgetStreak) > 0 || recent;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool ShouldLogSlow(string key, double windowSec = 5.0)
        {
            var now = NowSec();
            var pruneDue = now >= Volatile.Read(ref _slowLogPruneNext);
            if (_slowLogNext.Count > SlowLogPruneLimit || pruneDue)
            {
                var next = Volatile.Read(ref _slowLogPruneNext);
                if (now >= next &&
                    Interlocked.CompareExchange(ref _slowLogPruneNext, now + SlowLogPruneIntervalSeconds, next) == next)
                    PruneSlowLogCache(now);
            }
            if (_slowLogNext.TryGetValue(key, out var until) && now < until) return false;
            _slowLogNext[key] = now + windowSec;
            return true;
        }
        private static void PruneSlowLogCache(double now)
        {
            if (_slowLogNext.IsEmpty) return;
            foreach (var kv in _slowLogNext)
            {
                if (kv.Value < now - SlowLogRetainSeconds)
                    _slowLogNext.TryRemove(kv.Key, out _);
            }
        }
        private static void ClearRunGates()
        {
            _lastDailyIdx.Clear();
            _lastHourlyIdx.Clear();
            _lastWeeklyIdx.Clear();
            _slowLogNext.Clear();
            _dispatcherChildCursor.Clear();
            Volatile.Write(ref _slowLogPruneNext, 0.0);
        }
        private static readonly object MapScreenProbeLock = new object();
        private static double MapScreenProbeDtThresholdMs => MapPerfConfig.MapScreenProbeDtThresholdMs;
        private static List<MapFieldProbe> _mapScreenProbes;
        private static Type _mapScreenProbeType;
        private static Type _mapScreenType;
        private static double _mapScreenNextLog;
        private static double _nextFilterLog = 10.0;
        private static readonly StringBuilder MapScreenLogBuilder = new StringBuilder(320);
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
        private static Type _gsmType;
        private static PropertyInfo _gsmCurrentProp;
        private static PropertyInfo _gsmActiveStateProp;
        private static readonly double TicksToMsConst = 1000.0 / Stopwatch.Frequency;
        private static readonly double MsToTicksConst = Stopwatch.Frequency / 1000.0;
        // Per-frame cheap flags (avoid reflection in PeriodicSlicer)
        private static volatile bool _snapPaused;
        private static volatile bool _snapFast;
        private static bool _lastEnabled;
        private static int _simSkipToken;
        private static int _lastSimEveryN;
        internal static bool PausedSnapshot => _snapPaused;
        internal static bool FastSnapshot => _snapFast;
        internal static bool FastOrPausedSnapshot => _snapPaused || _snapFast;
        private static double PumpBudgetRunMs => MapPerfConfig.PumpBudgetRunMs;
        private static double PumpBudgetFastMs => MapPerfConfig.PumpBudgetFastMs;
        private static double PumpBudgetRunBoostMs => MapPerfConfig.PumpBudgetRunBoostMs;
        private static double PumpBudgetFastBoostMs => MapPerfConfig.PumpBudgetFastBoostMs;
        internal static int PumpBacklogBoostThreshold => MapPerfConfig.PumpBacklogBoostThreshold;
        private static double PumpBudgetRunCapMs => MapPerfConfig.PumpBudgetRunCapMs;
        private static double PumpBudgetFastCapMs => MapPerfConfig.PumpBudgetFastCapMs;
        private static double PumpTailMinRunMs => MapPerfConfig.PumpTailMinRunMs;
        private static double PumpTailMinFastMs => MapPerfConfig.PumpTailMinFastMs;
        private static double PumpPauseTrickleMapMs => MapPerfConfig.PumpPauseTrickleMapMs;
        private static double PumpPauseTrickleMenuMs => MapPerfConfig.PumpPauseTrickleMenuMs;
        private const int ChildInlineAllowEveryN = 120;
        private const int ChildInlineAllowanceMin = 8;
        private const int ChildInlineAllowanceHalfThresholdPct = 95;
        private const int ChildInlineAllowanceQuarterThresholdPct = 97;
        private const int ChildDropMethodWindowCap = 64;
        private const int ChildInlineTokenClamp = 1_000_000;
        private static int _childBacklogToken;
        private static int _childBackpressureSkips;
        private static readonly ConcurrentDictionary<MethodBase, int> _childBackpressureSkipsByMethod =
            new ConcurrentDictionary<MethodBase, int>();
        internal static double TicksToMs => TicksToMsConst;
        internal static double MsToTicks => MsToTicksConst;
        private const double BytesPerKiB = 1024.0;
        private enum FrameMode
        {
            Unknown,
            Run,
            Fast,
            Paused
        }

        private static FrameMode _lastFrameMode;
        private static bool _wasOnMap;
        private const byte StateFlagSkip = 1;
        private const byte StateFlagUiContext = 2;

        internal static void ResetChildBackpressureCounters()
        {
            Interlocked.Exchange(ref _childBacklogToken, 0);
            Interlocked.Exchange(ref _childBackpressureSkips, 0);
            _childBackpressureSkipsByMethod.Clear();
        }

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
                   || string.Equals(n, "PeriodicQuarterDailyTick", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "TickPeriodicEvents", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "PeriodicDailyTick", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "PeriodicHourlyTick", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "QuarterDailyPartyTick", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "TickPartialHourlyAi", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "AiHourlyTick", StringComparison.OrdinalIgnoreCase);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsUiContextMethod(MethodBase method)
        {
            var typeName = method?.DeclaringType?.FullName;
            if (string.IsNullOrEmpty(typeName)) return false;
            return typeName.IndexOf("TaleWorlds.GauntletUI.UIContext", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsPerFrameHook(MethodBase m)
        {
            var t = m?.DeclaringType?.FullName;
            if (t == null) return false;
            if (t.IndexOf("SandBox.View.Map.MapScreen", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("TaleWorlds.CampaignSystem.GameState.MapState", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("TaleWorlds.GauntletUI.UIContext", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("TaleWorlds.GauntletUI.GauntletLayer", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("TaleWorlds.Engine.Screens.ScreenBase", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static long _lastFrameTS = Stopwatch.GetTimestamp();
        private static readonly int[] _gcLast = new int[3];
        private static readonly int[] _gcAgg = new int[3];
        private static double _nextFlush = 0.0;
        private static long _lastAlloc = GC.GetTotalMemory(false);
        private static long _lastWs = GetWS();
        private static double _lastFrameMsSnap;
        private static double _desyncDebtMs;
        private static int _frameSeq;
        private static int _frameSeqStamped;
        private static int _debtSeqApplied;
        private static int _forcedCatchupSeq;
        private static double _forcedCatchupDebtSnap;
        private static double _nextDebtLog;
        private static double _forcedCatchupCooldown;
        private static double _frameSpikeCD = 0.0;
        private static double _memSpikeCD = 0.0;

        // --- MapScreen throttling (real perf tweak) ---
        private static GCLatencyMode _prevGcMode = GCSettings.LatencyMode;
        private static int _mapScreenSkipFrames;
        private static double MapScreenBackoffMs1 => MapPerfConfig.MapScreenBackoffMs1;
        private static double MapScreenBackoffMs2 => MapPerfConfig.MapScreenBackoffMs2;
        private static int MapScreenSkipFrames1 => MapPerfConfig.MapScreenSkipFrames1;
        private static int MapScreenSkipFrames2 => MapPerfConfig.MapScreenSkipFrames2;
        private static int MapScreenSkipFramesCap => MapPerfConfig.MapScreenSkipFramesCap;

        protected override void OnSubModuleLoad()
        {
            if (_didPatch) return;
            _didPatch = true;
            MapPerfLog.Info("=== MapPerfProbe start ===");

            try { _ = MapPerfSettings.Instance; }
            catch { /* ignore if MCM not present */ }

            _lastEnabled = MapPerfConfig.Enabled;
            _lastSimEveryN = MapPerfConfig.SimTickEveryNSkipped;
            if (!_lastEnabled)
                DisableRuntime();

            MsgFilter.RefreshFamilyMaskFromConfig();

            try
            {
                var harmony = CreateHarmony();
                if (harmony == null)
                {
                    MapPerfLog.Warn("Harmony not found; only frame/GC logging.");
                    _didPatch = false;
                    return;
                }

                try
                {
                    var targetAssembly = typeof(PeriodicHubDeferrer).Assembly;
                    if (harmony is HarmonyLib.Harmony typedHarmony)
                    {
                        typedHarmony.PatchAll(targetAssembly);
                    }
                    else
                    {
                        var patchAllMi = harmony.GetType().GetMethod("PatchAll", new[] { typeof(Assembly) });
                        patchAllMi?.Invoke(harmony, new object[] { targetAssembly });
                    }
                }
                catch (Exception ex)
                {
                    MapPerfLog.Warn($"PatchAll failed: {ex.Message}");
                }

                // GC latency tuning – reduce full-blocking pauses during map play
                _prevGcMode = GCSettings.LatencyMode;
                if (_lastEnabled)
                {
                    try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; }
                    catch { /* best-effort on Mono/older runtimes */ }
                }

                // IMPORTANT: after bootstrapping the deferrer assembly, apply the throttle patch
                // before broader instrumentation so its bool-prefix can skip the original when needed.
                SafePatch("PatchMapScreenThrottle", () => PatchMapScreenThrottle(harmony));

                // High-level map/UI hooks (already working)
                SafePatch("TryPatchType(MapState)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.GameState.MapState", new[] { "OnTick", "OnMapModeTick", "OnFrameTick" }));
                SafePatch("TryPatchType(MapState2)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.MapState", new[] { "OnTick", "OnMapModeTick", "OnFrameTick" }));
                SafePatch("Desync MapState", () => PatchDesyncMapState(harmony));
                SafePatch("TryPatchType(CampaignEventDispatcher)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.CampaignEventDispatcher", new[] { "OnTick", "DailyTick", "HourlyTick" }));
                SafePatch("Slice Daily/Hourly",
                    () =>
                    {
                        TryPatchType(
                            harmony,
                            "TaleWorlds.CampaignSystem.CampaignEventDispatcher",
                            new[] { "DailyTick", "HourlyTick", "OnDailyTick", "OnHourlyTick" },
                            prefix: typeof(SubModule).GetMethod(nameof(OnDailyTick_Prefix), HookBindingFlags),
                            prefix2: typeof(SubModule).GetMethod(nameof(OnHourlyTick_Prefix), HookBindingFlags));
                    });
                SafePatch("patch all overloads",
                    () =>
                    {
                        var t = Type.GetType("TaleWorlds.CampaignSystem.Campaign, TaleWorlds.CampaignSystem");
                        if (t == null) return;
                        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                        var pre = typeof(SubModule).GetMethod(nameof(DeferCampaignDailyTick_Prefix), HookBindingFlags);
                        if (pre == null) return;

                        // HarmonyMethod via Reflection
                        var ht = harmony.GetType();
                        var harmonyAsm = ht.Assembly;
                        var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                                   ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
                        if (hmType == null) return;
                        var hmCtor = hmType.GetConstructor(new[] { typeof(MethodInfo) });

                        var patchMi = ht.GetMethod("Patch", BindingFlags.Instance | BindingFlags.Public);
                        if (patchMi == null) return;

                        foreach (var mi in t.GetMethods(F))
                        {
                            if (!string.Equals(mi.Name, "DailyTick", StringComparison.Ordinal)) continue;
                            if (mi.IsAbstract) continue;
                            var preHM = hmCtor?.Invoke(new object[] { pre });
                            if (preHM != null)
                            {
                                try
                                {
                                    MemberInfo pr = hmType.GetProperty("priority");
                                    if (pr == null) pr = hmType.GetField("priority");
                                    if (preHM != null && pr != null)
                                    {
                                        var tProp = (pr as PropertyInfo)?.PropertyType ?? (pr as FieldInfo)?.FieldType;
                                        object val = 400;
                                        if (tProp != null && tProp.IsEnum)
                                        {
                                            var veryHigh = Enum.GetNames(tProp).FirstOrDefault(n => n.Equals("VeryHigh", StringComparison.OrdinalIgnoreCase));
                                            val = veryHigh != null ? Enum.Parse(tProp, veryHigh) : Enum.ToObject(tProp, 400);
                                        }
                                        if (pr is PropertyInfo pi) pi.SetValue(preHM, val);
                                        else ((FieldInfo)pr).SetValue(preHM, val);
                                    }
                                }
                                catch { /* best effort */ }
                            }
                            try { patchMi.Invoke(harmony, new object[] { mi, preHM, null, null, null }); }
                            catch { /* weiter */ }
                        }
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
                SafePatch("Slice Dispatcher periodics (qtr/hourly/ai/weekly)",
                    () =>
                    {
                        TryPatchType(
                            harmony,
                            "TaleWorlds.CampaignSystem.CampaignEventDispatcher",
                            new[] { "QuarterDailyPartyTick", "TickPartialHourlyAi", "AiHourlyTick" },
                            typeof(SubModule).GetMethod(nameof(PeriodicHub_Prefix), HookBindingFlags));
                        TryPatchType(
                            harmony,
                            "TaleWorlds.CampaignSystem.CampaignEventDispatcher",
                            new[] { "WeeklyTick" },
                            typeof(SubModule).GetMethod(nameof(OnWeeklyTick_Prefix), HookBindingFlags));
                    });
                SafePatch("Slice CampaignEvents periodics (static hub)",
                    () =>
                    {
                        TryPatchType(
                            harmony,
                            "TaleWorlds.CampaignSystem.CampaignEvents",
                            new[] { "QuarterDailyPartyTick", "TickPartialHourlyAi", "AiHourlyTick" },
                            typeof(SubModule).GetMethod(nameof(PeriodicHub_Prefix), HookBindingFlags));
                        TryPatchType(
                            harmony,
                            "TaleWorlds.CampaignSystem.CampaignEvents",
                            new[] { "WeeklyTick" },
                            typeof(SubModule).GetMethod(nameof(OnWeeklyTick_Prefix), HookBindingFlags));
                    });
                SafePatch("Slice PeriodicEventManager helpers",
                    () => TryPatchType(
                        harmony,
                        "TaleWorlds.CampaignSystem.CampaignPeriodicEventManager",
                        new[] { "TickPartialHourlyAi" },
                        typeof(SubModule).GetMethod(nameof(PeriodicHub_Prefix), HookBindingFlags)));
                SafePatch("Defer CampaignPeriodicEventManager periodics",
                    () => TryPatchType(
                        harmony,
                        "TaleWorlds.CampaignSystem.CampaignPeriodicEventManager",
                        new[] { "PeriodicDailyTick", "PeriodicHourlyTick", "PeriodicQuarterDailyTick" },
                        typeof(SubModule).GetMethod(nameof(DeferPeriodicChild_Prefix), HookBindingFlags)));
                SafePatch("TryPatchType(MapScreen)", () => TryPatchType(harmony, "SandBox.View.Map.MapScreen", new[] { "OnFrameTick", "Tick", "OnTick" }));
                SafePatch("TryPatchType(UI)", () => TryPatchType(harmony, "TaleWorlds.GauntletUI.UIContext", new[] { "Update", "Tick" }));
                SafePatch("TryPatchType(Layer)", () => TryPatchType(harmony, "TaleWorlds.GauntletUI.GauntletLayer", new[] { "OnLateUpdate", "Tick" }));
                SafePatch("PatchMapScreenHotspots", () => PatchMapScreenHotspots(harmony));

                // 🔎 NEW: instrument the actual campaign behaviors that daily/hourly logic calls into
                SafePatch("PatchBehaviorTicks", () => PatchBehaviorTicks(harmony));
                SafePatch("PatchCampaignCoreTicks", () => PatchCampaignCoreTicks(harmony));
                SafePatch("PatchDispatcherFallback", () => PatchDispatcherFallback(harmony));

                if (MapPerfConfig.DebugLogging)
                {
                    MapPerfLog.Info($"[boot] DailyTick-prefix hits so far: {Interlocked.Read(ref _dailyPrefixHits)}");
                    Task.Run(async () =>
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            await Task.Delay(20000);
                            MapPerfLog.Info($"[watch] DailyTick-prefix hits: {Interlocked.Read(ref _dailyPrefixHits)}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                MapPerfLog.Error("OnSubModuleLoad fatal", ex);
                try { GCSettings.LatencyMode = _prevGcMode; }
                catch { /* best-effort restore */ }
                _didPatch = false; // fail safe, keep game running
            }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            ClearRunGates();
            PeriodicSlicer.ResetHourlyGates();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            BoostDrain(3.0);
            ClearRunGates();
        }

        public override void OnGameLoaded(Game game, object initializerObject)
        {
            base.OnGameLoaded(game, initializerObject);
            BoostDrain(3.0);
            ClearRunGates();
            PeriodicSlicer.ResetHourlyGates();
        }

        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            BoostDrain(3.0);
            ClearRunGates();
            PeriodicSlicer.ResetHourlyGates();
        }

        protected override void OnSubModuleUnloaded()
        {
            try
            {
                ClearRunGates();
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

        private static void HandleFrameModeTransition(FrameMode nextMode)
        {
            var prevMode = _lastFrameMode;
            _lastFrameMode = nextMode;
            Volatile.Write(ref _recentOverBudgetUntilSec, 0.0);
            Interlocked.Exchange(ref _overBudgetStreak, 0);
            Stats.Clear();
            RootAgg.Clear();
            AllocBuckets.Clear();
            PeriodicSlicer.ResetHourlyGates();
            ResetChildBackpressureCounters();
            _rootBucket = null;
            _rootPeriodic = null;
            _rootBurstTotal = 0.0;
            _rootDepth = 0;
            _traceMem = false;
            _mapHotGate = false;
            _uiContextDepth = 0;
            if ((prevMode == FrameMode.Fast && nextMode != FrameMode.Fast) ||
                nextMode == FrameMode.Paused || prevMode == FrameMode.Paused)
            {
                BoostDrain();
            }
        }

        private static void TryHandleDebugToggle()
        {
            try
            {
                var ctrlDown = Input.IsKeyDown(InputKey.LeftControl) || Input.IsKeyDown(InputKey.RightControl);
                var shiftDown = Input.IsKeyDown(InputKey.LeftShift) || Input.IsKeyDown(InputKey.RightShift);
                if (!(ctrlDown && shiftDown))
                {
                    _debugToggleComboActive = false;
                    return;
                }

                if (!_debugToggleComboActive && Input.IsKeyPressed(InputKey.F10))
                {
                    var enabled = !MapPerfLog.DebugEnabled;
                    MapPerfLog.DebugEnabled = enabled;
                    MapPerfLog.Info($"[debug] MapPerfProbe debug logging {(enabled ? "enabled" : "disabled")} (Ctrl+Shift+F10)");
                    _debugToggleComboActive = true;
                    _debugOverride = true;
                    return;
                }

                if (!Input.IsKeyDown(InputKey.F10))
                    _debugToggleComboActive = false;
            }
            catch
            {
                // input layer not ready; ignore
            }
        }

        private static void DisableRuntime()
        {
            // Reset GC mode and all per-frame gates
            try { GCSettings.LatencyMode = _prevGcMode; }
            catch { /* best-effort */ }
            _desyncDebtMs = 0.0;
            WriteLastFrameMsSnap(0.0);
            Interlocked.Exchange(ref _frameSeqStamped, 0);
            Interlocked.Exchange(ref _frameSeq, 0);
            Interlocked.Exchange(ref _debtSeqApplied, 0);
            Interlocked.Exchange(ref _forcedCatchupSeq, 0);
            _forcedCatchupDebtSnap = 0.0;
            _nextDebtLog = 0.0;
            _forcedCatchupCooldown = 0.0;
            _mapScreenSkipFrames = 0;
            _mapScreenThrottleActive = false;
            _skipMapOnFrameTick = false;
            _mapScreenFastTime = false;
            _mapScreenFastTimeValid = false;
            _mapHotGate = false;
            _snapPaused = false;
            _snapFast = false;
            Interlocked.Exchange(ref _simSkipToken, 0);
        }

        private static void EnableRuntime()
        {
            // Restore preferred GC mode when active
            try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; }
            catch { /* best-effort */ }
            _lastSimEveryN = MapPerfConfig.SimTickEveryNSkipped;
            Interlocked.Exchange(ref _simSkipToken, 0);
            _desyncDebtMs = 0.0;
            WriteLastFrameMsSnap(0.0);
            Interlocked.Exchange(ref _frameSeq, 0);
            Interlocked.Exchange(ref _frameSeqStamped, 0);
            Interlocked.Exchange(ref _debtSeqApplied, 0);
            Interlocked.Exchange(ref _forcedCatchupSeq, 0);
            _forcedCatchupDebtSnap = 0.0;
            _nextDebtLog = 0.0;
            _forcedCatchupCooldown = 0.0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StampFrameSequence()
        {
            if (Interlocked.CompareExchange(ref _frameSeqStamped, 1, 0) == 0)
            {
                var seq = Interlocked.Increment(ref _frameSeq);
                if (seq <= 0 || seq > 1_000_000_000)
                {
                    Interlocked.Exchange(ref _frameSeq, 1);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static double ReadLastFrameMsSnap() => Interlocked.CompareExchange(ref _lastFrameMsSnap, 0.0, 0.0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteLastFrameMsSnap(double value) => Interlocked.Exchange(ref _lastFrameMsSnap, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComputeEffectiveSkipCadence(double debt, int maxDebtMs)
        {
            var n = MapPerfConfig.SimTickEveryNSkipped;
            if (n < 0) n = 0;
            var wm = Math.Max(0, Math.Min(MapPerfConfig.DesyncLowWatermarkMs, maxDebtMs));
            if (debt >= wm && n > 1)
                return 2;
            return n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldSkipSimulationNow()
        {
            if (!MapPerfConfig.Enabled)
                return false;
            if (!MapPerfConfig.EnableMapThrottle || !MapPerfConfig.DesyncSimWhileThrottling)
                return false;
            if (!FastSnapshot || PausedSnapshot)
                return false;
            if (!(_mapScreenThrottleActive || _mapScreenSkipFrames > 0
                  || Volatile.Read(ref _overBudgetStreak) >= HotEnableStreak))
                return false;

            var maxDebtMs = MapPerfConfig.MaxDesyncMs;
            if (maxDebtMs <= 0)
                return false;

            var debt = _desyncDebtMs;
            var frameSeq = Volatile.Read(ref _frameSeq);
            if (debt >= maxDebtMs)
            {
                Volatile.Write(ref _forcedCatchupSeq, frameSeq);
                _forcedCatchupDebtSnap = debt;
                return false;
            }

            var n = ComputeEffectiveSkipCadence(debt, maxDebtMs);
            if (n == 0)
                return true;

            var token = Interlocked.Increment(ref _simSkipToken);
            if (token <= 0 || token > 1_000_000_000)
            {
                Interlocked.Exchange(ref _simSkipToken, 0);
                token = 0;
            }
            return (token % n) != 0;
        }

        protected override void OnApplicationTick(float dt)
        {
            // start-of-frame sequence stamp
            StampFrameSequence();

            try
            {
                if (_forcedCatchupCooldown > 0.0)
                    _forcedCatchupCooldown = Math.Max(0.0, _forcedCatchupCooldown - dt);

                var enabledNow = MapPerfConfig.Enabled;
                if (enabledNow != _lastEnabled)
                {
                    if (enabledNow)
                        EnableRuntime();
                    else
                        DisableRuntime();
                    _lastEnabled = enabledNow;
                }

                if (!enabledNow)
                    return;

                var paused = IsPaused();
                var fast = IsFastTime();
                MsgFilter.RefreshFamilyMaskFromConfig();
                TryHandleDebugToggle();
                if (!_debugOverride)
                {
                    MapPerfLog.DebugEnabled = MapPerfConfig.DebugLogging;
                }
                else if (MapPerfLog.DebugEnabled == MapPerfConfig.DebugLogging)
                {
                    _debugOverride = false;
                }
                if (MapPerfLog.DebugEnabled)
                {
                    _nextFilterLog -= dt;
                    if (_nextFilterLog <= 0.0)
                    {
                        _nextFilterLog = 10.0;
                        var blocked = Volatile.Read(ref MsgFilter.FilterCount);
                        MapPerfLog.Info($"[filter] blocked {blocked} messages total");
                    }
                }
                else
                {
                    _nextFilterLog = 10.0;
                }

                if (MapPerfLog.DebugEnabled && MapPerfConfig.EnableMapThrottle && MapPerfConfig.DesyncSimWhileThrottling)
                {
                    _nextDebtLog -= dt;
                    if (_nextDebtLog <= 0.0)
                    {
                        _nextDebtLog = 5.0;
                        var maxDebt = MapPerfConfig.MaxDesyncMs;
                        if (maxDebt > 0)
                        {
                            var debt = Math.Max(0.0, _desyncDebtMs);
                            var cadence = ComputeEffectiveSkipCadence(debt, maxDebt);
                            string cadenceLabel;
                            if (cadence <= 0)
                                cadenceLabel = "skip-only";
                            else if (cadence == 1)
                                cadenceLabel = "every frame";
                            else
                                cadenceLabel = $"1 per {cadence}";
                            var forcedActive = _forcedCatchupCooldown > 0.0;
                            var forcedSuffix = forcedActive ? ", forced catch-up active" : string.Empty;
                            MapPerfLog.Info($"[desync] debt {debt:F1} ms (cadence {cadenceLabel}, max {maxDebt} ms{forcedSuffix})");
                        }
                        else
                        {
                            MapPerfLog.Info("[desync] debt tracking disabled (max=0)");
                        }
                    }
                }
                else
                {
                    _nextDebtLog = 5.0;
                }
                var nextMode = paused ? FrameMode.Paused : (fast ? FrameMode.Fast : FrameMode.Run);
                if (nextMode != _lastFrameMode)
                    HandleFrameModeTransition(nextMode);
                _snapPaused = paused;
                _snapFast = fast;

                if (!_mapScreenThrottleActive && _mapScreenSkipFrames == 0)
                {
                    Interlocked.Exchange(ref _simSkipToken, 0);
                    _desyncDebtMs = 0.0;
                }

                var simEveryN = MapPerfConfig.SimTickEveryNSkipped;
                if (simEveryN != _lastSimEveryN)
                {
                    _lastSimEveryN = simEveryN;
                    Interlocked.Exchange(ref _simSkipToken, 0);
                }

                if (!MapPerfConfig.EnableMapThrottle || !MapPerfConfig.DesyncSimWhileThrottling)
                    _desyncDebtMs = 0.0;

                var onMap = IsOnMap();
                if (onMap != _wasOnMap)
                {
                    _wasOnMap = onMap;
                    Interlocked.Exchange(ref MsgFilter.FilterCount, 0);
                    MsgFilter.ClearDedup();
                }

                if (fast && onMap)
                {
                    var lastMs = ReadLastFrameMsSnap();
                    if (lastMs >= MapPerfConfig.SpikeRunMs)
                    {
                        _mapScreenSkipFrames = 0;
                        _mapScreenThrottleActive = false;
                        Interlocked.Exchange(ref _simSkipToken, 0);
                        _forcedCatchupCooldown = Math.Max(_forcedCatchupCooldown, 2.00);
                        _desyncDebtMs = Math.Min(_desyncDebtMs, MapPerfConfig.DesyncLowWatermarkMs * 0.5);
                        BoostDrain(0.75);
                        var cooldownUntil = NowSec() + PostSpikeNoPumpSec;
                        if (cooldownUntil > _noPumpUntilSec)
                            _noPumpUntilSec = cooldownUntil;
                    }
                }
                var frameTag = paused ? "[PAUSED]" : (fast ? "[RUN-FAST]" : "[RUN]");
                if (!onMap)
                    PeriodicSlicer.ClearCachesIfIdle();
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
                WriteLastFrameMsSnap(frameMs);

                var overBudget = false;
                // Treat recent big frames as over-budget to clamp pump
                if (ReadLastFrameMsSnap() >= 45.0) overBudget = true;
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
                    MapPerfLog.Warn($"FRAME spike {frameMs:F1} ms {frameTag}");
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
                {
                    // Avoid huge string-building + aggregation on the UI thread while paused.
                    // Instead, schedule a near-term flush.
                    if (paused)
                        _nextFlush = Math.Min(_nextFlush, 1.0);
                    else
                        FlushSummary(true);
                }

                _nextFlush -= dt;
                if (_frameSpikeCD > 0.0)
                    _frameSpikeCD = Math.Max(0.0, _frameSpikeCD - dt);
                if (_memSpikeCD > 0.0)
                    _memSpikeCD = Math.Max(0.0, _memSpikeCD - dt);
                if (_nextFlush <= 0.0)
                {
                    // slower cadence while running; avoid summary flush during live play but fail-safe every ~30s
                    _nextFlush = paused ? 5.0 : 30.0;
                    if (onMap && (paused || allocDelta > 0 || wsDelta > 0))
                        FlushSummary(force: false);
                }

                if (overBudget)
                {
                    var dtMs = frameMs;
                    var hysteresisSec = Math.Min(1.0, 0.3 + 2.0 * (dtMs / 1000.0));
                    Volatile.Write(ref _recentOverBudgetUntilSec, NowSec() + hysteresisSec);
                }

                // Drain queue
                PeriodicSlicer.GetQueueStats(out var queueLength, out _, out _);

                double pumpMs = 0.0;
                var boostUntil = Volatile.Read(ref _drainBoostUntilSec);

                // end boost early if nothing left
                if (queueLength == 0 && boostUntil > 0.0)
                {
                    Volatile.Write(ref _drainBoostUntilSec, 0.0);
                    boostUntil = 0.0;
                }

                if (overBudget && boostUntil > 0.0)
                {
                    Volatile.Write(ref _drainBoostUntilSec, 0.0);
                    boostUntil = 0.0;
                }

                if (!paused)
                {
                    var baseBudgetMs = fast ? PumpBudgetFastMs : PumpBudgetRunMs;

                    // short burst after transitions
                    if (boostUntil > 0.0 && NowSec() < boostUntil)
                        baseBudgetMs = Math.Max(baseBudgetMs, 40.0);

                    pumpMs = baseBudgetMs;

                    // soft boost below hard threshold – clears backlog while staying subtle
                    if (queueLength > 0 && queueLength < PumpBacklogBoostThreshold)
                        pumpMs += Math.Min(8.0, queueLength * 0.002);

                    // hard boost when backlog is large
                    if (queueLength >= PumpBacklogBoostThreshold)
                        pumpMs += fast ? PumpBudgetFastBoostMs : PumpBudgetRunBoostMs;

                    // if frame was over budget, be conservative
                    if (overBudget)
                        pumpMs = Math.Max(0.5, pumpMs * 0.25);
                }
                else
                {
                    // tiny trickle during pause/menus → no minute-long trickle afterwards
                    if (queueLength >= 1 || (boostUntil > 0.0 && NowSec() < boostUntil))
                    {
                        var trickle = onMap ? Math.Min(PumpPauseTrickleMapMs, PumpBudgetRunMs) : PumpPauseTrickleMenuMs;
                        pumpMs = Math.Max(0.0, trickle);
                    }
                }

                if (!overBudget && queueLength > 0 && queueLength <= 50)
                    pumpMs = Math.Max(pumpMs, fast ? PumpTailMinFastMs : PumpTailMinRunMs);

                pumpMs = Math.Min(pumpMs, fast ? PumpBudgetFastCapMs : PumpBudgetRunCapMs);

                var noPumpUntil = Volatile.Read(ref _noPumpUntilSec);
                if (paused || (noPumpUntil > 0.0 && NowSec() < noPumpUntil))
                {
                    PeriodicSlicer.GetQueueStats(out var qlenNow, out _, out _);
                    if (!paused && qlenNow >= PumpBacklogBoostThreshold)
                    {
                        var trickleMs = Math.Min(4.0, pumpMs > 0.0 ? pumpMs : (fast ? PumpTailMinFastMs : PumpTailMinRunMs));
                        if (trickleMs > 0.0)
                            PeriodicSlicer.Pump(trickleMs);
                    }
                    else if (!paused && ShouldLogSlow("pump-suppress", PostSpikeNoPumpSec))
                    {
                        MapPerfLog.Info("[slice] pump suppressed (cooldown)");
                    }
                }
                else if (pumpMs > 0.0)
                {
                    PeriodicSlicer.Pump(pumpMs);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _frameSeqStamped, 0);
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
                if (ps.Length > 4) continue;
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
            if (prefix == null || prefix2 == null || methodNames == null || methodNames.Length < 2) return;
            var t = FindType(fullName);
            if (t == null) return;

            var ht = harmony.GetType();
            var asm = ht.Assembly;
            var hmType = asm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {asm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (hmCtor == null || patchMi == null) return;

            var pre1 = hmCtor.Invoke(new object[] { prefix });
            var pre2 = hmCtor.Invoke(new object[] { prefix2 });

            var prefixMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            void MapPrefix(string key, object hm)
            {
                if (hm == null || string.IsNullOrEmpty(key)) return;
                prefixMap[key] = hm;
                var alias = GetMethodAlias(key);
                if (!string.IsNullOrEmpty(alias)) prefixMap[alias] = hm;
            }

            MapPrefix(methodNames[0], pre1);
            MapPrefix(methodNames[1], pre2);
            for (int i = 2; i < methodNames.Length; i++)
            {
                var key = methodNames[i];
                if (MatchesMethodAlias(key, methodNames[0]))
                    MapPrefix(key, pre1);
                else if (MatchesMethodAlias(key, methodNames[1]))
                    MapPrefix(key, pre2);
            }

            void Bump(object hm)
            {
                try
                {
                    var ty = hm?.GetType();
                    var prProp = ty?.GetProperty("priority");
                    var prField = ty?.GetField("priority");
                    var priorityType = prProp?.PropertyType ?? prField?.FieldType;
                    object highest = 400;
                    if (priorityType != null)
                    {
                        if (priorityType.IsEnum)
                            highest = Enum.ToObject(priorityType, 400);
                        else if (priorityType == typeof(int))
                            highest = 400;
                    }
                    try { prProp?.SetValue(hm, highest); } catch { }
                    try { prField?.SetValue(hm, highest); } catch { }
                }
                catch
                {
                    // best-effort priority bump
                }
            }

            Bump(pre1);
            Bump(pre2);

            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.IsSpecialName || m.ReturnType != typeof(void) || m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true)
                    continue;

                var dn = m.DeclaringType?.Name;
                if (dn != null && dn.IndexOf("d__", StringComparison.Ordinal) >= 0)
                    continue;

                var name = m.Name;
                if (!methodNames.Any(n => MatchesMethodAlias(name, n))) continue;

                var ps = m.GetParameters();
                if (ps.Length > 4) continue;
                if (ps.Any(p => p.IsOut || p.ParameterType.IsByRef)) continue;

                object hm = null;
                if (!prefixMap.TryGetValue(name, out hm))
                {
                    if (MatchesMethodAlias(name, methodNames[0]))
                        hm = pre1;
                    else if (methodNames.Length > 1 && MatchesMethodAlias(name, methodNames[1]))
                        hm = pre2;
                }

                if (hm == null) continue;

                try
                {
                    patchMi.Invoke(harmony, new object[] { m, hm, null, null, null });
                }
                catch (Exception ex)
                {
                    MapPerfLog.Error($"Slice patch fail {t.FullName}.{m.Name}", ex);
                }
            }
        }

        private static void TryPatchType(object harmony, string fullName, string[] methodNames, MethodInfo prefix)
        {
            if (prefix == null) return;
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
                if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true) continue;
                var declName = m.DeclaringType?.Name;
                if (declName != null && declName.IndexOf("d__", StringComparison.Ordinal) >= 0) continue;
                var ps = m.GetParameters();
                if (ps.Length > 4) continue;
                bool byref = false;
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (p.IsOut || p.ParameterType.IsByRef) { byref = true; break; }
                }
                if (byref) continue;

                var preHM = hmCtor.Invoke(new object[] { prefix });
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

        private static bool DeferCampaignDailyTick_Prefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            Interlocked.Increment(ref _dailyPrefixHits);
            if (!MapPerfConfig.Enabled) return true;
            if (!ShouldDeferPeriodic(__originalMethod)) return true;
            if (PeriodicSlicer.ShouldBypass(__originalMethod)) return true;

            // if gate is closed, free a bit then retry
            if (!MayEnqueueNow())
            {
                PeriodicSlicer.Pump(SubModule.FastSnapshot ? 3.0 : 2.0);
                if (!MayEnqueueNow()) return true; // still closed → run inline (last resort)
            }

            var target = __instance;
            var mi = __originalMethod as MethodInfo;
            if (!PeriodicSlicer.EnqueueSlowAction( // heavy → slow lane
                () =>
                {
                    PeriodicSlicer.EnterBypass(__originalMethod);
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var args = (__args != null && __args.Length > 0) ? __args : null;
                        mi?.Invoke(target, args);
                    }
                    catch (Exception ex)
                    {
                        const string excKey = "exc:Campaign.DailyTick";
                        if (ShouldLogSlow(excKey, 30.0))
                            MapPerfLog.Warn($"Deferred Campaign.DailyTick failed: {ex}");
                    }
                    finally
                    {
                        sw.Stop();
                        PeriodicSlicer.ExitBypass(__originalMethod);
                        if (sw.Elapsed.TotalMilliseconds > 12.0 &&
                            ShouldLogSlow("Campaign.DailyTick"))
                            MapPerfLog.Warn($"[slow-job] Campaign.DailyTick took {sw.Elapsed.TotalMilliseconds:F1} ms");
                    }
                }))
                return true;
            return false;
        }

        // Handle both instance hubs (dispatcher) and static hubs (CampaignEvents) with reentry guard
        public static bool OnDailyTick_Prefix(object __instance, MethodBase __originalMethod)
        {
            if (__originalMethod == null) return true;
            if (!MapPerfConfig.Enabled) return true;
            if (!ShouldDeferPeriodic(__originalMethod)) return true;
            if (PeriodicSlicer.ShouldBypass(__originalMethod)) return true;
            var name = __originalMethod?.Name ?? string.Empty;
            // Split the dispatcher hub: handle exact names on .NET 4.7.2
            if (name == "DailyTick" || name == "OnDailyTick")
            {
                if (SplitAndEnqueueDispatcherChildren(__instance, "DailyTick", __originalMethod)) return false;
                if (SplitAndEnqueueDispatcherChildren(__instance, "OnDailyTick", __originalMethod)) return false;
            }

            // Fallback to generic redirect.
            var handled = PeriodicSlicer.RedirectAny(__instance, __originalMethod, name);
            return !handled;
        }

        public static bool OnHourlyTick_Prefix(object __instance, MethodBase __originalMethod)
        {
            if (__originalMethod == null) return true;
            if (!MapPerfConfig.Enabled) return true;
            if (!ShouldDeferPeriodic(__originalMethod)) return true;
            if (PeriodicSlicer.ShouldBypass(__originalMethod)) return true;

            long hourNow;
            try
            {
                hourNow = (long)Math.Floor(CampaignTime.Now.ToHours);
            }
            catch
            {
                hourNow = long.MinValue;
            }

            if (hourNow == long.MinValue)
            {
                var handledFallback = PeriodicSlicer.RedirectAny(__instance, __originalMethod, "OnHourlyTick");
                return !handledFallback;
            }

            // Split like Daily
            if (SplitAndEnqueueDispatcherChildren(__instance, "HourlyTick", __originalMethod)) return false;
            if (SplitAndEnqueueDispatcherChildren(__instance, "OnHourlyTick", __originalMethod)) return false;

            var handled = PeriodicSlicer.RedirectHourlyWithGate(__instance, __originalMethod, hourNow, out _);
            return !handled;
        }

        public static bool OnWeeklyTick_Prefix(object __instance, MethodBase __originalMethod)
        {
            if (__originalMethod == null) return true;
            if (!MapPerfConfig.Enabled) return true;
            if (!ShouldDeferPeriodic(__originalMethod)) return true;
            if (PeriodicSlicer.ShouldBypass(__originalMethod)) return true;
            if (SplitAndEnqueueDispatcherChildren(__instance, "WeeklyTick", __originalMethod, isWeekly: true))
                return false;

            var handled = PeriodicSlicer.RedirectAny(__instance, __originalMethod, "WeeklyTick");
            return !handled;
        }

        // Reflect the behavior list and enqueue each child method as its own job.
        private static bool SplitAndEnqueueDispatcherChildren(object dispatcher, string childMethod, MethodBase hub, bool isWeekly = false)
        {
            try
            {
                if (!ShouldDeferPeriodic(hub)) return false;
                PeriodicSlicer.GetQueueStats(out var queueLength, out _, out _);
                if (queueLength >= HardNoEnqueueQLen) return false;
                var behaviors = CollectUniqueBehaviors(dispatcher);
                var count = behaviors.Count;
                if (count == 0) return false;

                var any = false;

                // Backpressure-aware per-hub enqueue cap
                var nearFull = PeriodicSlicer.IsQueueNearFull();
                var cap = nearFull ? 1 : (FastSnapshot ? 2 : 3);
                if (cap < 1) cap = 1;
                int enq = 0;

                var isDaily = childMethod == "DailyTick" || childMethod == "OnDailyTick";
                var gate = isWeekly ? _lastWeeklyIdx : (isDaily ? _lastDailyIdx : _lastHourlyIdx);

                long idxNow = long.MinValue;
                CampaignTime now = default;
                bool haveTime = false;
                if (gate != null)
                {
                    try
                    {
                        now = CampaignTime.Now;
                        haveTime = true;
                    }
                    catch
                    {
                        haveTime = false;
                    }

                    if (haveTime)
                    {
                        if (isWeekly)
                            idxNow = (long)Math.Floor(now.ToDays / 7.0);
                        else if (isDaily)
                            idxNow = (long)Math.Floor(now.ToDays);
                        else
                            idxNow = (long)Math.Floor(now.ToHours);
                    }
                }

                var cursorKey = hub != null ? (hub, childMethod ?? string.Empty) : ((MethodBase, string)?)null;
                int start = 0;
                if (cursorKey.HasValue)
                {
                    var existing = _dispatcherChildCursor.GetOrAdd(cursorKey.Value, 0);
                    if (existing < 0) existing = 0;
                    start = count > 0 ? existing % count : 0;
                }

                for (var offset = 0; offset < count; offset++)
                {
                    if (enq >= cap) break;

                    var idx = (start + offset) % count;
                    var beh = behaviors[idx];
                    var behType = beh?.GetType();
                    if (behType == null) continue;

                    var mi = ResolveTickMi(behType, childMethod);
                    if (mi == null || mi.GetParameters().Length != 0) continue;

                    MethodInfo primaryTick = null;
                    if (childMethod == "OnDailyTick" || childMethod == "OnHourlyTick")
                    {
                        var primaryName = childMethod == "OnDailyTick" ? "DailyTick" : "HourlyTick";
                        primaryTick = ResolveTickMi(behType, primaryName);
                    }

                    // If On* exists but a plain *Tick also exists, skip On* to avoid double work.
                    if (primaryTick != null && primaryTick.GetParameters().Length == 0) continue;

                    var idxGate = idxNow;
                    if (idxGate != long.MinValue && gate != null && gate.TryGetValue(mi, out var prev) && prev == idxGate)
                        continue;

                    var behName = behType.Name;
                    var behKey = behType.FullName ?? behName;

                    // Use the child MethodInfo as the queue key to avoid coalescing on the hub.
                    var idxCaptured = idxGate;
                    var ok = PeriodicSlicer.EnqueueActionWithBypass(mi, () =>
                    {
                        if (idxCaptured != long.MinValue)
                        {
                            long idxCur;
                            try
                            {
                                if (isWeekly)
                                    idxCur = (long)Math.Floor(CampaignTime.Now.ToDays / 7.0);
                                else if (isDaily)
                                    idxCur = (long)Math.Floor(CampaignTime.Now.ToDays);
                                else
                                    idxCur = (long)Math.Floor(CampaignTime.Now.ToHours);
                            }
                            catch
                            {
                                idxCur = idxCaptured;
                            }

                            if (idxCur != idxCaptured)
                                return;
                        }

                        var sw = Stopwatch.StartNew();
                        try { mi.Invoke(beh, null); }
                        catch (Exception ex)
                        {
                            var excKey = $"exc:{childMethod}:{behKey}";
                            if (ShouldLogSlow(excKey, 30.0))
                                MapPerfLog.Warn($"Deferred {childMethod} {behName} failed: {ex}");
                        }
                        finally
                        {
                            sw.Stop();
                            if (sw.Elapsed.TotalMilliseconds > 12.0)
                            {
                                var slowKey = $"{childMethod}:{behKey}";
                                if (ShouldLogSlow(slowKey))
                                    MapPerfLog.Warn($"[slow-job] {childMethod} {behName} took {sw.Elapsed.TotalMilliseconds:F1} ms");
                            }
                        }
                    });
                    if (ok)
                    {
                        any = true;
                        if (idxGate != long.MinValue && gate != null)
                            gate[mi] = idxGate;

                        enq++;
                        if (enq >= cap) break; // defer remaining children to next hub call/frame
                    }
                }

                if (cursorKey.HasValue && count > 0)
                {
                    var advance = Math.Max(enq, 1);
                    var next = (start + advance) % count;
                    _dispatcherChildCursor.AddOrUpdate(cursorKey.Value, next, (_, __) => next);
                }

                // If we scheduled any children, skip the hub invoke.
                if (any) return true;
            }
            catch (Exception ex)
            {
                MapPerfLog.Warn($"SplitAndEnqueue {childMethod} failed: {ex.Message}");
            }

            return false;
        }

        // Try several routes to enumerate campaign behaviors.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IEnumerable<object> EnumCampaignBehaviors(object dispatcher)
        {
            if (TryGetCampaignBehaviorList(out var coreList))
            {
                foreach (var beh in coreList)
                    yield return beh;
            }

            foreach (var beh in EnumerateDispatcherFieldBehaviors(dispatcher))
                yield return beh;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetCampaignBehaviorList(out IEnumerable coreList)
        {
            coreList = null;
            try
            {
                var campT = _campaignType ?? (_campaignType = AccessTools.TypeByName("TaleWorlds.CampaignSystem.Campaign"));
                var currentProp = _campaignCurrentProp ??
                                  (_campaignCurrentProp = campT?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public));
                var campaign = currentProp?.GetValue(null);
                if (campaign == null) return false;

                var campaignType = campaign.GetType();
                var managerProp = _campaignBehaviorManagerProp;
                if (managerProp == null || managerProp.ReflectedType != campaignType)
                {
                    managerProp = campaignType.GetProperty("CampaignBehaviorManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (managerProp == null)
                    {
                        _campaignBehaviorManagerProp = null;
                        return false;
                    }
                    _campaignBehaviorManagerProp = managerProp;
                }

                var manager = managerProp.GetValue(campaign);
                if (manager == null) return false;

                var managerType = manager.GetType();
                if (!ReferenceEquals(managerType, _campaignBehaviorManagerType))
                {
                    _campaignBehaviorManagerType = managerType;
                    _campaignBehaviorsProp = null;
                }

                var behaviorsProp = _campaignBehaviorsProp;
                if (behaviorsProp == null || behaviorsProp.ReflectedType != managerType)
                {
                    behaviorsProp = managerType.GetProperty("CampaignBehaviors", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _campaignBehaviorsProp = behaviorsProp;
                }
                coreList = behaviorsProp?.GetValue(manager) as IEnumerable;
                return coreList != null;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<object> EnumerateDispatcherFieldBehaviors(object dispatcher)
        {
            var dt = dispatcher?.GetType();
            if (dt == null) yield break;

            foreach (var field in dt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var ft = field.FieldType;
                if (ft == null) continue;
                if (!typeof(IEnumerable).IsAssignableFrom(ft)) continue;
                if (IsDictionaryLike(ft)) continue;
                var fieldValue = field.GetValue(dispatcher);
                if (fieldValue == null) continue;
                var en = fieldValue as IEnumerable;
                if (en == null) continue;
                foreach (var beh in en)
                    yield return beh;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsDictionaryLike(Type type)
        {
            if (type == null) return false;
            if (typeof(IDictionary).IsAssignableFrom(type)) return true;
            foreach (var iface in type.GetInterfaces())
            {
                if (iface == typeof(IDictionary)) return true;
                if (!iface.IsGenericType) continue;
                var genericDef = iface.GetGenericTypeDefinition();
                if (genericDef == typeof(IDictionary<,>)) return true;
                if (genericDef == typeof(IReadOnlyDictionary<,>)) return true;
                if (genericDef == typeof(IEnumerable<>))
                {
                    var arg = iface.GetGenericArguments();
                    if (arg.Length == 1 && IsKeyValuePair(arg[0])) return true;
                }
            }

            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                if (def == typeof(System.Collections.ObjectModel.ReadOnlyDictionary<,>)) return true;
                if (def == typeof(ConcurrentDictionary<,>)) return true;
                if (def == typeof(IEnumerable<>))
                {
                    var args = type.GetGenericArguments();
                    if (args.Length == 1 && IsKeyValuePair(args[0])) return true;
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsKeyValuePair(Type type)
        {
            if (type == null || !type.IsGenericType) return false;
            return type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
        }

        private static readonly ConcurrentDictionary<(MethodBase Hub, string Child), int> _dispatcherChildCursor =
            new ConcurrentDictionary<(MethodBase, string), int>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static List<object> CollectUniqueBehaviors(object dispatcher)
        {
            var list = new List<object>();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var beh in EnumCampaignBehaviors(dispatcher))
            {
                if (beh == null) continue;
                if (seen.Add(beh)) list.Add(beh);
            }
            return list;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            private ReferenceEqualityComparer()
            {
            }

            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static readonly ConcurrentDictionary<(Type Type, string Name), MethodInfo> _tickMiCache =
            new ConcurrentDictionary<(Type, string), MethodInfo>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static MethodInfo ResolveTickMi(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            return _tickMiCache.GetOrAdd((type, name),
                key => key.Type.GetMethod(key.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        }

        // Unified prefix for any periodic hub (instance or static).
        public static bool PeriodicHub_Prefix(object __instance, MethodBase __originalMethod)
        {
            var name = __originalMethod?.Name;
            if (string.IsNullOrEmpty(name)) return true;
            if (!IsPeriodic(__originalMethod)) return true;
            // 🚫 never enqueue unless we’re truly in/near lag
            if (!ShouldDeferPeriodic(__originalMethod))
                return true; // run inline (no redirect/enqueue)

            if (string.Equals(name, "HourlyTick", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "OnHourlyTick", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "WeeklyTick", StringComparison.OrdinalIgnoreCase))
                return true;
            if (PeriodicSlicer.ShouldBypass(__originalMethod)) return true;
            var handled = PeriodicSlicer.RedirectAny(__instance, __originalMethod, name);
            return !handled;
        }

        public static bool DeferPeriodicChild_Prefix(object __instance, MethodBase __originalMethod)
        {
            if (__originalMethod == null) return true;
            if (PeriodicSlicer.ShouldBypass(__originalMethod)) return true;
            // 🚫 only defer when a frame actually went over budget (or just after)
            if (!ShouldDeferPeriodic(__originalMethod))
                return true; // run inline

            if (PausedSnapshot)
            {
                Interlocked.Exchange(ref _childBacklogToken, 0);
                return true;
            }

            var queueNearFull = PeriodicSlicer.IsQueueNearFull(); // fast-time tightens headroom automatically
            if (queueNearFull)
            {
                if (FastSnapshot)
                    return true;

                var inlineEvery = ChildInlineAllowEveryN;
                PeriodicSlicer.GetQueueStats(out var len, out var threshold, out var capacity);
                var scaleBase = threshold > 0 ? threshold : capacity;
                if (scaleBase > 0)
                {
                    var pct = (int)((long)len * 100 / scaleBase);
                    if (pct >= ChildInlineAllowanceQuarterThresholdPct)
                    {
                        inlineEvery = Math.Max(ChildInlineAllowEveryN / 4, ChildInlineAllowanceMin);
                    }
                    else if (pct >= ChildInlineAllowanceHalfThresholdPct)
                    {
                        inlineEvery = Math.Max(ChildInlineAllowEveryN / 2, ChildInlineAllowanceMin);
                    }
                }

                var ticket = Interlocked.Increment(ref _childBacklogToken);
                if (ticket >= inlineEvery)
                {
                    var remaining = Interlocked.Add(ref _childBacklogToken, -inlineEvery);
                    if (Math.Abs(remaining) > ChildInlineTokenClamp)
                        Interlocked.Exchange(ref _childBacklogToken, 0);
                    return true;
                }

                IncrementChildBackpressureSkips();
                RecordChildBackpressureSkip(__originalMethod);
                return false;
            }

            Interlocked.Exchange(ref _childBacklogToken, 0);

            if (!(__originalMethod is MethodInfo method)
                || method.IsAbstract
                || method.ContainsGenericParameters
                || method.GetParameters().Length != 0)
            {
                return true;
            }

            var name = method.Name;
            if (!string.IsNullOrEmpty(name) && PeriodicSlicer.RedirectAny(__instance, method, name))
                return false;

            var target = __instance;
            bool enqueued = PeriodicSlicer.EnqueueActionWithBypass(__originalMethod, () =>
            {
                try { method.Invoke(target, null); }
                catch (Exception ex)
                {
                    if (ex is TargetInvocationException tie && tie.InnerException != null)
                        ex = tie.InnerException;
                    var owner = method.DeclaringType?.FullName ?? "<unknown>";
                    MapPerfLog.Error($"deferred {owner}.{method.Name} failed", ex);
                }
            });

            if (!enqueued)
            {
                if (!FastSnapshot)
                {
                    IncrementChildBackpressureSkips();
                    RecordChildBackpressureSkip(method);
                    return false;
                }
                return true;
            }

            Interlocked.Exchange(ref _childBacklogToken, 0);

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void IncrementChildBackpressureSkips()
        {
            while (true)
            {
                var current = Volatile.Read(ref _childBackpressureSkips);
                if (current >= int.MaxValue - 1)
                {
                    if (current == int.MaxValue - 1)
                        return;
                    if (Interlocked.CompareExchange(ref _childBackpressureSkips, int.MaxValue - 1, current) == current)
                        return;
                    continue;
                }

                if (Interlocked.CompareExchange(ref _childBackpressureSkips, current + 1, current) == current)
                    return;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecordChildBackpressureSkip(MethodBase method)
        {
            if (method == null) return;

            _childBackpressureSkipsByMethod.AddOrUpdate(
                method,
                1,
                (_, current) => current >= int.MaxValue - 1 ? int.MaxValue - 1 : current + 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string GetMethodAlias(string methodName)
        {
            if (methodName == null) return null;
            if (string.Equals(methodName, "OnDailyTick", StringComparison.OrdinalIgnoreCase)) return "DailyTick";
            if (string.Equals(methodName, "DailyTick", StringComparison.OrdinalIgnoreCase)) return "OnDailyTick";
            if (string.Equals(methodName, "OnHourlyTick", StringComparison.OrdinalIgnoreCase)) return "HourlyTick";
            if (string.Equals(methodName, "HourlyTick", StringComparison.OrdinalIgnoreCase)) return "OnHourlyTick";
            if (string.Equals(methodName, "OnPeriodicDailyTick", StringComparison.OrdinalIgnoreCase)) return "PeriodicDailyTick";
            if (string.Equals(methodName, "PeriodicDailyTick", StringComparison.OrdinalIgnoreCase)) return "OnPeriodicDailyTick";
            if (string.Equals(methodName, "OnPeriodicHourlyTick", StringComparison.OrdinalIgnoreCase)) return "PeriodicHourlyTick";
            if (string.Equals(methodName, "PeriodicHourlyTick", StringComparison.OrdinalIgnoreCase)) return "OnPeriodicHourlyTick";
            if (string.Equals(methodName, "OnWeeklyTick", StringComparison.OrdinalIgnoreCase)) return "WeeklyTick";
            if (string.Equals(methodName, "WeeklyTick", StringComparison.OrdinalIgnoreCase)) return "OnWeeklyTick";
            if (string.Equals(methodName, "OnQuarterDailyPartyTick", StringComparison.OrdinalIgnoreCase)) return "QuarterDailyPartyTick";
            if (string.Equals(methodName, "QuarterDailyPartyTick", StringComparison.OrdinalIgnoreCase)) return "OnQuarterDailyPartyTick";
            if (string.Equals(methodName, "OnTickPartialHourlyAi", StringComparison.OrdinalIgnoreCase)) return "TickPartialHourlyAi";
            if (string.Equals(methodName, "TickPartialHourlyAi", StringComparison.OrdinalIgnoreCase)) return "OnTickPartialHourlyAi";
            if (string.Equals(methodName, "OnPeriodicQuarterDailyTick", StringComparison.OrdinalIgnoreCase)) return "PeriodicQuarterDailyTick";
            if (string.Equals(methodName, "PeriodicQuarterDailyTick", StringComparison.OrdinalIgnoreCase)) return "OnPeriodicQuarterDailyTick";
            if (string.Equals(methodName, "OnAiHourlyTick", StringComparison.OrdinalIgnoreCase)) return "AiHourlyTick";
            if (string.Equals(methodName, "AiHourlyTick", StringComparison.OrdinalIgnoreCase)) return "OnAiHourlyTick";
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

                try
                {
                    foreach (var m in type.GetMethods(flags))
                        if (m.Name == name && m.GetParameters().Length == 0)
                            return m;
                }
                catch
                {
                    // ignored: fall back to slower base-type walk
                }

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
            StampFrameSequence();
            // Master switch: never throttle when disabled
            if (!MapPerfConfig.Enabled) return true;

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
                    _mapScreenSkipFrames = 0;
                    return true;
                }

                var fastTime = IsFastTime();
                _mapScreenFastTime = fastTime;
                _mapScreenFastTimeValid = true;
                _mapHotGate = ShouldEnableMapHot(fastTime);

                var throttleEnabled = MapPerfConfig.EnableMapThrottle;
                var throttleOnlyFast = MapPerfConfig.ThrottleOnlyInFastTime;

                if (!throttleEnabled)
                {
                    _skipMapOnFrameTick = false;
                    _mapScreenSkipFrames = 0;
                    return true;
                }

                if (throttleOnlyFast && !fastTime)
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
            var throttleEnabled2 = MapPerfConfig.EnableMapThrottle;
            var throttleOnlyFast2 = MapPerfConfig.ThrottleOnlyInFastTime;
            if (!throttleEnabled2)
            {
                _mapScreenThrottleActive = false;
                _skipMapOnFrameTick = false;
                _mapHotGate = ShouldEnableMapHot(fastTime2);
                return true;
            }

            if (throttleOnlyFast2 && !fastTime2)
            {
                _mapScreenThrottleActive = false;
                _skipMapOnFrameTick = false;
                _mapScreenSkipFrames = 0;
                _mapHotGate = ShouldEnableMapHot(fastTime2);
                return true;
            }

            if (_mapScreenThrottleActive || _mapScreenSkipFrames > 0)
            {
                _mapScreenThrottleActive = true;
                _skipMapOnFrameTick = true;
                _mapHotGate = false;
                return false;
            }

            _mapHotGate = ShouldEnableMapHot(fastTime2);
            return true;
        }

        private static void PatchDesyncMapState(object harmony)
        {
            try
            {
                var prefix = typeof(SubModule).GetMethod(nameof(DesyncPrefix), HookBindingFlags);
                if (prefix == null) return;

                var ht = harmony.GetType();
                var harmonyAsm = ht.Assembly;
                var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                            ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
                var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
                var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
                if (hmType == null || hmCtor == null || patchMi == null) return;

                var prefixHm = hmCtor.Invoke(new object[] { prefix });

                void ElevatePriority(object hm)
                {
                    if (hm == null) return;
                    try
                    {
                        var prioProp = hmType.GetProperty("priority", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        var prioField = hmType.GetField("priority", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        object highest = 400;
                        var prioEnum = harmonyAsm.GetType("HarmonyLib.Priority");
                        if (prioEnum != null && prioEnum.IsEnum)
                        {
                            try { highest = Enum.ToObject(prioEnum, 400); }
                            catch { highest = 400; }
                        }

                        if (prioProp != null)
                        {
                            var pt = prioProp.PropertyType;
                            if (pt.IsEnum)
                                prioProp.SetValue(hm, highest);
                            else if (pt == typeof(int))
                                prioProp.SetValue(hm, 400);
                        }

                        if (prioField != null)
                        {
                            var ft = prioField.FieldType;
                            if (ft.IsEnum)
                                prioField.SetValue(hm, highest);
                            else if (ft == typeof(int))
                                prioField.SetValue(hm, 400);
                        }
                    }
                    catch
                    {
                        // best-effort priority elevation
                    }
                }

                ElevatePriority(prefixHm);

                void PatchTarget(string typeName, string methodName)
                {
                    var t = FindType(typeName);
                    if (t == null) return;

                    MethodInfo target = null;
                    try { target = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch { }
                    if (target == null) return;
                    if (target.ReturnType != typeof(void)) return;
                    if (target.IsAbstract || target.ContainsGenericParameters || target.DeclaringType?.IsGenericTypeDefinition == true)
                        return;

                    try
                    {
                        patchMi.Invoke(harmony, new object[] { target, prefixHm, null, null, null });
                        MapPerfLog.Info($"Patched {t.FullName}.{methodName} (desync)");
                    }
                    catch (Exception ex)
                    {
                        MapPerfLog.Error($"Patch fail {t.FullName}.{methodName} (desync)", ex);
                    }
                }

                PatchTarget("TaleWorlds.CampaignSystem.GameState.MapState", "OnTick");
                PatchTarget("TaleWorlds.CampaignSystem.GameState.MapState", "OnMapModeTick");
                PatchTarget("TaleWorlds.CampaignSystem.MapState", "OnTick");
                PatchTarget("TaleWorlds.CampaignSystem.MapState", "OnMapModeTick");
            }
            catch (Exception ex)
            {
                MapPerfLog.Error("PatchDesyncMapState failed", ex);
            }
        }

        // return false => skip original (no Campaign tick this frame)
        public static bool DesyncPrefix()
        {
            StampFrameSequence();
            var skip = ShouldSkipSimulationNow();
            var dtSnap = ReadLastFrameMsSnap();
            var dt = dtSnap > 0.0 ? dtSnap : 16.0;
            var seq = Volatile.Read(ref _frameSeq);
            var forcedSeq = Volatile.Read(ref _forcedCatchupSeq);
            var forced = forcedSeq == seq;
            // ensure we add/subtract debt only once per frame even if multiple hooks run
            var firstThisFrame = Interlocked.Exchange(ref _debtSeqApplied, seq) != seq;
            if (firstThisFrame && skip)
            {
                var maxDebt = Math.Max(MapPerfConfig.MaxDesyncMs, 0);
                var cap = maxDebt > 0 ? maxDebt * 2.0 : 0.0;
                var nextDebt = _desyncDebtMs + dt;
                _desyncDebtMs = cap > 0.0 ? Math.Min(cap, nextDebt) : nextDebt;
            }
            else if (firstThisFrame && _desyncDebtMs > 0.0)
            {
                _desyncDebtMs = Math.Max(0.0, _desyncDebtMs - dt);
            }

            if (MapPerfLog.DebugEnabled && firstThisFrame && forced && _forcedCatchupCooldown <= 0.0)
            {
                var debtSnap = Math.Max(0.0, _forcedCatchupDebtSnap);
                MapPerfLog.Info($"[desync] forced catch-up ({debtSnap:F1} ms debt)");
                _forcedCatchupCooldown = 2.0;
            }

            return !skip;
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

        public struct State
        {
            public long ts;
            public long mem;
            public byte Flags;
        }

        public static void PerfPrefix(object __instance, MethodBase __originalMethod, out State __state)
        {
            __state = default;

            if (!MapPerfConfig.Enabled)
            {
                __state.Flags = StateFlagSkip;
                return;
            }

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
                        return;
                    }
                }
            }

            _skipMapOnFrameTick = false;

            byte flags = 0;
            if (__originalMethod != null && IsUiContextMethod(__originalMethod))
            {
                flags |= StateFlagUiContext;
                var depth = _uiContextDepth++;
                if (depth > 0)
                {
                    flags |= StateFlagSkip;
                    __state.Flags = flags;
                    return;
                }
            }

            _callDepth++;

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

            var traceThis = _traceMem && IsPeriodic(__originalMethod) && !IsPerFrameHook(__originalMethod);
            __state.mem = traceThis ? GC.GetTotalMemory(false) : 0;
            __state.ts = Stopwatch.GetTimestamp();
            __state.Flags = flags;
        }

        public static void PerfPostfix(object __instance, MethodBase __originalMethod, State __state)
        {
            if ((__state.Flags & StateFlagSkip) != 0)
                return;
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

                        var throttleEnabled3 = MapPerfConfig.EnableMapThrottle;
                        var throttleOnlyFast3 = MapPerfConfig.ThrottleOnlyInFastTime;
                        if (!throttleEnabled3)
                        {
                            _mapScreenSkipFrames = 0;
                        }
                        else
                        {
                            var allowThrottle = !throttleOnlyFast3 || fastTime;
                            if (!allowThrottle)
                            {
                                _mapScreenSkipFrames = 0;
                            }
                            else
                            {
                                var cap = MapScreenSkipFramesCap;
                                if (cap <= 0)
                                {
                                    _mapScreenSkipFrames = 0;
                                }
                                else
                                {
                                    if (dt >= MapScreenBackoffMs2)
                                    {
                                        if (_mapScreenSkipFrames < MapScreenSkipFrames2)
                                            _mapScreenSkipFrames = MapScreenSkipFrames2;
                                    }
                                    else if (dt >= MapScreenBackoffMs1)
                                    {
                                        if (_mapScreenSkipFrames < MapScreenSkipFrames1)
                                            _mapScreenSkipFrames = MapScreenSkipFrames1;
                                    }

                                    if (_mapScreenSkipFrames > cap)
                                        _mapScreenSkipFrames = cap;
                                }
                            }
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
            if (_rootPeriodic != null)
            {
                if (ReferenceEquals(__originalMethod, _rootPeriodic))
                {
                    // window total (inclusive) + burst self
                    RootAgg.AddOrUpdate(_rootPeriodic, dt, (_, v) => v + dt);
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
                if (_rootBurstTotal >= RootBurstMinTotalMs)
                {
                    // Dump top children once per burst (limit spam)
                    if (_rootBucket != null)
                    {
                        var list = new List<KeyValuePair<MethodBase, (double sum, int n)>>(_rootBucket);
                        list.Sort((a, b) => b.Value.sum.CompareTo(a.Value.sum));
                        var rOwner = _rootPeriodic?.DeclaringType?.FullName ?? "<global>";
                        var rName = _rootPeriodic?.Name ?? "<none>";
                        int take = Math.Min(8, list.Count);
                        MapPerfLog.Info($"[root-burst] {rOwner}.{rName} — top children:");
                        var includePct = _rootBurstTotal >= 1.0;
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
            var flags = __state.Flags;
            if ((flags & StateFlagUiContext) != 0 && _uiContextDepth > 0)
                _uiContextDepth--;

            if (_mapScreenFastTimeValid &&
                __instance != null &&
                GetMapScreenType()?.IsInstanceOfType(__instance) == true &&
                (__originalMethod?.Name != null && MapScreenFrameHooks.Contains(__originalMethod.Name)))
            {
                _mapScreenFastTimeValid = false;
            }

            var didInstrument = (__state.ts != 0) && ((flags & StateFlagSkip) == 0);

            if (didInstrument)
            {
                if (__exception != null && _rootBucket != null && _rootPeriodic != null && _callDepth == _rootDepth &&
                    _rootBurstTotal >= RootBurstMinTotalMs)
                {
                    var list = new List<KeyValuePair<MethodBase, (double sum, int n)>>(_rootBucket);
                    list.Sort((a, b) => b.Value.sum.CompareTo(a.Value.sum));
                    var rOwner = _rootPeriodic?.DeclaringType?.FullName ?? "<global>";
                    var rName = _rootPeriodic?.Name ?? "<none>";
                    int take = Math.Min(8, list.Count);
                    MapPerfLog.Info($"[root-burst:EX] {rOwner}.{rName} — top children:");
                    var includePct = _rootBurstTotal >= 1.0;
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
            }
            if (didInstrument)
            {
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
            var paused = PausedSnapshot;
            var tag = paused ? "[PAUSED]" : (FastSnapshot ? "[RUN-FAST]" : "[RUN]");
            MapPerfLog.Info($"-- bucket summary {tag} --");
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
            PeriodicSlicer.PeekQueueStats(out var queueLength, out var queueBackpressureThreshold, out var queueCapacity);
            var skipped = Interlocked.Exchange(ref _childBackpressureSkips, 0);
            KeyValuePair<MethodBase, int>[] shedByMethod = null;
            if (!_childBackpressureSkipsByMethod.IsEmpty)
            {
                shedByMethod = _childBackpressureSkipsByMethod.ToArray();
                for (int i = 0; i < shedByMethod.Length; i++)
                    _childBackpressureSkipsByMethod.TryRemove(shedByMethod[i].Key, out _);
                if (shedByMethod.Length > 0)
                {
                    Array.Sort(shedByMethod, (a, b) => b.Value.CompareTo(a.Value));
                    if (shedByMethod.Length > ChildDropMethodWindowCap)
                        Array.Resize(ref shedByMethod, ChildDropMethodWindowCap);
                }
            }

            if (queueLength > 0 || skipped > 0 || (shedByMethod != null && shedByMethod.Length > 0))
            {
                var pct = queueCapacity > 0 ? (int)(100.0 * queueLength / queueCapacity) : 0;
                var builder = new StringBuilder(320);
                builder.Append("[slice] queue ");
                builder.Append(queueLength);
                builder.Append('/');
                builder.Append(queueCapacity);
                builder.Append(" (");
                builder.Append(pct);
                builder.Append("%) (backpressure ");
                builder.Append(queueBackpressureThreshold);
                builder.Append(") inline skips ");
                builder.Append(skipped);

                if (shedByMethod != null && shedByMethod.Length > 0)
                {
                    var takeDrops = Math.Min(4, shedByMethod.Length);
                    builder.Append(" drops ");
                    for (int i = 0; i < takeDrops; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        builder.Append(FormatMethod(shedByMethod[i].Key));
                        builder.Append('=');
                        builder.Append(shedByMethod[i].Value);
                    }
                }

                MapPerfLog.Info(builder.ToString());
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
            if (PausedSnapshot) return;
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (now < _mapScreenNextLog) return;
            lock (MapScreenProbeLock)
            {
                if (now < _mapScreenNextLog) return;
                _mapScreenNextLog = now + 1.0;
                EnsureMapScreenProbes(instance);
                var fast = FastSnapshot;
                var tag = fast ? "[MapScreen RUN-FAST]" : "[MapScreen RUN]";

                if (_mapScreenProbes == null || _mapScreenProbes.Count == 0)
                {
                    MapPerfLog.Info($"{tag} OnFrameTick {dt:F1} ms — no probe targets found.");
                    return;
                }

                var loggedAny = false;
                foreach (var probe in _mapScreenProbes)
                {
                    int? count = null;
                    try { count = probe.Getter(instance); }
                    catch { }
                    if (!count.HasValue) continue;
                    if (!loggedAny)
                    {
                        MapScreenLogBuilder.Clear();
                        MapScreenLogBuilder.Append(tag);
                        MapScreenLogBuilder.Append(" OnFrameTick ");
                        MapScreenLogBuilder.Append(dt.ToString("F1", CultureInfo.InvariantCulture));
                        MapScreenLogBuilder.Append(" ms — ");
                        loggedAny = true;
                    }
                    else
                    {
                        MapScreenLogBuilder.Append(", ");
                    }
                    MapScreenLogBuilder.Append(probe.Name);
                    MapScreenLogBuilder.Append('=');
                    MapScreenLogBuilder.Append(count.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (!loggedAny)
                {
                    MapPerfLog.Info($"{tag} OnFrameTick {dt:F1} ms — <no counts>");
                    MapScreenLogBuilder.Clear();
                    return;
                }

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsFastOrPaused()
        {
            if (FastOrPausedSnapshot)
                return true;
            return !IsOnMapRunning();
        }

        internal static bool IsOnMapRunning()
        {
            if (!IsOnMap()) return false;
            return !IsPaused();
        }

        private static void EnsureGsmRefs()
        {
            if (_gsmType == null)
                _gsmType = Type.GetType("TaleWorlds.Core.GameStateManager, TaleWorlds.Core", throwOnError: false);
            if (_gsmCurrentProp == null)
                _gsmCurrentProp = _gsmType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
            if (_gsmActiveStateProp == null)
            {
                var cur = _gsmCurrentProp?.GetValue(null);
                if (cur != null)
                    _gsmActiveStateProp = cur.GetType().GetProperty("ActiveState", BindingFlags.Public | BindingFlags.Instance);
            }
        }

        internal static bool IsOnMap()
        {
            try
            {
                EnsureGsmRefs();
                var current = _gsmCurrentProp?.GetValue(null);
                if (_gsmActiveStateProp == null && current != null)
                    _gsmActiveStateProp = current.GetType().GetProperty("ActiveState", BindingFlags.Public | BindingFlags.Instance);
                var active = _gsmActiveStateProp?.GetValue(current);
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
        // Sliced handlers drained under a small per-frame budget
        private static readonly Queue<Action> _qHandlers = new Queue<Action>(4096);
        private static readonly object _lock = new object();
        private static readonly Queue<Action> _qHandlersSlow = new Queue<Action>(); // heavy lane
        private const int MaxQueued = 20000;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, int> _bypass
            = new System.Collections.Concurrent.ConcurrentDictionary<MethodBase, int>();
        // Cache: does this hub actually have any discovered handlers?
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, bool> _hubHasHandlers
            = new System.Collections.Concurrent.ConcurrentDictionary<MethodBase, bool>();
        // When we last concluded "no handlers"; after this point, re-discover.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, long> _hubNoHandlersUntil
            = new System.Collections.Concurrent.ConcurrentDictionary<MethodBase, long>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<HandlerKey, bool> _hubHasHandlersInst
            = new System.Collections.Concurrent.ConcurrentDictionary<HandlerKey, bool>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<HandlerKey, long> _hubNoHandlersUntilInst
            = new System.Collections.Concurrent.ConcurrentDictionary<HandlerKey, long>();
        // Cache discovered handlers to avoid reflection+alloc per tick
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<HandlerKey, List<Action>> _handlerCache
            = new System.Collections.Concurrent.ConcurrentDictionary<HandlerKey, List<Action>>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<HandlerKey, long> _handlerNextRefreshTs
            = new System.Collections.Concurrent.ConcurrentDictionary<HandlerKey, long>();
        private static readonly ConcurrentDictionary<HourlyHubKey, byte> _hourlyInFlight
            = new ConcurrentDictionary<HourlyHubKey, byte>();
        private static readonly ConcurrentDictionary<HandlerKey, byte> _batchInFlight
            = new ConcurrentDictionary<HandlerKey, byte>();
        private static readonly ConcurrentDictionary<HandlerKey, long> _recentFireUntil
            = new ConcurrentDictionary<HandlerKey, long>();
        private const double HubRepeatTtlSec = 0.5;
        private static int _gateRejects;
        private static int _capRejects;
        private static long _nextGateLogTs;
        private static long _nextPumpLogTs;
        private static long _nextQueuedHandlersLogTs;
        private static long _nextQueuePressureLogTs;
        private static long _nextQueueDropLogTs;
        private static long _pumpCooldownUntil;
        private static long _exitHeadroom;
        private static long _exitEmpty;
        private static long _exitBudget;
        private static long _exitOverrun;
        private static long _exitCap;
        private static long _exitDone;
        private const double PumpLogCooldownSeconds = 0.5;
        private const double QueuePressureCooldownSeconds = 0.5;
        private const int QueuePressureThreshold = 15_000;
        private const double QueueDropLogCooldownSeconds = 0.5;
        private const double GateLogIntervalSeconds = 10.0;
        private const int QueueBackpressureHeadroomDefault = 512;
        private const int QueueBackpressureHeadroomFast = 1_024;
        private const int QueueBackpressureHeadroomPaused = 256;
        private const int QueueBackpressureHysteresisDelta = 512;
        private const int QueueBackpressureMinEnterHeadroom = 1_024;
        private static bool _queueBackpressureLatched;
        private const double QueuedHandlersLogCooldownSeconds = 0.5;
        private const double HandlerCacheRefreshMs = 5000.0;
        private const int HandlerCacheMax = 16384;
        private const double HubNoHandlersRetryMs = 5000.0;
        private static readonly int BatchBacklogThreshold = Math.Min(QueuePressureThreshold, SubModule.PumpBacklogBoostThreshold);
        private const double BatchChildErrorCooldownSeconds = 0.25;
        private static long _nextBatchChildErrorLogTs;
        private static readonly double[] OvershootEdgesMs = { 0.25, 0.5, 1, 2, 4, 8, 16, 32 };
        private static readonly long[] OvershootBins = new long[9];
        private const double LongChildLogCooldownSeconds = 0.5;
        private static readonly long LongChildLogCooldownTicks = (long)(Stopwatch.Frequency * LongChildLogCooldownSeconds);
        private static readonly ConcurrentDictionary<string, long> LongChildNextLogTicks =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        [ThreadStatic] internal static long PumpDeadlineTicks;

        private sealed class BatchThunk
        {
            public readonly List<Action> List;
            public int Index;
            public readonly string Name;

            private readonly Action _onComplete;
            private int _completed;

            public BatchThunk(List<Action> list, string name, Action onComplete)
            {
                List = list;
                Name = name;
                _onComplete = onComplete;
            }

            private void Complete()
            {
                if (_onComplete == null) return;
                if (Interlocked.Exchange(ref _completed, 1) != 0) return;
                try { _onComplete(); }
                catch { }
            }

            public void Abort() => Complete();

            public void Run()
            {
                var q = QueueLength;
                var budgetMs = SubModule.FastSnapshot ? 1.5 :
                    (SubModule.PausedSnapshot ? 4.0 : (q >= BatchBacklogThreshold ? 4.0 : 3.0));
                var start = Stopwatch.GetTimestamp();
                var localDeadline = start + (long)(budgetMs * SubModule.MsToTicks);
                var pumpDeadline = PumpDeadlineTicks;
                var deadline = pumpDeadline != 0
                    ? Math.Min(localDeadline, pumpDeadline)
                    : localDeadline;
                // The pump runs single-threaded on the main thread; if that changes, index management needs synchronization.
                var headroomTicks = (long)(1.5 * SubModule.MsToTicks);
                var batchLabel = string.IsNullOrEmpty(Name) ? "<batch>" : Name;
                var maxPerRun = SubModule.PausedSnapshot ? 24 : (SubModule.FastSnapshot ? 4 : 12);
                if (maxPerRun < 1) maxPerRun = 1;
                var processed = 0;
                while (Index < List.Count)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (now + headroomTicks >= deadline)
                        break;

                    var childIndex = Index;
                    var child = List[childIndex];
                    var t0 = now;
                    try
                    {
                        child();
                    }
                    catch (Exception ex)
                    {
                        MaybeLogBatchChildError(batchLabel, ex);
                    }
                    finally
                    {
                        Index = childIndex + 1;
                    }

                    var after = Stopwatch.GetTimestamp();
                    var elapsedMs = (after - t0) * SubModule.TicksToMs;
                    var limit = SubModule.PausedSnapshot ? 16.0 : 6.0;
                    if (elapsedMs > limit)
                    {
                        var method = child.Method;
                        string who;
                        if (method != null)
                        {
                            var typeName = method.DeclaringType?.Name;
                            var methodName = method.Name;
                            if (!string.IsNullOrEmpty(typeName))
                            {
                                who = string.IsNullOrEmpty(methodName) ? typeName : typeName + "." + methodName;
                            }
                            else
                            {
                                who = methodName ?? batchLabel;
                            }
                        }
                        else
                        {
                            who = batchLabel;
                        }

                        if (ShouldLogLongChild(who, after))
                        {
                            MapPerfLog.Warn($"[slice] long child {who} #{childIndex} {elapsedMs:F1} ms (paused {SubModule.PausedSnapshot})");
                        }
                    }

                    if (after >= deadline)
                        break;

                    processed++;
                    if (processed >= maxPerRun)
                        break;
                }
                if (Index < List.Count)
                {
                    if (!PeriodicSlicer.EnqueueAction(Run))
                    {
                        TryLogQueueDrop(List.Count - Index);
                        Complete();
                    }
                }
                else
                {
                    Complete();
                }
                // Handler lists are cached for reuse; keep the list intact for future batches once drained.
            }
        }

        private readonly struct HandlerKey : IEquatable<HandlerKey>
        {
            public HandlerKey(MethodInfo method, object target)
            {
                Method = method;
                Target = target;
            }

            public MethodInfo Method { get; }
            public object Target { get; }

            public bool Equals(HandlerKey other)
                => ReferenceEquals(Method, other.Method) && ReferenceEquals(Target, other.Target);

            public override bool Equals(object obj)
                => obj is HandlerKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var methodHash = Method != null ? RuntimeHelpers.GetHashCode(Method) : 0;
                    var targetHash = Target != null ? RuntimeHelpers.GetHashCode(Target) : 0;
                    return (methodHash * 397) ^ targetHash;
                }
            }
        }

        private readonly struct HourlyHubKey : IEquatable<HourlyHubKey>
        {
            public HourlyHubKey(MethodInfo method, object target, long hour)
            {
                Method = method;
                Target = target;
                Hour = hour;
            }

            public MethodInfo Method { get; }
            public object Target { get; }
            public long Hour { get; }

            public bool Equals(HourlyHubKey other)
                => ReferenceEquals(Method, other.Method)
                   && ReferenceEquals(Target, other.Target)
                   && Hour == other.Hour;

            public override bool Equals(object obj)
                => obj is HourlyHubKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var methodHash = Method != null ? RuntimeHelpers.GetHashCode(Method) : 0;
                    var targetHash = Target != null ? RuntimeHelpers.GetHashCode(Target) : 0;
                    return (((methodHash * 397) ^ targetHash) * 397) ^ Hour.GetHashCode();
                }
            }
        }

        private static List<Action> DeduplicateHandlers(List<Action> actions)
        {
            if (actions == null || actions.Count < 2)
                return actions;

            var seen = new HashSet<(object target, MethodInfo method)>();
            var deduped = new List<Action>(actions.Count);
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;

                var method = action.Method;
                if (method == null) continue;

                if (seen.Add((action.Target, method)))
                    deduped.Add(action);
            }

            return deduped.Count == actions.Count ? actions : deduped;
        }

        private static void NoteHubFire(HandlerKey key)
        {
            if (key.Method == null)
                return;
            if (!IsOur(key.Method.DeclaringType))
                return;

            var until = Stopwatch.GetTimestamp()
                        + (long)(HubRepeatTtlSec * Stopwatch.Frequency);
            _recentFireUntil[key] = until;
        }

        private static void MaybeLogRepeat(HandlerKey key)
        {
            if (key.Method == null)
                return;
            if (!IsOur(key.Method.DeclaringType))
                return;

            if (_recentFireUntil.TryGetValue(key, out var until)
                && Stopwatch.GetTimestamp() < until)
            {
                MapPerfLog.Warn($"[slice] repeat hub within {HubRepeatTtlSec * 1000:F0} ms: {key.Method.Name}");
            }
        }

        private static List<Action> GetOrDiscoverHandlers(object dispatcher, MethodBase hub, string methodName)
        {
            var mi = hub as MethodInfo;
            var key = new HandlerKey(mi, dispatcher);
            var now = Stopwatch.GetTimestamp();
            if (_handlerCache.TryGetValue(key, out var cached)
                && _handlerNextRefreshTs.TryGetValue(key, out var next)
                && now < next)
            {
                return cached;
            }

            var discovered = dispatcher != null
                ? DiscoverSubscriberActions(dispatcher, hub, methodName)
                : DiscoverSubscriberActions(hub?.DeclaringType, methodName);

            var filtered = (discovered ?? new List<Action>(0))
                .Where(a => IsOur(a?.Method?.DeclaringType))
                .ToList();

            var actions = DeduplicateHandlers(filtered) ?? new List<Action>(0);
            _handlerCache[key] = actions;
            _handlerNextRefreshTs[key] = now
                                          + (long)(Stopwatch.Frequency * (HandlerCacheRefreshMs / 1000.0));
            if (_handlerCache.Count > HandlerCacheMax)
            {
                foreach (var kv in _handlerNextRefreshTs
                             .OrderBy(kvp => kvp.Value)
                             .Take(Math.Max(1, _handlerCache.Count / 4)))
                {
                    _handlerCache.TryRemove(kv.Key, out _);
                    _handlerNextRefreshTs.TryRemove(kv.Key, out _);
                }
            }
            return actions;
        }

        public static bool RedirectDaily(object dispatcher)
        {
            MethodBase hub = null;
            if (dispatcher != null)
            {
                var t = dispatcher.GetType();
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                hub = SubModule.GetZeroParamMethod(t, "OnDailyTick", F)
                      ?? SubModule.GetZeroParamMethod(t, "DailyTick", F);
            }
            return Redirect(dispatcher, hub, "OnDailyTick");
        }

        private static bool TryEnterHourlyGate(HourlyHubKey key)
        {
            if (key.Method == null)
                return true;
            return _hourlyInFlight.TryAdd(key, 0);
        }

        private static void ExitHourlyGate(HourlyHubKey key)
        {
            if (key.Method == null)
                return;
            _hourlyInFlight.TryRemove(key, out _);
        }

        internal static bool RedirectHourlyWithGate(object dispatcher, MethodBase original, long hour, out bool enqueued)
        {
            enqueued = false;
            if (hour == long.MinValue)
            {
                var handledFallback = RedirectAny(dispatcher, original, "OnHourlyTick");
                enqueued = handledFallback;
                return handledFallback;
            }

            var hub = original;
            if (hub == null && dispatcher != null)
            {
                var t = dispatcher.GetType();
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                hub = SubModule.GetZeroParamMethod(t, "OnHourlyTick", F)
                      ?? SubModule.GetZeroParamMethod(t, "HourlyTick", F);
            }

            if (hub == null)
            {
                var handledDirect = Redirect(dispatcher, hub, "OnHourlyTick");
                enqueued = handledDirect;
                return handledDirect;
            }
            var hubInfo = hub as MethodInfo;
            var key = new HourlyHubKey(hubInfo, dispatcher, hour);
            if (!TryEnterHourlyGate(key))
                return true;

            var handled = false;
            try
            {
                handled = Redirect(dispatcher, hub, "OnHourlyTick", () => ExitHourlyGate(key));
                if (handled)
                    enqueued = true;
                return handled;
            }
            finally
            {
                if (!handled)
                    ExitHourlyGate(key);
            }
        }

        public static bool RedirectHourly(object dispatcher)
        {
            MethodBase hub = null;
            if (dispatcher != null)
            {
                var t = dispatcher.GetType();
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                hub = SubModule.GetZeroParamMethod(t, "OnHourlyTick", F)
                      ?? SubModule.GetZeroParamMethod(t, "HourlyTick", F);
            }
            return Redirect(dispatcher, hub, "OnHourlyTick");
        }

        public static bool RedirectAny(object instance, MethodBase original, string methodName, Action onComplete = null)
        {
            if (!SubModule.MayEnqueueNow())
            {
                NoteGateReject();
                return false;
            }
            GetQueueStats(out var qlenGate, out _, out _);
            if (qlenGate >= SubModule.MaxQueueForEnqueue)
            {
                NoteCapReject();
                return false;
            }

            if (instance != null)
                return Redirect(instance, original, methodName, onComplete);
            var t = original?.DeclaringType;
            return t != null && RedirectStatic(t, original, methodName, onComplete);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool EnqueueAction(Action action)
            => TryEnqueue(action, _qHandlers);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool EnqueueSlowAction(Action action)
            => TryEnqueue(action, _qHandlersSlow);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryEnqueue(Action action, Queue<Action> queue)
        {
            if (action == null) return false;
            if (!SubModule.MayEnqueueNow())
            {
                NoteGateReject();
                return false;
            }
            GetQueueStats(out var qlen, out _, out _);
            if (qlen >= SubModule.MaxQueueForEnqueue)
            {
                NoteCapReject();
                return false;
            }

            int total;
            lock (_lock)
            {
                var combined = _qHandlers.Count + _qHandlersSlow.Count;
                if (combined >= MaxQueued)
                {
                    TryLogQueueDrop(1);
                    return false;
                }
                queue.Enqueue(action);
                total = combined + 1;
            }

            if (total >= QueuePressureThreshold)
                TryLogQueuePressure_NoLock(total);
            return true;
        }

        private static bool EnqueueBatch(List<Action> actions, string name, Action onComplete = null)
        {
            if (actions == null || actions.Count == 0) return false;
            if (!SubModule.MayEnqueueNow())
            {
                NoteGateReject();
                return false;
            }
            GetQueueStats(out var qlen, out _, out _);
            if (qlen >= SubModule.MaxQueueForEnqueue)
            {
                NoteCapReject();
                return false;
            }

            var thunk = new BatchThunk(actions, name, onComplete);
            bool isHeavy = !string.IsNullOrEmpty(name) &&
                           (name.IndexOf("Daily", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Weekly", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("TickPartialHourlyAi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("AiHourlyTick", StringComparison.OrdinalIgnoreCase) >= 0);
            var enqueuedOk = isHeavy ? EnqueueSlowAction(thunk.Run) : EnqueueAction(thunk.Run);
            if (!enqueuedOk)
            {
                TryLogQueueDrop(actions.Count);
                thunk.Abort();
                return false;
            }

            var now = Stopwatch.GetTimestamp();
            if (now >= Volatile.Read(ref _nextQueuedHandlersLogTs))
            {
                var shown = Math.Min(actions.Count, 5000);
                var suffix = actions.Count > shown ? "+" : string.Empty;
                var label = string.IsNullOrEmpty(name) ? "" : $" {name}";
                MapPerfLog.Debug($"[slice] queued {shown}{suffix}{label} handlers");
                Volatile.Write(ref _nextQueuedHandlersLogTs,
                    now + (long)(Stopwatch.Frequency * QueuedHandlersLogCooldownSeconds));
            }

            return true;
        }

        internal static int QueueLength
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                lock (_lock)
                {
                    return _qHandlers.Count + _qHandlersSlow.Count;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsQueueNearFull()
        {
            lock (_lock)
            {
                var count = _qHandlers.Count + _qHandlersSlow.Count;
                GetQueueBackpressureThresholds(out var enterThreshold, out var exitThreshold);
                if (_queueBackpressureLatched)
                {
                    if (count < exitThreshold)
                    {
                        _queueBackpressureLatched = false;
                        return false;
                    }
                    return true;
                }

                if (count >= enterThreshold)
                {
                    _queueBackpressureLatched = true;
                    return true;
                }

                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void GetQueueStats(out int length, out int backpressureThreshold, out int capacity)
        {
            lock (_lock)
            {
                length = _qHandlers.Count + _qHandlersSlow.Count;
                GetQueueBackpressureThresholds(out var enterThreshold, out var exitThreshold);
                var latched = _queueBackpressureLatched;
                if (latched)
                {
                    if (length < exitThreshold)
                    {
                        _queueBackpressureLatched = false;
                        latched = false;
                    }
                }
                else if (length >= enterThreshold)
                {
                    _queueBackpressureLatched = true;
                    latched = true;
                }

                backpressureThreshold = latched ? exitThreshold : enterThreshold;
                capacity = MaxQueued;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void PeekQueueStats(out int length, out int backpressureThreshold, out int capacity)
        {
            lock (_lock)
            {
                length = _qHandlers.Count + _qHandlersSlow.Count;
                GetQueueBackpressureThresholds(out var enterThreshold, out var exitThreshold);
                backpressureThreshold = _queueBackpressureLatched ? exitThreshold : enterThreshold;
                capacity = MaxQueued;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetQueueBackpressureThresholds(out int enterThreshold, out int exitThreshold)
        {
            var headroom = QueueBackpressureHeadroomDefault;
            if (SubModule.PausedSnapshot)
                headroom = QueueBackpressureHeadroomPaused;
            else if (SubModule.FastSnapshot)
                headroom = QueueBackpressureHeadroomFast;

            var enterHeadroom = headroom;
            if (!SubModule.PausedSnapshot)
                enterHeadroom = Math.Max(headroom, QueueBackpressureMinEnterHeadroom);

            var exitHeadroom = enterHeadroom + QueueBackpressureHysteresisDelta;

            enterThreshold = ClampQueueThreshold(enterHeadroom);
            exitThreshold = ClampQueueThreshold(exitHeadroom);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ClampQueueThreshold(int headroom)
        {
            var threshold = MaxQueued - headroom;
            if (threshold < 0) return 0;
            if (threshold > MaxQueued) return MaxQueued;
            return threshold;
        }

        private static void TryLogQueueDrop(int dropped)
        {
            if (dropped <= 0) return;
            var now = Stopwatch.GetTimestamp();
            var next = Volatile.Read(ref _nextQueueDropLogTs);
            if (now < next) return;

            var shown = Math.Min(dropped, 5000);
            var suffix = dropped > shown ? "+" : string.Empty;
            MapPerfLog.Warn($"[slice] dropped {shown}{suffix} (queue full)");
            Volatile.Write(ref _nextQueueDropLogTs,
                now + (long)(Stopwatch.Frequency * QueueDropLogCooldownSeconds));
        }

        internal static bool EnqueueActionWithBypass(MethodBase method, Action action)
        {
            if (action == null) return false;
            if (!SubModule.MayEnqueueNow())
            {
                NoteGateReject();
                return false;
            }
            GetQueueStats(out var qlen, out _, out _);
            if (qlen >= SubModule.MaxQueueForEnqueue)
            {
                NoteCapReject();
                return false;
            }

            return EnqueueAction(() =>
            {
                EnterBypass(method);
                try { action(); }
                finally { ExitBypass(method); }
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsOur(Type type)
            => type?.Assembly == typeof(SubModule).Assembly;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsSimulationCritical(MethodBase method)
        {
            var ns = method?.DeclaringType?.Namespace ?? string.Empty;
            if (!ns.StartsWith("TaleWorlds.CampaignSystem", StringComparison.Ordinal))
                return false;

            var name = method?.Name;
            if (string.IsNullOrEmpty(name))
                return false;

            if (name.IndexOf("DailyTick", StringComparison.Ordinal) >= 0
                || name.IndexOf("HourlyTick", StringComparison.Ordinal) >= 0
                || name.IndexOf("WeeklyTick", StringComparison.Ordinal) >= 0
                || name.IndexOf("RealTick", StringComparison.Ordinal) >= 0
                || name.IndexOf("OnTick", StringComparison.Ordinal) >= 0
                || name.IndexOf("OnMapModeTick", StringComparison.Ordinal) >= 0
                || name.IndexOf("PeriodicDailyTick", StringComparison.Ordinal) >= 0
                || name.IndexOf("PeriodicHourlyTick", StringComparison.Ordinal) >= 0
                || string.Equals(name, "PeriodicQuarterDailyTick", StringComparison.Ordinal)
                || string.Equals(name, "TickPartialHourlyAi", StringComparison.Ordinal)
                || string.Equals(name, "AiHourlyTick", StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(name, "Tick", StringComparison.Ordinal);
        }

        private static bool TryDeferHubInvoke(
            object target,
            MethodInfo mi,
            MethodBase bypassKey,
            Action onComplete)
        {
            if (mi == null || mi.IsAbstract || mi.ContainsGenericParameters || mi.GetParameters().Length != 0)
                return false;
            if (!SubModule.FastOrPausedSnapshot)
                return false;
            if (!IsOur(mi.DeclaringType))
                return false;

            return EnqueueActionWithBypass(bypassKey ?? mi, () =>
            {
                try { mi.Invoke(target, null); }
                catch (Exception ex)
                {
                    MapPerfLog.Error($"slice hub {mi.DeclaringType?.FullName}.{mi.Name} failed", ex);
                }
                finally
                {
                    onComplete?.Invoke();
                }
            });
        }

        private static bool TryEnterBatchGate(HandlerKey key) => _batchInFlight.TryAdd(key, 0);

        private static void ExitBatchGate(HandlerKey key) => _batchInFlight.TryRemove(key, out _);

        private static Action Chain(Action first, Action second)
            => () =>
            {
                try { first?.Invoke(); }
                finally { second?.Invoke(); }
            };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void NoteGateReject() => Interlocked.Increment(ref _gateRejects);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void NoteCapReject() => Interlocked.Increment(ref _capRejects);

        private static void MaybeLogGateStats()
        {
            var now = Stopwatch.GetTimestamp();
            var next = Volatile.Read(ref _nextGateLogTs);
            if (now < next) return;
            if (Interlocked.CompareExchange(
                    ref _nextGateLogTs,
                    now + (long)(Stopwatch.Frequency * GateLogIntervalSeconds),
                    next) != next)
                return;

            var gate = Interlocked.Exchange(ref _gateRejects, 0);
            var cap = Interlocked.Exchange(ref _capRejects, 0);
            if (gate == 0 && cap == 0) return;

            MapPerfLog.Debug($"[slice] gate rejects={gate} cap rejects={cap}");
        }

        private static bool Redirect(object dispatcher, MethodBase hub, string methodName, Action onComplete = null)
        {
            try
            {
                if (!SubModule.MayEnqueueNow())
                {
                    NoteGateReject();
                    return false;
                }
                GetQueueStats(out var qlenGate, out _, out _);
                if (qlenGate >= SubModule.MaxQueueForEnqueue)
                {
                    NoteCapReject();
                    return false;
                }

                if (dispatcher == null) return false;
                if (hub != null && IsSimulationCritical(hub))
                    return false;
                List<Action> actions;
                var nowTs = Stopwatch.GetTimestamp();
                var hubMethod = hub as MethodInfo;
                var hasInstanceKey = hubMethod != null && dispatcher != null;
                var handlerKey = hasInstanceKey ? new HandlerKey(hubMethod, dispatcher) : default;
                var enteredGate = false;
                if (hasInstanceKey)
                {
                    if (!TryEnterBatchGate(handlerKey))
                        return true;
                    enteredGate = true;
                    MaybeLogRepeat(handlerKey);
                    onComplete = Chain(onComplete, () => ExitBatchGate(handlerKey));
                }
                long retryTs = 0L;
                if (hub != null)
                {
                    retryTs = hasInstanceKey
                        ? (_hubNoHandlersUntilInst.TryGetValue(handlerKey, out var untilTsInst) ? untilTsInst : 0L)
                        : (_hubNoHandlersUntil.TryGetValue(hub, out var untilTs) ? untilTs : 0L);
                }
                var shouldSkip = false;
                if (hub != null)
                {
                    shouldSkip = hasInstanceKey
                        ? (_hubHasHandlersInst.TryGetValue(handlerKey, out var hasInst) && !hasInst && nowTs < retryTs)
                        : (_hubHasHandlers.TryGetValue(hub, out var hasHandlers) && !hasHandlers && nowTs < retryTs);
                }
                if (shouldSkip)
                {
                    actions = new List<Action>(0);
                }
                else
                {
                    actions = GetOrDiscoverHandlers(dispatcher, hub, methodName);
                    if (hub != null)
                    {
                        var found = actions.Count > 0;
                        if (hasInstanceKey)
                        {
                            _hubHasHandlersInst[handlerKey] = found;
                            if (!found)
                            {
                                _hubNoHandlersUntilInst[handlerKey] =
                                    nowTs + (long)(Stopwatch.Frequency * (HubNoHandlersRetryMs / 1000.0));
                            }
                            else
                            {
                                _hubNoHandlersUntilInst.TryRemove(handlerKey, out _);
                            }
                        }
                        else
                        {
                            _hubHasHandlers[hub] = found;
                            if (!found)
                            {
                                _hubNoHandlersUntil[hub] =
                                    nowTs + (long)(Stopwatch.Frequency * (HubNoHandlersRetryMs / 1000.0));
                            }
                            else
                            {
                                _hubNoHandlersUntil.TryRemove(hub, out _);
                            }
                        }
                    }
                }
                var name = hub?.Name ?? methodName;
                if (hasInstanceKey)
                    onComplete = Chain(onComplete, () => NoteHubFire(handlerKey));

                if (actions.Count == 0)
                {
                    var handled = TryDeferHubInvoke(dispatcher, hubMethod, hub, onComplete);
                    if (!handled && enteredGate)
                        ExitBatchGate(handlerKey);
                    return handled;
                }

                actions = DeduplicateHandlers(actions);
                var enqueued = EnqueueBatch(actions, name, onComplete);
                if (!enqueued && enteredGate)
                    ExitBatchGate(handlerKey);
                return enqueued;
            }
            catch (Exception ex)
            {
                MapPerfLog.Error($"slice redirect {hub?.Name ?? methodName} failed", ex);
                return false;
            }
        }

        private static bool RedirectStatic(Type hubType, MethodBase original, string methodName, Action onComplete = null)
        {
            try
            {
                if (!SubModule.MayEnqueueNow())
                {
                    NoteGateReject();
                    return false;
                }
                GetQueueStats(out var qlenGate, out _, out _);
                if (qlenGate >= SubModule.MaxQueueForEnqueue)
                {
                    NoteCapReject();
                    return false;
                }

                if (hubType == null) return false;
                var hubKey = (MethodBase)original
                               ?? (MethodBase)hubType.GetMethod(methodName,
                                   BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static | BindingFlags.Instance);
                if (hubKey != null && IsSimulationCritical(hubKey))
                    return false;
                var hubName = hubKey?.Name ?? methodName;
                var hubInfo = hubKey as MethodInfo;
                var hasKey = hubInfo != null;
                var handlerKey = hasKey ? new HandlerKey(hubInfo, null) : default;
                var enteredGate = false;
                if (hasKey)
                {
                    if (!TryEnterBatchGate(handlerKey))
                        return true;
                    enteredGate = true;
                    MaybeLogRepeat(handlerKey);
                    onComplete = Chain(onComplete, () => ExitBatchGate(handlerKey));
                }
                List<Action> actions;
                if (hubKey != null && _hubHasHandlers.TryGetValue(hubKey, out var hasHandlers) && !hasHandlers)
                {
                    var nowTs = Stopwatch.GetTimestamp();
                    if (_hubNoHandlersUntil.TryGetValue(hubKey, out var untilTs) && nowTs < untilTs)
                    {
                        actions = new List<Action>(0);
                    }
                    else
                    {
                        actions = GetOrDiscoverHandlers(null, hubKey, methodName);
                        var found = actions.Count > 0;
                        _hubHasHandlers[hubKey] = found;
                        if (!found)
                            _hubNoHandlersUntil[hubKey] = nowTs + (long)(Stopwatch.Frequency * (HubNoHandlersRetryMs / 1000.0));
                        else
                            _hubNoHandlersUntil.TryRemove(hubKey, out _);
                    }
                }
                else
                {
                    actions = GetOrDiscoverHandlers(null, hubKey, methodName);
                    if (hubKey != null)
                    {
                        var nowTs = Stopwatch.GetTimestamp();
                        var found = actions.Count > 0;
                        _hubHasHandlers[hubKey] = found;
                        if (!found)
                            _hubNoHandlersUntil[hubKey] = nowTs + (long)(Stopwatch.Frequency * (HubNoHandlersRetryMs / 1000.0));
                        else
                            _hubNoHandlersUntil.TryRemove(hubKey, out _);
                    }
                }

                if (hasKey)
                    onComplete = Chain(onComplete, () => NoteHubFire(handlerKey));

                if (actions.Count == 0)
                {
                    var handled = TryDeferHubInvoke(null, hubInfo, hubKey, onComplete);
                    if (!handled && enteredGate)
                        ExitBatchGate(handlerKey);
                    return handled;
                }

                var logName = hubName;
                if (hubKey != null && hubKey.IsStatic)
                    logName += " (static hub)";
                actions = DeduplicateHandlers(actions);
                var enqueued = EnqueueBatch(actions, logName, onComplete);
                if (!enqueued && enteredGate)
                    ExitBatchGate(handlerKey);
                return enqueued;
            }
            catch (Exception ex)
            {
                MapPerfLog.Error($"slice redirect static {methodName} failed", ex);
                return false;
            }
        }
        public static void ClearCachesIfIdle()
        {
            lock (_lock)
            {
                if (_qHandlers.Count != 0 || _qHandlersSlow.Count != 0)
                    return;
                _queueBackpressureLatched = false;
                SubModule.ResetChildBackpressureCounters();
            }

            _handlerCache.Clear();
            _handlerNextRefreshTs.Clear();
            _hubHasHandlersInst.Clear();
            _hubNoHandlersUntilInst.Clear();
            _hubHasHandlers.Clear();
            _hubNoHandlersUntil.Clear();
            _batchInFlight.Clear();
            _recentFireUntil.Clear();
            ResetHourlyGates();
        }

        public static void ResetHourlyGates()
        {
            _hourlyInFlight.Clear();
            _batchInFlight.Clear();
            _recentFireUntil.Clear();
            lock (_lock) { _qHandlersSlow.Clear(); } // clear slow lane too
        }

        public static void Pump(double msBudget)
        {
            MaybeLogGateStats();

            if (msBudget <= 0.0) return;

            var inCooldownTrickle = false;
            if (Stopwatch.GetTimestamp() < Volatile.Read(ref _pumpCooldownUntil))
            {
                // allow a tiny trickle during cooldown if backlog is large in fast-time
                if (!(SubModule.FastSnapshot && QueueLength >= MapPerfConfig.PumpBacklogBoostThreshold))
                    return;

                // clamp to a safe trickle budget
                msBudget = Math.Min(msBudget, 3.0);
                inCooldownTrickle = true;
            }

            var budgetTicks = (long)(msBudget * SubModule.MsToTicks);
            var globalStart = Stopwatch.GetTimestamp();
            var start = globalStart;
            var deadline = start + budgetTicks;
            PumpDeadlineTicks = deadline;
            double budget = msBudget;
            int pumped = 0;
            var throttle = true;

            var overrunLimit = SubModule.PausedSnapshot ? 32.0
                : (SubModule.FastSnapshot ? 6.0 : 3.5);
            var pumpedCap = SubModule.PausedSnapshot ? 0
                : (SubModule.FastSnapshot ? Math.Min(6, 2 + Math.Max(1, QueueLength) / 400) : 3);
            if (SubModule.FastSnapshot && QueueLength >= 2000)
                pumpedCap = Math.Min(pumpedCap, 2);
            if (inCooldownTrickle)
                pumpedCap = Math.Min(pumpedCap, 2);
            long overshoot = 0;
            var pumpHeadroomTicks = (long)(2.0 * SubModule.MsToTicks);
            string exitReason = string.Empty;
            try
            {
                if (pumpedCap <= 0)
                {
                    exitReason = "cap";
                }
                else
                {
                    while (true)
                    {
                        Action action = null;
                        if (Stopwatch.GetTimestamp() + pumpHeadroomTicks >= deadline)
                        {
                            exitReason = "headroom";
                            break;
                        }
                        lock (_lock)
                        {
                            // pick lane: in fast-time never start slow-lane items
                            if (SubModule.FastSnapshot)
                            {
                                if (_qHandlers.Count > 0)
                                {
                                    action = _qHandlers.Dequeue();
                                }
                                else
                                {
                                    exitReason = "slow-defer-fast";
                                    break;
                                }
                            }
                            else
                            {
                                if (_qHandlersSlow.Count > 0)
                                {
                                    action = _qHandlersSlow.Dequeue();
                                }
                                else if (_qHandlers.Count > 0)
                                {
                                    action = _qHandlers.Dequeue();
                                }
                                else
                                {
                                    exitReason = "empty";
                                    break;
                                }
                            }
                        }

                        var aStart = Stopwatch.GetTimestamp();
                        try { action(); }
                        catch (Exception ex) { MapPerfLog.Error("[slice] pumped action failed", ex); }
                        var aTicks = Stopwatch.GetTimestamp() - aStart;
                        var tookMs = aTicks * SubModule.TicksToMs;
                        if (SubModule.FastSnapshot && tookMs > 3.0)
                            MapPerfLog.Debug($"[slice] long action {tookMs:F1} ms (fast)");
                        pumped++;
                        budget -= Math.Max(0.0, tookMs);

                        if (SubModule.FastSnapshot && tookMs > 25.0)
                        {
                            // break earlier on long items in fast time
                            exitReason = "slow-item";
                            break;
                        }

                        if (budget <= 0.0)
                        {
                            exitReason = "budget";
                            break;
                        }
                        if (throttle && tookMs > overrunLimit)
                        {
                            exitReason = "overrun";
                            break;
                        }
                        if (pumped >= pumpedCap)
                        {
                            exitReason = "cap";
                            break;
                        }
                    }
                }
            }
            finally
            {
                PumpDeadlineTicks = 0;
                var end = Stopwatch.GetTimestamp();
                overshoot = (end - globalStart) - budgetTicks;
            }

            int remaining;
            lock (_lock) remaining = _qHandlers.Count + _qHandlersSlow.Count;
            var now = Stopwatch.GetTimestamp();
            var overshootMs = overshoot * SubModule.TicksToMs;

            if (overshoot > 0)
            {
                var edges = OvershootEdgesMs;
                var bin = edges.Length;
                while (bin > 0 && overshootMs <= edges[bin - 1])
                    bin--;
                Interlocked.Increment(ref OvershootBins[bin]);
                var warn = SubModule.PausedSnapshot ? overshootMs > 8.0
                    : (SubModule.FastSnapshot ? overshootMs > 24.0 : overshootMs > 8.0);

                var msg = $"[slice] pump overshoot {overshootMs:F2} ms (pumped {pumped}, rem {remaining}, paused {SubModule.PausedSnapshot})";
                if (warn)
                    MapPerfLog.Warn(msg);
                else
                    MapPerfLog.Debug(msg);

            }

            if (SubModule.FastSnapshot && (overshootMs > 24.0 || exitReason == "slow-item"))
            {
                Volatile.Write(ref _pumpCooldownUntil, Stopwatch.GetTimestamp()
                    + (long)(Stopwatch.Frequency * MapPerfConfig.PostSpikeNoPumpSec));
            }
            if (string.IsNullOrEmpty(exitReason))
                exitReason = "done";

            switch (exitReason)
            {
                case "headroom":
                    Interlocked.Increment(ref _exitHeadroom);
                    break;
                case "empty":
                    Interlocked.Increment(ref _exitEmpty);
                    break;
                case "slow-defer-fast":
                    Interlocked.Increment(ref _exitEmpty);
                    break;
                case "budget":
                    Interlocked.Increment(ref _exitBudget);
                    break;
                case "overrun":
                    Interlocked.Increment(ref _exitOverrun);
                    break;
                case "cap":
                    Interlocked.Increment(ref _exitCap);
                    break;
                case "done":
                    Interlocked.Increment(ref _exitDone);
                    break;
            }

            if (pumped > 0 && (remaining == 0 || pumped >= 64) &&
                now >= Volatile.Read(ref _nextPumpLogTs))
            {
                long ReadExit(ref long value) => Interlocked.Read(ref value);
                MapPerfLog.Info(
                    $"[slice] pumped {pumped} (rem {remaining}) reason={exitReason} dist " +
                    $"H:{ReadExit(ref _exitHeadroom)} E:{ReadExit(ref _exitEmpty)} " +
                    $"B:{ReadExit(ref _exitBudget)} O:{ReadExit(ref _exitOverrun)} " +
                    $"C:{ReadExit(ref _exitCap)} D:{ReadExit(ref _exitDone)}");
                long ReadBin(int i) => Interlocked.Read(ref OvershootBins[i]);
                MapPerfLog.Info(
                    "[slice] overshoot ms bins: " +
                    $"<=0.25:{ReadBin(0)} <=0.5:{ReadBin(1)} <=1:{ReadBin(2)} <=2:{ReadBin(3)} " +
                    $"<=4:{ReadBin(4)} <=8:{ReadBin(5)} <=16:{ReadBin(6)} <=32:{ReadBin(7)} >32:{ReadBin(8)}");
                ResetPumpStatsWindow();
                Volatile.Write(ref _nextPumpLogTs, now + (long)(Stopwatch.Frequency * PumpLogCooldownSeconds));
            }
        }

        private static bool ShouldLogLongChild(string key, long now)
        {
            if (string.IsNullOrEmpty(key))
                return true;

            var dict = LongChildNextLogTicks;
            while (true)
            {
                if (!dict.TryGetValue(key, out var nextAllowed))
                {
                    if (dict.TryAdd(key, now + LongChildLogCooldownTicks))
                        return true;
                    continue;
                }

                if (now < nextAllowed)
                    return false;

                var updated = now + LongChildLogCooldownTicks;
                if (dict.TryUpdate(key, updated, nextAllowed))
                    return true;
            }
        }

        private static void ResetPumpStatsWindow()
        {
            Interlocked.Exchange(ref _exitHeadroom, 0);
            Interlocked.Exchange(ref _exitEmpty, 0);
            Interlocked.Exchange(ref _exitBudget, 0);
            Interlocked.Exchange(ref _exitOverrun, 0);
            Interlocked.Exchange(ref _exitCap, 0);
            Interlocked.Exchange(ref _exitDone, 0);
            for (int i = 0; i < OvershootBins.Length; i++)
                Interlocked.Exchange(ref OvershootBins[i], 0);
        }

        private static void MaybeLogBatchChildError(string name, Exception ex)
        {
            var now = Stopwatch.GetTimestamp();
            if (now < Volatile.Read(ref _nextBatchChildErrorLogTs))
                return;

            MapPerfLog.Error($"[slice] {name} child failed", ex);
            Volatile.Write(ref _nextBatchChildErrorLogTs,
                now + (long)(Stopwatch.Frequency * BatchChildErrorCooldownSeconds));
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

        private static List<Action> DiscoverSubscriberActions(object dispatcher, MethodBase hub, string fallbackName)
        {
            var ret = new List<Action>(256);
            var t = dispatcher?.GetType() ?? hub?.DeclaringType;
            if (t == null) return ret;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            var nameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddName(string candidate)
            {
                if (string.IsNullOrEmpty(candidate)) return;
                if (!nameSet.Add(candidate)) return;
                var alias = SubModule.GetMethodAlias(candidate);
                if (!string.IsNullOrEmpty(alias)) nameSet.Add(alias);
            }

            AddName(hub?.Name);
            AddName(fallbackName);
            MethodInfo FindHandler(Type targetType)
            {
                if (targetType == null || nameSet.Count == 0) return null;
                foreach (var name in nameSet)
                {
                    var mi = SubModule.GetZeroParamMethod(targetType, name, F);
                    if (mi != null && !mi.IsAbstract) return mi;
                }
                return null;
            }

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
                            if (one.Method.IsStatic)
                            {
                                ret.Add((Action)one.Method.CreateDelegate(typeof(Action)));
                            }
                            else
                            {
                                ret.Add((Action)one.Method.CreateDelegate(typeof(Action), one.Target));
                            }
                        }
                        catch
                        {
                            var o = one;
                            ret.Add(() => o.DynamicInvoke(Array.Empty<object>()));
                        }
                    }
                    return;
                }

                var mi = FindHandler(target.GetType());
                if (mi != null && !mi.IsAbstract)
                {
                    try
                    {
                        if (mi.IsStatic)
                        {
                            ret.Add((Action)mi.CreateDelegate(typeof(Action)));
                        }
                        else
                        {
                            ret.Add((Action)mi.CreateDelegate(typeof(Action), target));
                        }
                    }
                    catch
                    {
                        ret.Add(() => mi.Invoke(mi.IsStatic ? null : target, null));
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
                                 || n.IndexOf("weekly", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("quarter", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("tick", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("subscriber", StringComparison.OrdinalIgnoreCase) >= 0);

            foreach (var f in t.GetFields(F))
            {
                if (!NameLooksRelevant(f.Name)) continue;
                var target = f.IsStatic ? null : dispatcher;
                var val = f.GetValue(target);
                TryExpand(val, AddIfInvokable);
            }

            foreach (var p in t.GetProperties(F))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                if (!NameLooksRelevant(p.Name)) continue;
                object val = null;
                var getter = p.GetGetMethod(true);
                var target = getter != null && getter.IsStatic ? null : dispatcher;
                try { val = p.GetValue(target, null); }
                catch { }
                TryExpand(val, AddIfInvokable);
            }

            if (ret.Count == 0)
            {
                foreach (var f in t.GetFields(F))
                {
                    var target = f.IsStatic ? null : dispatcher;
                    TryExpand(f.GetValue(target), AddIfInvokable);
                }
            }

            return ret;
        }

        private static List<Action> DiscoverSubscriberActions(Type hubType, string methodName)
        {
            var ret = new List<Action>(256);
            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            bool NameIsTarget(string n) => SubModule.MatchesMethodAlias(n, methodName);

            bool NameLooksRelevant(string n)
                => n != null && (n.IndexOf("daily", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("hourly", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("weekly", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("quarter", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("tick", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("subscriber", StringComparison.OrdinalIgnoreCase) >= 0);

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
                            if (one.Method.IsStatic)
                            {
                                ret.Add((Action)one.Method.CreateDelegate(typeof(Action)));
                            }
                            else
                            {
                                ret.Add((Action)one.Method.CreateDelegate(typeof(Action), one.Target));
                            }
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
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi != null && !mi.IsAbstract)
                {
                    try
                    {
                        ret.Add(mi.IsStatic
                            ? (Action)mi.CreateDelegate(typeof(Action))
                            : (Action)mi.CreateDelegate(typeof(Action), target));
                    }
                    catch
                    {
                        ret.Add(() => mi.Invoke(mi.IsStatic ? null : target, null));
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

            PropertyInfo[] props;
            try { props = hubType.GetProperties(F); }
            catch { props = Array.Empty<PropertyInfo>(); }

            foreach (var p in props)
            {
                if (p.GetIndexParameters().Length != 0) continue;
                if (!NameLooksRelevant(p.Name)) continue;
                object val = null;
                try { val = p.GetValue(null, null); }
                catch { }
                TryExpand(val, AddIfInvokable);
            }

            return ret;
        }

        public static bool ShouldBypass(MethodBase m)
            => m != null && _bypass.TryGetValue(m, out var c) && c > 0;

        internal static void EnterBypass(MethodBase m)
        {
            if (m == null) return;
            _bypass.AddOrUpdate(m, 1, (_, v) => v + 1);
        }

        internal static void ExitBypass(MethodBase m)
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
