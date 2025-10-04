using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace MapPerfProbe
{
    internal static class PauseSimSkipper
    {
        private const string HarmonyId = "MapPerfProbe.pause-sim-skipper";
        private static Harmony _harmony;
        private static long _lastCacheTicks;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        internal static void ResetCacheGate()
        {
            _lastCacheTicks = 0;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                _harmony = new Harmony(HarmonyId);
            }
            catch (Exception ex)
            {
                MapPerfLog.Warn($"PauseSimSkipper Harmony init failed: {ex.Message}");
                _harmony = null;
                return;
            }

            TryPatchCampaignRealTick();
            TryPatchCacheRealTick();
        }

        private static void TryPatchCampaignRealTick()
        {
            if (_harmony == null) return;
            try
            {
                var method = AccessTools.Method(typeof(Campaign), "RealTick", new[] { typeof(float) });
                if (!IsPatchable(method)) return;

                _harmony.Patch(method, prefix: new HarmonyMethod(typeof(PauseSimSkipper), nameof(Campaign_RealTick_Prefix)));
            }
            catch (Exception ex)
            {
                MapPerfLog.Warn($"PauseSimSkipper Campaign.RealTick patch failed: {ex.Message}");
            }
        }

        private static void TryPatchCacheRealTick()
        {
            if (_harmony == null) return;
            try
            {
                var cacheType = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignTickCacheDataStore");
                if (cacheType == null) return;

                var method = AccessTools.Method(cacheType, "RealTick", new[] { typeof(float) });
                if (!IsPatchable(method)) return;

                _harmony.Patch(method, prefix: new HarmonyMethod(typeof(PauseSimSkipper), nameof(Cache_RealTick_Prefix)));
            }
            catch (Exception ex)
            {
                MapPerfLog.Warn($"PauseSimSkipper Cache.RealTick patch failed: {ex.Message}");
            }
        }

        private static bool IsPatchable(MethodInfo method)
        {
            if (method == null) return false;
            if (method.IsAbstract) return false;
            if (method.ContainsGenericParameters) return false;
            if (method.IsSpecialName) return false;
            return method.GetMethodBody() != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPaused()
        {
            var campaign = Campaign.Current;
            return campaign != null && campaign.TimeControlMode == CampaignTimeControlMode.Stop;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Campaign_RealTick_Prefix()
            => !(MapPerfConfig.SkipCampaignRealTickWhenPaused && InitGate.MapReady() && IsPaused());

        private static bool Cache_RealTick_Prefix(ref float dt)
        {
            if (!InitGate.MapReady()) return true;
            if (!IsPaused()) return true;
            if (!MapPerfConfig.ThrottleCacheWhenPaused) return true;

            if (MapPerfConfig.SkipCacheRealTickWhenPaused)
                return false;

            var now = Stopwatch.GetTimestamp();
            var msSince = (now - _lastCacheTicks) * TicksToMs;
            if (msSince < MapPerfConfig.CachePauseMinIntervalMs)
                return false;

            _lastCacheTicks = now;
            return true;
        }
    }
}
