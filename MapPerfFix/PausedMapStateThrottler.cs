using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace MapPerfProbe
{
    internal static class PausedMapStateThrottler
    {
        private const string HarmonyId = "MapPerfProbe.paused-mapstate-throttle";
        private static Harmony _harmony;
        private static long _lastTicks;
        private static long _lastLogTicks;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        internal static void ResetGate()
        {
            _lastTicks = 0;
            _lastLogTicks = 0;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            try { _harmony = new Harmony(HarmonyId); }
            catch (Exception ex) { MapPerfLog.Warn($"PausedMapStateThrottler Harmony init failed: {ex.Message}"); return; }
            TryPatch("TaleWorlds.CampaignSystem.GameState.MapState");
            TryPatch("TaleWorlds.CampaignSystem.MapState");
        }

        private static void TryPatch(string typeName)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) return;
                // OnMapModeTick(float)
                var m1 = AccessTools.Method(t, "OnMapModeTick", new[] { typeof(float) });
                if (IsPatchable(m1))
                    _harmony.Patch(m1, prefix: new HarmonyMethod(typeof(PausedMapStateThrottler), nameof(OnMapModeTick_Prefix)));
                // OnTick(float) — also runs while paused
                var m2 = AccessTools.Method(t, "OnTick", new[] { typeof(float) });
                if (IsPatchable(m2))
                    _harmony.Patch(m2, prefix: new HarmonyMethod(typeof(PausedMapStateThrottler), nameof(OnMapModeTick_Prefix)));
                // OnFrameTick(float) – throttle during pause too
                var m3 = AccessTools.Method(t, "OnFrameTick", new[] { typeof(float) });
                if (IsPatchable(m3))
                    _harmony.Patch(m3, prefix: new HarmonyMethod(typeof(PausedMapStateThrottler), nameof(OnMapModeTick_Prefix)));
            }
            catch (Exception ex)
            {
                MapPerfLog.Warn($"PausedMapStateThrottler patch {typeName} failed: {ex.Message}");
            }
        }

        private static bool IsPatchable(MethodInfo mi)
            => mi != null && !mi.IsAbstract && !mi.ContainsGenericParameters && !mi.IsSpecialName && mi.GetMethodBody() != null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPaused()
        {
            var c = Campaign.Current;
            return c != null && c.TimeControlMode == CampaignTimeControlMode.Stop;
        }

        // return false => skip original
        private static bool OnMapModeTick_Prefix()
        {
            if (!MapPerfConfig.ThrottlePausedMapState) return true;
            if (!InitGate.MapReady()) return true;
            if (!IsPaused()) return true;

            int period = MapPerfConfig.PausedMapStateMinIntervalMs;
            var now = Stopwatch.GetTimestamp();
            var ms = (now - _lastTicks) * TicksToMs;
            if (ms < period) return false;
            _lastTicks = now;

            // breadcrumb: log at most every ~3s when we *do* allow a tick
            if (MapPerfConfig.DebugLogging)
            {
                var logMs = (now - _lastLogTicks) * TicksToMs;
                if (logMs >= 3000)
                {
                    _lastLogTicks = now;
                    MapPerfLog.Info($"[paused-mapstate] allow tick (period={period}ms)");
                }
            }
            return true;
        }
    }
}
