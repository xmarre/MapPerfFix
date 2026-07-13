using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace MapPerfProbe
{
    public sealed class MapPerfSettings : AttributeGlobalSettings<MapPerfSettings>
    {
        // Keep the existing ID so old configurations migrate. Removed unsafe fields are ignored.
        public override string Id => "MapPerfProbe_v1";
        public override string DisplayName => "Map Performance Fix";
        public override string FolderName => "MapPerfProbe";
        public override string FormatType => "json";

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyBool("Enable Map Performance Fix", RequireRestart = false, Order = 0)]
        public bool Enabled { get; set; } = true;

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyBool("Debug logging", RequireRestart = false, Order = 1)]
        public bool DebugLogging { get; set; } = false;

        [SettingPropertyGroup("Safe optimizations", GroupOrder = 1)]
        [SettingPropertyBool("Tune GC latency on the campaign map",
            HintText = "Changes only the .NET garbage-collector latency mode. Campaign and UI callbacks are never skipped.",
            RequireRestart = false, Order = 0)]
        public bool TuneGcLatency { get; set; } = true;

        [SettingPropertyGroup("Safe optimizations", GroupOrder = 1)]
        [SettingPropertyBool("Reduce off-screen party visual ticks while paused",
            HintText = "Affects only confirmed off-screen PartyVisual rendering while campaign time is stopped. The optimization disables itself when required visibility-state members cannot be verified.",
            RequireRestart = false, Order = 1)]
        public bool OptimizePausedOffscreenVisuals { get; set; } = true;

        [SettingPropertyGroup("Safe optimizations", GroupOrder = 1)]
        [SettingPropertyInteger("Paused off-screen visual cadence", 2, 12,
            HintText = "Run confirmed off-screen party visuals once every N rendered frames while paused.",
            RequireRestart = false, Order = 2)]
        public int PausedVisualTickCadence { get; set; } = 4;
    }
}
