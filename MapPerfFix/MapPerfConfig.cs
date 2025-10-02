using System;

namespace MapPerfProbe
{
    internal static class MapPerfConfig
    {
        private static T Get<T>(Func<MapPerfSettings, T> selector, T fallback, bool allowEmptyString = false)
        {
            try
            {
                var instance = MapPerfSettings.Instance;
                if (instance != null)
                {
                    var value = selector(instance);
                    if (value is string str)
                    {
                        if (allowEmptyString || !string.IsNullOrEmpty(str))
                            return value;
                    }
                    else if (!object.ReferenceEquals(value, null))
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // MCM not present; fall through to fallback
            }

            return fallback;
        }

        internal static bool Enabled => Get(s => s.Enabled, true);
        internal static bool DebugLogging => Get(s => s.DebugLogging, false);
        internal static bool EnableMapThrottle => Get(s => s.EnableMapThrottle, true);
        internal static bool ThrottleOnlyInFastTime => Get(s => s.ThrottleOnlyInFastTime, true);
        internal static bool DesyncSimWhileThrottling => Get(s => s.DesyncSimWhileThrottling, true);
        internal static int SimTickEveryNSkipped => Get(s => s.SimTickEveryNSkipped, 8);
        internal static int MaxDesyncMs => Get(s => s.MaxDesyncMs, 1000);
        internal static int DesyncLowWatermarkMs => Get(s => s.DesyncLowWatermarkMs, 400);
        internal static ThrottlePreset Preset => Get(s => s.Preset, ThrottlePreset.Balanced);
        internal static int PeriodicQueueHardCap => Get(s => s.PeriodicQueueHardCap, 150);

        // Fixed internals (kept sane; not exposed)
        internal static double BudgetAlpha => 0.05;
        internal static double BudgetHeadroom => 1.20;
        internal static double BudgetMinMs => 4.0;
        internal static double BudgetMaxMs => 33.5;
        internal static double SnapEpsMs => 0.40;
        internal static int HotEnableStreak => 2;
        internal static double SpikeRunMs => 50.0;
        internal static double SpikePausedMs => 80.0;
        internal static double FlushOnHugeFrameMs => 200.0;
        internal static long AllocSpikeBytes => 25_000_000;
        internal static long WsSpikeBytes => 75_000_000;
        internal static long ForceFlushAllocBytes => 300_000_000;
        internal static long ForceFlushWsBytes => 500_000_000;
        internal static double PumpBudgetRunMs => 3.0;
        internal static double PumpBudgetFastMs => 8.0;
        internal static double PumpBudgetRunBoostMs => 4.0;
        internal static double PumpBudgetFastBoostMs => 24.0;
        internal static int PumpBacklogBoostThreshold => 1_000;
        internal static double PumpBudgetRunCapMs => 12.0;
        internal static double PumpBudgetFastCapMs => 18.0;
        internal static double PumpTailMinRunMs => 4.0;
        internal static double PumpTailMinFastMs => 6.0;
        internal static double PumpPauseTrickleMapMs => 6.0;
        internal static double PumpPauseTrickleMenuMs => 2.0;
        internal static double MapScreenProbeDtThresholdMs => 12.0;
        internal static double MapHotDurationMsThreshold => 1.0;
        internal static long MapHotAllocThresholdBytes => 128 * 1024;
        internal static double MapHotCooldownSeconds => 0.05;

        // Preset-driven bits
        internal static double MapScreenBackoffMs1
        {
            get
            {
                switch (Preset)
                {
                    case ThrottlePreset.Off: return 999.0;
                    case ThrottlePreset.Aggressive: return 8.0;
                    default: return 10.0;
                }
            }
        }

        internal static double MapScreenBackoffMs2
        {
            get
            {
                switch (Preset)
                {
                    case ThrottlePreset.Off: return 999.0;
                    case ThrottlePreset.Aggressive: return 12.0;
                    default: return 14.0;
                }
            }
        }

        internal static int MapScreenSkipFrames1
        {
            get
            {
                switch (Preset)
                {
                    case ThrottlePreset.Off: return 0;
                    case ThrottlePreset.Aggressive: return 2;
                    default: return 1;
                }
            }
        }

        internal static int MapScreenSkipFrames2
        {
            get
            {
                switch (Preset)
                {
                    case ThrottlePreset.Off: return 0;
                    case ThrottlePreset.Aggressive: return 3;
                    default: return 2;
                }
            }
        }

        internal static int MapScreenSkipFramesCap
        {
            get
            {
                switch (Preset)
                {
                    case ThrottlePreset.Off: return 0;
                    case ThrottlePreset.Aggressive: return 8;
                    default: return 6;
                }
            }
        }

        // Filter toggles
        internal static bool F_Raids => Get(s => s.SilenceRaids, false);
        internal static bool F_Sieges => Get(s => s.SilenceSieges, false);
        internal static bool F_WarPeace => Get(s => s.SilenceWarPeace, false);
        internal static bool F_ArmiesParties => Get(s => s.SilenceArmiesParties, false);
        internal static bool F_Economy => Get(s => s.SilenceEconomy, false);
        internal static bool F_Settlements => Get(s => s.SilenceSettlements, false);
        internal static bool F_Quests => Get(s => s.SilenceQuests, false);
        internal static bool F_SkillsTraits => Get(s => s.SilenceSkillsTraits, false);
        internal static bool F_ClanKingdom => Get(s => s.SilenceClanKingdom, false);
        internal static string CustomPatterns => Get(s => s.CustomPatterns, "is raiding; besieging", allowEmptyString: true) ?? string.Empty;
    }
}

