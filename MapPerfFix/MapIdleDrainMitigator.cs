using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.InputSystem;

namespace MapPerfProbe
{
    /// <summary>
    /// Runtime, self-validating mitigations for paused map drain.
    /// Activates only when MapIdleDrainProbe reports sustained cost while paused.
    /// </summary>
    [HarmonyPatch]
    internal static class MapIdleDrainMitigator
    {
        // Heuristics (kept internal; not exposed to MCM to keep UI stable)
        private const double UiAvgMsThreshold = 3.5;      // avg UI frame cost to consider throttling
        private const double MapAvgMsThreshold = 7.5;     // avg MapScreen cost to consider throttling
        private const int UiSkipMin = 2, UiSkipMax = 4;   // skip cadence when paused
        private const int MapSkipMin = 2, MapSkipMax = 6; // skip cadence when paused
        private const int TrackPurgeThreshold = 15000;    // if tracks exceed this while paused, purge
        private const double DecisionCooldownSec = 2.5;

        private static int _uiSkipSeq;
        private static int _mapSkipSeq;
        private static double _nextDecisionAt;
        private static int _uiSkipN, _mapSkipN;
        private static bool _uiActive, _mapActive;
        private static double NowSec() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        internal static void Install(object harmony)
        {
            if (harmony == null) return;
            var ht = harmony.GetType();
            var asm = ht.Assembly;
            var hmType = asm.GetType("HarmonyLib.HarmonyMethod") ?? typeof(HarmonyMethod);
            var hmCtor = hmType.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (hmCtor == null || patchMi == null) return;

            // UI throttle
            var uiType = Type.GetType("TaleWorlds.GauntletUI.UIContext, TaleWorlds.GauntletUI", false)
                        ?? Type.GetType("TaleWorlds.GauntletUI.UIContext", false);
            var uiPre = typeof(MapIdleDrainMitigator).GetMethod(nameof(UI_Update_Prefix), BindingFlags.Static | BindingFlags.NonPublic);
            if (uiType != null && uiPre != null)
            {
                var update = uiType.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (update != null)
                {
                    var preHM = hmCtor.Invoke(new object[] { uiPre });
                    try { patchMi.Invoke(harmony, new object[] { update, preHM, null, null, null }); }
                    catch { }
                }
            }

            // MapScreen throttle
            var mapType = Type.GetType("SandBox.View.Map.MapScreen, SandBox.View", false)
                       ?? Type.GetType("SandBox.View.Map.MapScreen, SandBox", false)
                       ?? Type.GetType("SandBox.View.Map.MapScreen", false);
            var mapPre = typeof(MapIdleDrainMitigator).GetMethod(nameof(Map_OnFrameTick_Prefix), BindingFlags.Static | BindingFlags.NonPublic);
            if (mapType != null && mapPre != null)
            {
                var onFrame = mapType.GetMethod("OnFrameTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (onFrame != null)
                {
                    var preHM = hmCtor.Invoke(new object[] { mapPre });
                    try { patchMi.Invoke(harmony, new object[] { onFrame, preHM, null, null, null }); }
                    catch { }
                }
            }
        }

        [HarmonyPriority(Priority.VeryHigh)]
        private static bool UI_Update_Prefix(object __instance)
        {
            if (!MapPerfConfig.Enabled) return true;
            if (!SubModule.IsOnMap() || !IsPaused()) return true;

            // Pull snapshot; if unavailable, do nothing.
            if (!MapIdleDrainProbe.TryGetSnapshot(out var snap)) return true;

            // Decide every DecisionCooldownSec. Keep decisions sticky to avoid oscillation.
            var now = NowSec();
            if (now >= _nextDecisionAt)
            {
                _nextDecisionAt = now + DecisionCooldownSec;
                _uiActive = snap.AvgUiMs >= UiAvgMsThreshold && !UserActive();
                _uiSkipN = Clamp(_uiActive ? MapToRange(snap.AvgUiMs, UiAvgMsThreshold, UiAvgMsThreshold * 2.5, UiSkipMin, UiSkipMax) : 0, 0, UiSkipMax);
            }

            // Optional safety: prune tracks if huge, once decisions are made.
            MaybePurgeTracks(snap);

            if (!_uiActive || _uiSkipN <= 0) return true;
            var token = ++_uiSkipSeq; if (token <= 0 || token > 1_000_000_000) _uiSkipSeq = token = 1;
            if ((token % _uiSkipN) != 0)
                return false; // skip UI update this frame
            return true;
        }

        [HarmonyPriority(Priority.VeryHigh)]
        private static bool Map_OnFrameTick_Prefix(object __instance)
        {
            if (!MapPerfConfig.Enabled) return true;
            if (!SubModule.IsOnMap() || !IsPaused()) return true;
            if (!MapIdleDrainProbe.TryGetSnapshot(out var snap)) return true;

            var now = NowSec();
            if (now >= _nextDecisionAt)
            {
                _nextDecisionAt = now + DecisionCooldownSec;
                _mapActive = snap.AvgMapMs >= MapAvgMsThreshold && !UserActive();
                _mapSkipN = Clamp(_mapActive ? MapToRange(snap.AvgMapMs, MapAvgMsThreshold, MapAvgMsThreshold * 3.0, MapSkipMin, MapSkipMax) : 0, 0, MapSkipMax);
            }

            MaybePurgeTracks(snap);

            if (!_mapActive || _mapSkipN <= 0) return true;
            var token = ++_mapSkipSeq; if (token <= 0 || token > 1_000_000_000) _mapSkipSeq = token = 1;
            if ((token % _mapSkipN) != 0)
                return false; // skip visual tick when paused and idle
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPaused()
        {
            try { return Campaign.Current?.TimeControlMode == CampaignTimeControlMode.Stop; }
            catch { return false; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool UserActive()
        {
            // Cheap check: any mouse button down or common nav keys pressed.
            try
            {
                if (Input.IsKeyDown(InputKey.LeftMouseButton) || Input.IsKeyDown(InputKey.RightMouseButton) || Input.IsKeyDown(InputKey.MiddleMouseButton))
                    return true;
                if (Input.IsKeyDown(InputKey.LeftShift) || Input.IsKeyDown(InputKey.RightShift)) return true;
                if (Input.IsKeyDown(InputKey.W) || Input.IsKeyDown(InputKey.A) || Input.IsKeyDown(InputKey.S) || Input.IsKeyDown(InputKey.D)) return true;
            }
            catch { }
            return false;
        }

        private static void MaybePurgeTracks(MapIdleDrainProbe.Snapshot snap)
        {
            if (snap.AvgTracks < TrackPurgeThreshold) return;
            try
            {
                var campaign = Campaign.Current; if (campaign == null) return;
                var managerProp = campaign.GetType().GetProperty("CampaignBehaviorManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var manager = managerProp?.GetValue(campaign);
                if (manager == null) return;
                var behaviorsProp = manager.GetType().GetProperty("Behaviors", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var list = behaviorsProp?.GetValue(manager) as IEnumerable; if (list == null) return;

                object target = null; FieldInfo trackField = null;
                foreach (var b in list)
                {
                    if (b == null) continue;
                    var t = b.GetType();
                    var name = t.FullName ?? string.Empty;
                    if (name.IndexOf("MapTrack", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (f == null) continue;
                        if (f.Name.IndexOf("track", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!typeof(IEnumerable).IsAssignableFrom(f.FieldType)) continue;
                        target = b; trackField = f; break;
                    }
                    if (trackField != null) break;
                }
                if (target == null || trackField == null) return;

                var col = trackField.GetValue(target);
                if (col == null) return;

                // Prefer Clear() if available. Otherwise try Trim to half if List<T>.
                var clear = col.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (clear != null)
                {
                    clear.Invoke(col, null);
                    MapPerfLog.Warn($"[idle-mitigator] purged map tracks via Clear() (avg {snap.AvgTracks:F0})");
                    return;
                }

                var countProp = col.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var removeRange = col.GetType().GetMethod("RemoveRange", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (countProp != null && removeRange != null)
                {
                    var count = Convert.ToInt32(countProp.GetValue(col, null));
                    if (count > 0)
                    {
                        var drop = count / 2;
                        // remove oldest half: start at 0
                        try { removeRange.Invoke(col, new object[] { 0, drop }); }
                        catch { /* ignore if unsupported */ }
                        MapPerfLog.Warn($"[idle-mitigator] trimmed map tracks by {drop} (avg {snap.AvgTracks:F0})");
                    }
                }
            }
            catch { /* best effort */ }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MapToRange(double x, double x1, double x2, int y1, int y2)
        {
            if (x <= x1) return y1;
            if (x >= x2) return y2;
            var t = (x - x1) / Math.Max(1e-6, (x2 - x1));
            return (int)Math.Round(y1 + (y2 - y1) * t);
        }
    }
}
