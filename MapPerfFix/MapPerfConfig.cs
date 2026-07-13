using System;
using System.Runtime.CompilerServices;

namespace MapPerfProbe
{
    internal static class MapPerfConfig
    {
        private static T Get<T>(Func<MapPerfSettings, T> selector, T fallback)
        {
            try
            {
                var instance = MapPerfSettings.Instance;
                if (instance != null)
                    return selector(instance);
            }
            catch
            {
            }

            return fallback;
        }

        internal static bool Enabled => Get(s => s.Enabled, true);
        internal static bool DebugLogging => Get(s => s.DebugLogging, true);
        internal static bool OptimizeHiddenPartyVisuals =>
            Get(s => s.OptimizeHiddenPartyVisuals, true);
        internal static bool TuneGcLatency => Get(s => s.TuneGcLatency, true);
        internal static bool ProfileTorCampaignCallbacks =>
            Get(s => s.ProfileTorCampaignCallbacks, true);
        internal static int SlowCallbackThresholdMs =>
            Clamp(Get(s => s.SlowCallbackThresholdMs, 8), 1, 100);
        internal static int ProfilerReportIntervalSeconds =>
            Clamp(Get(s => s.ProfilerReportIntervalSeconds, 30), 5, 120);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Clamp(int value, int minimum, int maximum) =>
            value < minimum ? minimum : (value > maximum ? maximum : value);
    }
}
