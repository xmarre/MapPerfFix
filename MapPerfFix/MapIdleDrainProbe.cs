using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;        // MobileParty.All
using TaleWorlds.CampaignSystem.Settlements;  // Settlement.All

namespace MapPerfProbe
{
    internal static class MapIdleDrainProbe
    {
        // Public snapshot API for mitigator
        internal struct Snapshot
        {
            public long Frames;
            public double AvgMapMs;
            public double MaxMapMs;
            public double AvgUiMs;
            public double MaxUiMs;
            public double AvgParties;
            public double AvgArmies;
            public double AvgSettlements;
            public double AvgTracks;
            public int G0;
            public int G1;
            public int G2;
            public long WsMb;
            public long WsDeltaMb;
            public string Mode;
        }

        private static Snapshot _lastSnap;
        private static int _snapValid;
        private const double ReportIntervalSeconds = 3.0;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

        private sealed class Metric
        {
            private const int Cap = 8192;
            private readonly double[] _buf = new double[Cap];
            private int _n;
            public long Count;
            public double Sum;
            public double Max;
            public int SampleCount => _n;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(double v)
            {
                if (v < 0) return;
                Sum += v;
                Count++;
                if (v > Max) Max = v;
                if (_n < Cap) _buf[_n++] = v;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void RecordMax(double v)
            {
                if (v < 0) return;
                Count++;
                if (v > Max) Max = v;
                if (_n < Cap) _buf[_n++] = v;
            }

            public double Avg => Count > 0 ? Sum / Count : 0.0;

            public double P95()
            {
                var n = _n;
                if (n == 0) return 0.0;
                Array.Sort(_buf, 0, n);
                var idx = (int)Math.Round(0.95 * (n - 1));
                if (idx < 0) idx = 0;
                if (idx >= n) idx = n - 1;
                return _buf[idx];
            }

            public int CountAbove(double threshold)
            {
                var n = _n;
                if (n == 0) return 0;
                var count = 0;
                for (var i = 0; i < n; i++)
                {
                    if (_buf[i] >= threshold)
                        count++;
                }
                return count;
            }

            public void Reset()
            {
                Count = 0;
                Sum = 0;
                Max = 0;
                _n = 0;
            }
        }

        private static bool _installed;
        private static long _mapFrameStart;
        private static long _uiStart;
        private static long _msStart;
        private static long _campStart;
        private static long _glStart;
        private static long _frames;
        private static long _uiFrames;
        private static double _windowStartSec;
        private static readonly Metric _mMap = new Metric();
        private static readonly Metric _mUI = new Metric();
        private static readonly Metric _mMS = new Metric();
        private static readonly Metric _mCamp = new Metric();
        private static readonly Metric _mGL = new Metric(); // Gauntlet tracks per-window peak; Avg is not used
        private static double _maxMapFrameMs;
        private static double _maxUiMs;
        private static long _samples;
        private static long _sumParties;
        private static long _sumArmies;
        private static long _sumSettlements;
        private static long _sumTracks;
        private static double _nextReportSec;
        private static int _lastGc0;
        private static int _lastGc1;
        private static int _lastGc2;
        private static long _lastWorkingSetMb;
        private static bool _msTickSeenThisFrame;

        private static readonly ConcurrentDictionary<Type, Func<object, int>> CountResolvers =
            new ConcurrentDictionary<Type, Func<object, int>>();

        private static PropertyInfo _campaignBehaviorManagerProp;
        private static PropertyInfo _behaviorsProp;
        private static bool _trackGetterScanned;
        private static Func<object, int> _trackCountGetter;

