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
                // Settings may be unavailable during early module startup; use safe defaults.
            }

            return fallback;
        }

        internal static bool Enabled => Get(s => s.Enabled, true);
        internal static bool DebugLogging => Get(s => s.DebugLogging, false);
        internal static bool TuneGcLatency => Get(s => s.TuneGcLatency, true);
        internal static bool OptimizePausedOffscreenVisuals =>
            Get(s => s.OptimizePausedOffscreenVisuals, true);

        internal static int PausedVisualTickCadence =>
            Clamp(Get(s => s.PausedVisualTickCadence, 4), 2, 12);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);
    }
}
