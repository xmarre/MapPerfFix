using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MapPerfProbe
{
    public class SubModule : MBSubModuleBase
    {
        private const string HId = "mmq.mapperfprobe";
        private static readonly ConcurrentDictionary<MethodBase, PerfStat> Stats = new ConcurrentDictionary<MethodBase, PerfStat>();
        private static string _logFile;

        private static long _lastFrameTS = Stopwatch.GetTimestamp();
        private static readonly int[] _gcLast = new int[3];
        private static double _nextFlush = 0.0;

        protected override void OnSubModuleLoad()
        {
            var harmony = new Harmony(HId);
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var logDir = Path.Combine(root, "Modules", "MapPerfProbe", "Log");
            try { Directory.CreateDirectory(logDir); }
            catch { /* ignore path issues */ }
            _logFile = Path.Combine(logDir, "probe.log");
            Log("=== MapPerfProbe start ===");

            // Patch buckets: MapState ticks, CampaignEventDispatcher ticks, Gauntlet MapScreen ticks.
            TryPatchType(harmony, "TaleWorlds.CampaignSystem.GameState.MapState", new[] {"OnTick","OnMapModeTick","OnFrameTick"});

            TryPatchType(harmony, "TaleWorlds.CampaignSystem.MapState",          new[] {"OnTick","OnMapModeTick","OnFrameTick"});

            TryPatchType(harmony, "TaleWorlds.CampaignSystem.CampaignEventDispatcher",
                new[] { "OnTick", "OnHourlyTick", "OnDailyTick" });

            TryPatchType(harmony, "SandBox.View.Map.MapScreen",
                new[] { "OnFrameTick", "Tick", "OnTick" });

            // Optional: UI context update (if present)
            TryPatchType(harmony, "TaleWorlds.GauntletUI.UIContext",
                new[] { "Update", "Tick" });

            TryPatchType(harmony, "TaleWorlds.GauntletUI.GauntletLayer",
                new[] { "OnLateUpdate", "Tick" });
        }

        protected override void OnSubModuleUnloaded()
        {
            Harmony.UnpatchAll(HId);
            FlushSummary(force: true);
            Log("=== MapPerfProbe stop ===");
        }

        protected override void OnApplicationTick(float dt)
        {
            // Frame time sampling
            var now = Stopwatch.GetTimestamp();
            double ms = (now - _lastFrameTS) * 1000.0 / Stopwatch.Frequency;
            _lastFrameTS = now;
            if (ms > 25.0 && IsOnMap())
                Log($"FRAME spike {ms:F1} ms [{(IsPaused() ? "PAUSED" : "RUN")}]");

            // GC sampling
            for (int g = 0; g < 3; g++)
            {
                int c = GC.CollectionCount(g);
                if (c != _gcLast[g] && IsOnMap())
                {
                    Log($"GC Gen{g} collections +{c - _gcLast[g]}");
                    _gcLast[g] = c;
                }
            }

            // Periodic aggregate flush
            _nextFlush -= dt;
            if (_nextFlush <= 0.0)
            {
                _nextFlush = 2.0; // every ~2 real seconds
                if (IsOnMap()) FlushSummary(force: false);
            }
        }

        private static bool IsOnMap()
        {
            var s = GameStateManager.Current?.ActiveState;
            var n = s?.GetType().FullName ?? "";
            return n.EndsWith(".MapState") || n == "MapState";
        }

        private static void TryPatchType(Harmony harmony, string typeName, string[] methodNames)
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null) return;

            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!methodNames.Contains(m.Name)) continue;
                if (m.ReturnType != typeof(void)) continue;

                // Accept 0-2 args; first may be float dt
                var pars = m.GetParameters();
                if (pars.Length > 2) continue;

                try
                {
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(SubModule), nameof(PerfPrefix)),
                        postfix: new HarmonyMethod(typeof(SubModule), nameof(PerfPostfix)));
                    Log($"Patched {t.FullName}.{m.Name}");
                }
                catch (Exception ex)
                {
                    Log($"Patch fail {t.FullName}.{m.Name}: {ex.Message}");
                }
            }
        }

        // Shared prefix/postfix with __state
        public static void PerfPrefix(MethodBase __originalMethod, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void PerfPostfix(MethodBase __originalMethod, long __state)
        {
            var dt = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            var stat = Stats.GetOrAdd(__originalMethod, _ => new PerfStat(__originalMethod));
            stat.Add(dt);
        }

        private static void FlushSummary(bool force)
        {
            // Top offenders last window
            var top = Stats.Values
                .Select(s => s.SnapshotAndReset())
                .Where(s => s.Count > 0)
                .OrderByDescending(s => s.P95)
                .Take(8)
                .ToList();

            if (top.Count == 0 && !force) return;

            Log($"-- bucket summary [{(IsPaused() ? "PAUSED" : "RUN")}] --");
            foreach (var s in top)
                Log($"{s.Name,-48} avg {s.Avg:F1} ms | p95 {s.P95:F1} | max {s.Max:F1} | n {s.Count}");
        }

        private static bool IsPaused()
        {
            var camp = Campaign.Current;
            if (camp == null) return false;
            return camp.TimeControlMode == CampaignTimeControlMode.Stop;
        }

        private static void Log(string line)
        {
            if (string.IsNullOrEmpty(_logFile)) return;
            try
            {
                File.AppendAllText(_logFile, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
            }
            catch { /* ignore file IO errors */ }
        }

        private sealed class PerfStat
        {
            private double _sum, _max;
            private int _n;
            private readonly double[] _ring = new double[128];
            private int _i;
            public string Name { get; }

            public PerfStat(MethodBase m)
            {
                Name = $"{m.DeclaringType.FullName}.{m.Name}";
            }

            public void Add(double ms)
            {
                _sum += ms;
                if (ms > _max) _max = ms;
                _ring[_i++ & 127] = ms;
                _n++;
            }

            public Snapshot SnapshotAndReset()
            {
                var cnt = Math.Min(_n, _ring.Length);
                var arr = new double[cnt];
                // copy last cnt samples
                for (int k = 0; k < cnt; k++) arr[k] = _ring[(_i - 1 - k) & 127];
                Array.Sort(arr);
                double p95 = 0.0;
                if (cnt > 0)
                {
                    int idx = (int)Math.Floor(cnt * 0.95) - 1;
                    if (idx < 0) idx = 0;
                    if (idx >= cnt) idx = cnt - 1;
                    p95 = arr[idx];
                }
                var snap = new Snapshot
                {
                    Name = Name,
                    Avg = _n > 0 ? _sum / _n : 0.0,
                    Max = _max,
                    P95 = p95,
                    Count = _n
                };
                _sum = 0; _max = 0; _n = 0; // keep ring for continuity
                return snap;
            }
        }

        private struct Snapshot
        {
            public string Name;
            public double Avg, Max, P95;
            public int Count;
        }
    }
}