        internal static void Install(object harmony)
        {
            if (_installed || harmony == null) return;

            try
            {
                var mapType = ResolveMapScreenType();
                var mapStateType = Type.GetType("TaleWorlds.CampaignSystem.GameState.MapState, TaleWorlds.CampaignSystem", false)
                                   ?? Type.GetType("TaleWorlds.CampaignSystem.MapState, TaleWorlds.CampaignSystem", false);
                var uiType = ResolveUiContextType();
                var campaignType = Type.GetType("TaleWorlds.CampaignSystem.Campaign, TaleWorlds.CampaignSystem", false);
                var gauntletLayerType = Type.GetType("TaleWorlds.GauntletUI.GauntletLayer, TaleWorlds.GauntletUI", false);
                var prefix = AccessTools.Method(typeof(MapIdleDrainProbe), nameof(MapFramePrefix));
                var postfix = AccessTools.Method(typeof(MapIdleDrainProbe), nameof(MapFramePostfix));
                var uiPrefix = AccessTools.Method(typeof(MapIdleDrainProbe), nameof(UiUpdatePrefix));
                var uiPostfix = AccessTools.Method(typeof(MapIdleDrainProbe), nameof(UiUpdatePostfix));
                var msPre = AccessTools.Method(typeof(MapIdleDrainProbe), nameof(MapStateTickPrefix));
                var msPost = AccessTools.Method(typeof(MapIdleDrainProbe), nameof(MapStateTickPostfix));
                var campPre = AccessTools.Method(typeof(MapIdleDrainProbe), nameof(CampaignRealTickPrefix));
                var campPost = AccessTools.Method(typeof(MapIdleDrainProbe), nameof(CampaignRealTickPostfix));
                var glPre = AccessTools.Method(typeof(MapIdleDrainProbe), nameof(GauntletLayerLateUpdatePrefix));
                var glPost = AccessTools.Method(typeof(MapIdleDrainProbe), nameof(GauntletLayerLateUpdatePostfix));

                var ht = harmony.GetType();
                var harmonyAsm = ht.Assembly;
                var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                            ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false)
                            ?? typeof(HarmonyMethod);
                var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
                var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });

                if (hmCtor == null || patchMi == null)
                    return;

