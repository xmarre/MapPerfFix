using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace MapPerfProbe
{
    public enum ThrottlePreset { Off, Balanced, Aggressive }

    public sealed class MapPerfSettings : AttributeGlobalSettings<MapPerfSettings>
    {
        public override string Id => "MapPerfProbe_v1";
        public override string DisplayName => "Map Performance Probe";
        public override string FolderName => "MapPerfProbe";
        public override string FormatType => "json";

        // --- General ---
        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyBool("Enable MapPerfProbe (master switch)", RequireRestart = false, Order = -1)]
        public bool Enabled { get; set; } = true;

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyBool("Debug Logging", RequireRestart = false, Order = 0)]
        public bool DebugLogging { get; set; } = false;

        [SettingPropertyGroup("Map Throttle", GroupOrder = 1)]
        [SettingPropertyBool("Enable Map Throttle", Order = 0)]
        public bool EnableMapThrottle { get; set; } = true;

        // Desync the simulation when we're skipping Map frames (prevents vanilla ticks from running on those frames)
        [SettingPropertyGroup("Map Throttle", GroupOrder = 1)]
        [SettingPropertyBool("Desync simulation while throttling", Order = 3)]
        public bool DesyncSimWhileThrottling { get; set; } = true;

        // Allow one Campaign tick every N skipped frames (0 = never while throttling)
        [SettingPropertyGroup("Map Throttle", GroupOrder = 1)]
        [SettingPropertyInteger("Allow 1 sim tick every N skipped", 0, 20, RequireRestart = false, Order = 4)]
        public int SimTickEveryNSkipped { get; set; } = 8;

        // Hard upper bound for how far we allow the sim to lag behind (ms of skipped time)
        [SettingPropertyGroup("Map Throttle", GroupOrder = 1)]
        [SettingPropertyInteger("Max desync (ms) before forced catch-up", 0, 5000, RequireRestart = false, Order = 5)]
        public int MaxDesyncMs { get; set; } = 1000;

        // Soften catch-up: when debt passes this watermark, auto-tighten skipping
        [SettingPropertyGroup("Map Throttle", GroupOrder = 1)]
        [SettingPropertyInteger("Catch-up watermark (ms)", 0, 5000, RequireRestart = false, Order = 6)]
        public int DesyncLowWatermarkMs { get; set; } = 400;

        [SettingPropertyGroup("Map Throttle", GroupOrder = 1)]
        [SettingPropertyBool("Throttle Only In Fast-Forward", Order = 1)]
        public bool ThrottleOnlyInFastTime { get; set; } = true;

        // NOTE: To stay compatible with all MCM v5 variants (no Dropdown<T> / no SettingPropertyEnum),
        // expose an integer and map it to the enum.
        [SettingPropertyGroup("Map Throttle", GroupOrder = 1)]
        [SettingPropertyInteger("Preset (0=Off, 1=Balanced, 2=Aggressive)", 0, 2,
            Order = 2, RequireRestart = false)]
        public int PresetIndex { get; set; } = (int)ThrottlePreset.Balanced;

        // Convenience enum view for the rest of the codebase
        public ThrottlePreset Preset
        {
            get
            {
                var clamped = PresetIndex < 0 ? 0 : (PresetIndex > 2 ? 2 : PresetIndex);
                return (ThrottlePreset) clamped;
            }
        }

        // -------- Message Filters ----------
        [SettingPropertyGroup("Message Filters", GroupOrder = 10)]
        [SettingPropertyBool("Silence: Raids", Order = 0)]
        public bool SilenceRaids { get; set; } = false;

        [SettingPropertyGroup("Message Filters")]
        [SettingPropertyBool("Silence: Sieges", Order = 1)]
        public bool SilenceSieges { get; set; } = false;

        [SettingPropertyGroup("Message Filters")]
        [SettingPropertyBool("Silence: War/Peace Declarations", Order = 2)]
        public bool SilenceWarPeace { get; set; } = false;

        [SettingPropertyGroup("Message Filters")]
        [SettingPropertyBool("Silence: Armies & Parties (create/join/disband/spotted)", Order = 3)]
        public bool SilenceArmiesParties { get; set; } = false;

        [SettingPropertyGroup("Message Filters")]
        [SettingPropertyBool("Silence: Caravans & Economy", Order = 4)]
        public bool SilenceEconomy { get; set; } = false;

        [SettingPropertyGroup("Message Filters")]
        [SettingPropertyBool("Silence: Settlements Taken/Under Attack", Order = 5)]
        public bool SilenceSettlements { get; set; } = false;

        [SettingPropertyGroup("Message Filters")]
        [SettingPropertyBool("Silence: Quests", Order = 6)]
        public bool SilenceQuests { get; set; } = false;

        [SettingPropertyGroup("Message Filters")]
        [SettingPropertyBool("Silence: Skill/Perk/Trait", Order = 7)]
        public bool SilenceSkillsTraits { get; set; } = false;

        [SettingPropertyGroup("Message Filters")]
        [SettingPropertyBool("Silence: Clan/Kingdom/Policy/Relations", Order = 8)]
        public bool SilenceClanKingdom { get; set; } = false;

        [SettingPropertyGroup("Message Filters")]
        [SettingPropertyText("Custom Silence Patterns (; separated, case-insensitive)", Order = 20)]
        public string CustomPatterns { get; set; } = "is raiding; besieging";
    }
}

