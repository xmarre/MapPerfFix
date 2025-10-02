using System;
using System.Linq;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;
using MCM.Abstractions.Dropdowns;

namespace MapPerfProbe
{
    public enum ThrottlePreset { Off, Balanced, Aggressive }

    public sealed class MapPerfSettings : AttributeGlobalSettings<MapPerfSettings>
    {
        public override string Id => "MapPerfProbe_v1";
        public override string DisplayName => "Map Performance Probe";
        public override string FolderName => "MapPerfProbe";
        public override string FormatType => "json";

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyBool("Debug Logging", RequireRestart = false, Order = 0)]
        public bool DebugLogging { get; set; } = false;

        [SettingPropertyGroup("Map Throttle", GroupOrder = 1)]
        [SettingPropertyBool("Enable Map Throttle", Order = 0)]
        public bool EnableMapThrottle { get; set; } = true;

        [SettingPropertyGroup("Map Throttle", GroupOrder = 1)]
        [SettingPropertyBool("Throttle Only In Fast-Forward", Order = 1)]
        public bool ThrottleOnlyInFastTime { get; set; } = true;

        // MCM v5: use Dropdown<T> instead of SettingPropertyEnum
        [SettingPropertyGroup("Map Throttle", GroupOrder = 1)]
        [SettingPropertyDropdown("Preset", Order = 2)]
        public Dropdown<ThrottlePreset> PresetOption { get; set; } =
            new Dropdown<ThrottlePreset>(
                Enum.GetValues(typeof(ThrottlePreset)).Cast<ThrottlePreset>().ToArray(),
                (int)ThrottlePreset.Balanced
            );

        // Convenience getter for code usage
        public ThrottlePreset Preset =>
            (PresetOption?.SelectedValue) ?? ThrottlePreset.Balanced;

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