                if (mapType != null && prefix != null && postfix != null)
                {
                    var onFrame = mapType.GetMethod("OnFrameTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (onFrame != null)
                    {
                        var preHm = hmCtor.Invoke(new object[] { prefix });
                        var postHm = hmCtor.Invoke(new object[] { postfix });
                        try { patchMi.Invoke(harmony, new object[] { onFrame, preHm, postHm, null, null }); }
                        catch (Exception ex) { MapPerfLog.Error("[idle-probe] patch MapScreen.OnFrameTick failed", ex); }
                    }
                    else
                    {
                        MapPerfLog.Warn("[idle-probe] MapScreen.OnFrameTick not found; idle drain probe inactive.");
                    }
                }

                if (mapStateType != null && msPre != null && msPost != null)
                {
                    var msOnMapMode = mapStateType.GetMethod("OnMapModeTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var msOnTick = mapStateType.GetMethod("OnTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (msOnMapMode != null)
                    {
                        var preHm = hmCtor.Invoke(new object[] { msPre });
                        var postHm = hmCtor.Invoke(new object[] { msPost });
                        try { patchMi.Invoke(harmony, new object[] { msOnMapMode, preHm, postHm, null, null }); }
                        catch (Exception ex) { MapPerfLog.Error("[idle-probe] patch MapState.OnMapModeTick failed", ex); }
                    }
                    if (msOnTick != null)
                    {
                        var preHm = hmCtor.Invoke(new object[] { msPre });
                        var postHm = hmCtor.Invoke(new object[] { msPost });
                        try { patchMi.Invoke(harmony, new object[] { msOnTick, preHm, postHm, null, null }); }
                        catch (Exception ex) { MapPerfLog.Error("[idle-probe] patch MapState.OnTick failed", ex); }
                    }
                }

                if (uiType != null && uiPrefix != null && uiPostfix != null)
                {
                    var update = uiType.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (update != null)
                    {
                        var preHm = hmCtor.Invoke(new object[] { uiPrefix });
                        var postHm = hmCtor.Invoke(new object[] { uiPostfix });
                        try { patchMi.Invoke(harmony, new object[] { update, preHm, postHm, null, null }); }
                        catch (Exception ex) { MapPerfLog.Error("[idle-probe] patch UIContext.Update failed", ex); }
                    }
                    else
                    {
                        MapPerfLog.Warn("[idle-probe] UIContext.Update not found; idle drain probe UI timing disabled.");
                    }
                }

                if (campaignType != null && campPre != null && campPost != null)
                {
                    var realTick = campaignType.GetMethod("RealTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (realTick != null)
                    {
                        var preHm = hmCtor.Invoke(new object[] { campPre });
                        var postHm = hmCtor.Invoke(new object[] { campPost });
                        try { patchMi.Invoke(harmony, new object[] { realTick, preHm, postHm, null, null }); }
                        catch (Exception ex) { MapPerfLog.Error("[idle-probe] patch Campaign.RealTick failed", ex); }
                    }
                }

                if (gauntletLayerType != null && glPre != null && glPost != null)
                {
                    var lateUpdate = gauntletLayerType.GetMethod("OnLateUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                   ?? gauntletLayerType.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                   ?? gauntletLayerType.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (lateUpdate != null)
                    {
                        var preHm = hmCtor.Invoke(new object[] { glPre });
                        var postHm = hmCtor.Invoke(new object[] { glPost });
                        try { patchMi.Invoke(harmony, new object[] { lateUpdate, preHm, postHm, null, null }); }
                        catch (Exception ex) { MapPerfLog.Error("[idle-probe] patch GauntletLayer.* failed", ex); }
                    }
                }

                ResetWindow();
                _lastGc0 = GC.CollectionCount(0);
                _lastGc1 = GC.CollectionCount(1);
                _lastGc2 = GC.CollectionCount(2);
                _lastWorkingSetMb = ReadWorkingSetMb();
                _installed = true;
                MapPerfLog.Info("[idle-probe] Map idle drain sampler installed.");
            }
            catch (Exception ex)
            {
                MapPerfLog.Error("[idle-probe] install failed", ex);
            }
        }

        private static void ResetWindow()
        {
            _frames = 0;
            _uiFrames = 0;
            _windowStartSec = Stopwatch.GetTimestamp() * TicksToSeconds;
            _mMap.Reset();
            _mUI.Reset();
            _mMS.Reset();
            _mCamp.Reset();
            _mGL.Reset();
            _maxMapFrameMs = 0.0;
            _maxUiMs = 0.0;
            _samples = 0;
            _sumParties = 0;
            _sumArmies = 0;
            _sumSettlements = 0;
            _sumTracks = 0;
            _nextReportSec = 0.0;
            _msTickSeenThisFrame = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldSample()
        {
            if (!MapPerfConfig.Enabled) return false;
            if (!SubModule.IsOnMap()) return false;
            return IsPaused();
        }

        private static bool IsPaused()
        {
            try
            {
                var campaign = Campaign.Current;
                if (campaign == null) return false;
                return campaign.TimeControlMode == CampaignTimeControlMode.Stop;
            }
            catch
            {
                return false;
            }
        }

        private static void MapFramePrefix(object __instance)
        {
            _msTickSeenThisFrame = false;
            if (!ShouldSample())
            {
                _mapFrameStart = 0;
                return;
            }

            _mapFrameStart = Stopwatch.GetTimestamp();
        }

        private static void MapFramePostfix(object __instance)
        {
            var start = _mapFrameStart;
            if (start == 0) return;
            _mapFrameStart = 0;

            var ticks = Stopwatch.GetTimestamp() - start;
            var dtMs = ticks * TicksToMs;
            _mMap.Add(dtMs);
            if (dtMs > _maxMapFrameMs) _maxMapFrameMs = dtMs;
            _frames++;

            SampleWorldState(__instance);
            MaybeReport();
        }

        private static void UiUpdatePrefix(object __instance)
        {
            if (!ShouldSample())
            {
                _uiStart = 0;
                return;
            }

            _uiStart = Stopwatch.GetTimestamp();
        }

        private static void UiUpdatePostfix(object __instance)
        {
            var start = _uiStart;
            if (start == 0) return;
            _uiStart = 0;

            var dtMs = (Stopwatch.GetTimestamp() - start) * TicksToMs;
            _mUI.Add(dtMs);
            if (dtMs > _maxUiMs) _maxUiMs = dtMs;
            _uiFrames++;
            // also sample world state during UI-only windows so world counts are non-zero
            SampleWorldState(null);
            MaybeReport();
        }

        private static void MapStateTickPrefix(object __instance)
        {
            if (!ShouldSample() || _msTickSeenThisFrame)
            {
                _msStart = 0;
                return;
            }

            _msStart = Stopwatch.GetTimestamp();
        }

        private static void MapStateTickPostfix(object __instance)
        {
            var start = _msStart;
            if (start == 0) return;
            _msStart = 0;

            var dtMs = (Stopwatch.GetTimestamp() - start) * TicksToMs;
            _mMS.Add(dtMs);
            _msTickSeenThisFrame = true;
        }

        private static void CampaignRealTickPrefix(object __instance)
        {
            if (!ShouldSample())
            {
                _campStart = 0;
                return;
            }

            _campStart = Stopwatch.GetTimestamp();
        }

        private static void CampaignRealTickPostfix(object __instance)
        {
            var start = _campStart;
            if (start == 0) return;
            _campStart = 0;

            var dtMs = (Stopwatch.GetTimestamp() - start) * TicksToMs;
            _mCamp.Add(dtMs);
        }

        private static void GauntletLayerLateUpdatePrefix(object __instance)
        {
            if (!ShouldSample())
            {
                _glStart = 0;
                return;
            }

            _glStart = Stopwatch.GetTimestamp();
        }

        private static void GauntletLayerLateUpdatePostfix(object __instance)
        {
            var start = _glStart;
            if (start == 0) return;
            _glStart = 0;

            var dtMs = (Stopwatch.GetTimestamp() - start) * TicksToMs;
            _mGL.RecordMax(dtMs);
        }

        private static void SampleWorldState(object mapScreen)
        {
            try
            {
                var campaign = Campaign.Current;
                if (campaign == null) return;

                _samples++;

                _sumParties += SafeCount(MobileParty.All);
                _sumArmies += CountArmies();
                _sumSettlements += SafeCount(Settlement.All);

                var tracks = EstimateTrackCount(campaign);
                if (tracks > 0)
                    _sumTracks += tracks;
            }
            catch
            {
                // ignore – diagnostics only
            }
        }

        private static int CountArmies()
        {
            try
            {
                var kingdoms = Kingdom.All;
                if (kingdoms == null) return 0;

                var total = 0;
                foreach (var kingdom in kingdoms)
                {
                    if (kingdom == null) continue;
                    total += SafeCount(kingdom.Armies);
                }

                return total;
            }
            catch
            {
                return 0;
            }
        }

        private static void MaybeReport()
        {
            if (_frames == 0 && _uiFrames == 0) return;

            var nowSec = Stopwatch.GetTimestamp() * TicksToSeconds;
            if (_nextReportSec == 0.0) _nextReportSec = nowSec + ReportIntervalSeconds;
            if (nowSec < _nextReportSec) return;
            _nextReportSec = nowSec + ReportIntervalSeconds;

            var windowSec = Math.Max(1e-6, nowSec - _windowStartSec);
            var mapFrames = _frames;
            var uiCalls = _uiFrames;
            var hasMapFrames = mapFrames > 0;

            // FPS derives solely from real map frames.
            var fps = hasMapFrames ? mapFrames / windowSec : 0.0;
            var fpsStr = hasMapFrames ? $"{fps:F1}" : "—";
            // Require enough window time and map frames before classifying low FPS to avoid UI-only false positives.
            var canJudgeFps = hasMapFrames && windowSec >= 2.0 && mapFrames >= Math.Max(30, (int)(20 * windowSec));
            var avgMapMs = _mMap.Avg;
            var avgUiMs = _mUI.Avg;
            var avgMsTick = _mMS.Avg;
            var avgCamp = _mCamp.Avg;
            var p95Map = _mMap.P95();
            var p95Ui = _mUI.P95();
            var p95Ms = _mMS.P95();
            var p95Camp = _mCamp.P95();
            var p95Gl = _mGL.P95();
            var totalMapSamples = Math.Max(1, _mMap.SampleCount);
            var avgParties = _samples > 0 ? (double)_sumParties / _samples : 0.0;
            var avgArmies = _samples > 0 ? (double)_sumArmies / _samples : 0.0;
            var avgSettlements = _samples > 0 ? (double)_sumSettlements / _samples : 0.0;
            var avgTracks = _samples > 0 ? (double)_sumTracks / _samples : 0.0;

            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var gc2 = GC.CollectionCount(2);
            var d0 = gc0 - _lastGc0;
            var d1 = gc1 - _lastGc1;
            var d2 = gc2 - _lastGc2;
            _lastGc0 = gc0;
            _lastGc1 = gc1;
            _lastGc2 = gc2;

            var ws = ReadWorkingSetMb();
            var deltaWs = ws - _lastWorkingSetMb;
            _lastWorkingSetMb = ws;

            var mode = Campaign.Current?.TimeControlMode.ToString() ?? "<none>";

            PublishSnapshot(mapFrames, avgMapMs, _maxMapFrameMs, avgUiMs, _maxUiMs,
                avgParties, avgArmies, avgSettlements, avgTracks, d0, d1, d2, ws, deltaWs, mode);

            double cpuBudgetMs = 0.0;
            double longPct = 0.0;
            double veryLongPct = 0.0;
            if (hasMapFrames)
            {
                cpuBudgetMs = 1000.0 / Math.Max(1.0, fps);
                longPct = _mMap.CountAbove(1.25 * cpuBudgetMs) * 100.0 / totalMapSamples;
                veryLongPct = _mMap.CountAbove(2.0 * cpuBudgetMs) * 100.0 / totalMapSamples;
            }

            var frameDist = hasMapFrames
                ? FormattableString.Invariant($" long_frame%={longPct:F1} very_long%={veryLongPct:F1}")
                : string.Empty;

            MapPerfLog.Info(FormattableString.Invariant(
                $"[idle-probe] mode={mode} fps={fpsStr} frames={mapFrames} ui_calls={uiCalls} avg_map_ms={avgMapMs:F2} p95_map_ms={p95Map:F2} max_map_ms={_maxMapFrameMs:F2}{frameDist} avg_ui_ms={avgUiMs:F2} p95_ui_ms={p95Ui:F2} | p95_ms={p95Ms:F2} p95_camp={p95Camp:F2} p95_gl={p95Gl:F2} | world≈ parties={avgParties:F0} armies={avgArmies:F0} settlements={avgSettlements:F0} tracks={avgTracks:F0} | GCΔ G0={d0} G1={d1} G2={d2} | WS={ws}MB (Δ{deltaWs}MB)"));

            if (!hasMapFrames)
            {
                ResetWindow();
                return;
            }

            var managedLoadMs = avgMapMs + avgUiMs + avgMsTick + avgCamp + p95Gl;
            string DetermineCause()
            {
                if (p95Map > 8.0 || p95Ms > 8.0) return "MapState/MapScreen";
                if (p95Camp > 6.0) return "Campaign.RealTick";
                if (p95Gl > 6.0 || p95Ui > 4.0) return "UI/Gauntlet";
                if (managedLoadMs < 0.5 * cpuBudgetMs) return "engine/render (outside managed)";
                return "mixed";
            }

            if (canJudgeFps && fps < 55.0)
            {
                var cause = DetermineCause();
                if (d1 > 0 || d2 > 0)
                    cause += " + GC";
                MapPerfLog.Warn(FormattableString.Invariant(
                    $"[idle-probe][low-fps] fps={fps:F1} frames={mapFrames} ui_calls={uiCalls} budget_ms≈{cpuBudgetMs:F1} managed_load_ms≈{managedLoadMs:F1} cause={cause} p95 map={p95Map:F1} ms={p95Ms:F1} camp={p95Camp:F1} ui={p95Ui:F1} gl={p95Gl:F1} | GCΔ G0={d0} G1={d1} G2={d2} WSΔ={deltaWs}MB"));
            }

            if (mapFrames > 0 && fps >= 55.0 && (p95Map > 2.0 || _maxMapFrameMs > 3.0 * cpuBudgetMs))
            {
                var cause = DetermineCause();
                if (d1 > 0 || d2 > 0)
                    cause += " + GC";
                MapPerfLog.Warn(FormattableString.Invariant(
                    $"[idle-probe][stutter] fps={fps:F1} frames={mapFrames} ui_calls={uiCalls} budget_ms≈{cpuBudgetMs:F1} max_map_ms={_maxMapFrameMs:F1} cause={cause} p95 map={p95Map:F1} ms={p95Ms:F1} camp={p95Camp:F1} ui={p95Ui:F1} gl={p95Gl:F1} | GCΔ G0={d0} G1={d1} G2={d2} WSΔ={deltaWs}MB"));
            }

            ResetWindow();
        }

        internal static bool TryGetSnapshot(out Snapshot s)
        {
            if (System.Threading.Volatile.Read(ref _snapValid) == 1)
            {
                s = _lastSnap;
                return true;
            }

            s = default;
            return false;
        }

        private static void PublishSnapshot(long frameCount, double avgMapMs, double maxMapMs, double avgUiMs, double maxUiMs,
            double avgParties, double avgArmies, double avgSettlements, double avgTracks, int d0, int d1, int d2, long ws,
            long deltaWs, string mode)
        {
            var snap = new Snapshot
            {
                Frames = frameCount,
                AvgMapMs = avgMapMs,
                MaxMapMs = maxMapMs,
                AvgUiMs = avgUiMs,
                MaxUiMs = maxUiMs,
                AvgParties = avgParties,
                AvgArmies = avgArmies,
                AvgSettlements = avgSettlements,
                AvgTracks = avgTracks,
                G0 = d0,
                G1 = d1,
                G2 = d2,
                WsMb = ws,
                WsDeltaMb = deltaWs,
                Mode = mode
            };

            _lastSnap = snap;
            // Ensure consumers observe the populated snapshot before the validity flag flips.
            System.Threading.Volatile.Write(ref _snapValid, 1);
        }

        private static long ReadWorkingSetMb()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    return process.WorkingSet64 / (1024 * 1024);
                }
            }
            catch
            {
                return 0;
            }
        }

