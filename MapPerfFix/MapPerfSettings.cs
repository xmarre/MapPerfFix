using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace MapPerfProbe
{
    public sealed class MapPerfSettings : AttributeGlobalSettings<MapPerfSettings>
    {
        public override string Id => "MapPerfProbe_v1";
        public override string DisplayName => "MapPerfProbe";
        public override string FolderName => "MapPerfProbe";
        public override string FormatType => "json";

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyBool("Enable MapPerfProbe", RequireRestart = false, Order = 0)]
        public bool Enabled { get; set; } = true;

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyBool("Debug logging", RequireRestart = false, Order = 1)]
        public bool DebugLogging { get; set; } = true;

        [SettingPropertyGroup("Safe optimizations", GroupOrder = 1)]
        [SettingPropertyBool("Skip fully hidden mobile-party visual ticks",
            HintText = "Bannerlord 1.2.x only. Skips rendering and animation work after a mobile party is fully faded and hidden. Campaign simulation, positions, events, AI, and periodic callbacks are untouched.",
            RequireRestart = false, Order = 0)]
        public bool OptimizeHiddenPartyVisuals { get; set; } = true;

        [SettingPropertyGroup("Safe optimizations", GroupOrder = 1)]
        [SettingPropertyBool("Tune GC latency on the campaign map",
            HintText = "Changes only the .NET garbage-collector latency mode.",
            RequireRestart = false, Order = 1)]
        public bool TuneGcLatency { get; set; } = true;

        [SettingPropertyGroup("Diagnostics", GroupOrder = 2)]
        [SettingPropertyBool("Profile TOR campaign callbacks",
            HintText = "Measures TOR campaign callback time without skipping or delaying calls and writes aggregate reports to probe.log.",
            RequireRestart = false, Order = 0)]
        public bool ProfileTorCampaignCallbacks { get; set; } = true;

        [SettingPropertyGroup("Diagnostics", GroupOrder = 2)]
        [SettingPropertyInteger("Slow callback threshold (ms)", 1, 100,
            HintText = "Individual TOR callbacks at or above this duration are logged.",
            RequireRestart = false, Order = 1)]
        public int SlowCallbackThresholdMs { get; set; } = 8;

        [SettingPropertyGroup("Diagnostics", GroupOrder = 2)]
        [SettingPropertyInteger("Profiler report interval (seconds)", 5, 120,
            HintText = "Interval for aggregate top-callback reports.",
            RequireRestart = false, Order = 2)]
        public int ProfilerReportIntervalSeconds { get; set; } = 30;
    }
}