        private static int SafeCount(object value)
        {
            if (value == null) return 0;
            try
            {
                if (value is Array arr) return arr.Length;
                if (value is ICollection coll) return coll.Count;

                var type = value.GetType();
                var resolver = CountResolvers.GetOrAdd(type, BuildCountResolver);
                try { return resolver(value); }
                catch { return 0; }
            }
            catch
            {
                return 0;
            }
        }

        private static Func<object, int> BuildCountResolver(Type type)
        {
            if (type == null) return _ => 0;
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo countProp = null;
            try { countProp = type.GetProperty("Count", Flags); }
            catch { }
            if (countProp != null && countProp.GetIndexParameters().Length == 0)
                return obj => ConvertCount(() => countProp.GetValue(obj, null));

            PropertyInfo lengthProp = null;
            try { lengthProp = type.GetProperty("Length", Flags); }
            catch { }
            if (lengthProp != null && lengthProp.GetIndexParameters().Length == 0)
                return obj => ConvertCount(() => lengthProp.GetValue(obj, null));

            FieldInfo countField = null;
            try { countField = type.GetField("Count", Flags); }
            catch { }
            if (countField != null)
                return obj => ConvertCount(() => countField.GetValue(obj));

            FieldInfo lengthField = null;
            try { lengthField = type.GetField("Length", Flags); }
            catch { }
            if (lengthField != null)
                return obj => ConvertCount(() => lengthField.GetValue(obj));

            return _ => 0;
        }

        private static int ConvertCount(Func<object> getter)
        {
            try
            {
                var raw = getter();
                if (raw == null) return 0;
                switch (raw)
                {
                    case int i:
                        return i >= 0 ? i : 0;
                    case long l:
                        if (l <= 0) return 0;
                        return l > int.MaxValue ? int.MaxValue : (int)l;
                    case uint ui:
                        return ui > int.MaxValue ? int.MaxValue : (int)ui;
                    case ulong ul:
                        if (ul == 0) return 0;
                        return ul > (ulong)int.MaxValue ? int.MaxValue : (int)ul;
                }
                if (raw is ICollection coll) return coll.Count;
                if (raw is Array arr) return arr.Length;
                if (int.TryParse(raw.ToString(), out var parsed))
                    return parsed >= 0 ? parsed : 0;
            }
            catch
            {
                // ignore
            }
            return 0;
        }

        private static int EstimateTrackCount(Campaign campaign)
        {
            try
            {
                if (campaign == null) return 0;
                EnsureBehaviorAccessors(campaign);
                if (_behaviorsProp == null) return 0;
                var manager = _campaignBehaviorManagerProp?.GetValue(campaign);
                if (manager == null) return 0;

                if (!_trackGetterScanned)
                {
                    _trackGetterScanned = true;
                    _trackCountGetter = BuildTrackGetter(manager);
                }

                if (_trackCountGetter == null) return 0;
                return _trackCountGetter(manager);
            }
            catch
            {
                return 0;
            }
        }

        private static void EnsureBehaviorAccessors(Campaign campaign)
        {
            if (_campaignBehaviorManagerProp != null && _behaviorsProp != null) return;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try { _campaignBehaviorManagerProp = campaign.GetType().GetProperty("CampaignBehaviorManager", flags); }
            catch { }

            var managerType = Type.GetType("TaleWorlds.CampaignSystem.CampaignBehaviorManager, TaleWorlds.CampaignSystem", false);
            if (managerType == null) return;
            try { _behaviorsProp = managerType.GetProperty("Behaviors", flags); }
            catch { }
        }

        private static Func<object, int> BuildTrackGetter(object manager)
        {
            if (manager == null || _behaviorsProp == null) return null;

            try
            {
                var behaviors = _behaviorsProp.GetValue(manager) as IEnumerable;
                if (behaviors == null) return null;

                object trackBehavior = null;
                FieldInfo trackField = null;

                foreach (var behavior in behaviors)
                {
                    if (behavior == null) continue;
                    var type = behavior.GetType();
                    var name = type.FullName ?? string.Empty;
                    if (name.IndexOf("MapTrack", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        var field = fields[i];
                        if (!typeof(IEnumerable).IsAssignableFrom(field.FieldType)) continue;
                        if (field.Name.IndexOf("track", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        trackBehavior = behavior;
                        trackField = field;
                        break;
                    }
                    if (trackField != null) break;
                }

                if (trackField == null || trackBehavior == null) return null;
                var behaviorType = trackBehavior.GetType();
                var weak = new WeakReference(trackBehavior);

                return mgr =>
                {
                    try
                    {
                        var list = _behaviorsProp.GetValue(mgr) as IEnumerable;
                        if (list == null) return 0;

                        var target = weak.Target;
                        if (target == null || !behaviorType.IsInstanceOfType(target))
                        {
                            target = null;
                            foreach (var behavior in list)
                            {
                                if (behavior == null) continue;
                                if (!behaviorType.IsInstanceOfType(behavior)) continue;
                                target = behavior;
                                weak.Target = behavior;
                                break;
                            }
                        }

                        if (target == null) return 0;

                        var items = trackField.GetValue(target) as IEnumerable;
                        return SafeCount(items);
                    }
                    catch
                    {
                        return 0;
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private static Type ResolveMapScreenType()
        {
            return Type.GetType("SandBox.View.Map.MapScreen, SandBox.View", false)
                   ?? Type.GetType("SandBox.View.Map.MapScreen, SandBox", false)
                   ?? Type.GetType("SandBox.View.Map.MapScreen", false);
        }

        private static Type ResolveUiContextType()
        {
            return Type.GetType("TaleWorlds.GauntletUI.UIContext, TaleWorlds.GauntletUI", false)
                   ?? Type.GetType("TaleWorlds.GauntletUI.UIContext", false);
        }
    }
}
