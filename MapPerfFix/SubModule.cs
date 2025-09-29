using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using TaleWorlds.MountAndBlade;

namespace MapPerfProbe
{
    public class SubModule : MBSubModuleBase
    {
        private const string HId = "mmq.mapperfprobe";
        private static readonly ConcurrentDictionary<MethodBase, PerfStat> Stats = new ConcurrentDictionary<MethodBase, PerfStat>();

        private static long _lastFrameTS = Stopwatch.GetTimestamp();
        private static readonly int[] _gcLast = new int[3];
        private static double _nextFlush = 0.0;
        private static long _lastAlloc = GC.GetTotalMemory(false);
        private static long _lastWs = GetWS();

        protected override void OnSubModuleLoad()
        {
            MapPerfLog.Info("=== MapPerfProbe start ===");

            var harmony = CreateHarmony();
            if (harmony == null)
            {
                MapPerfLog.Warn("Harmony not found; only frame/GC logging.");
                return;
            }

            TryPatchType(harmony, "TaleWorlds.CampaignSystem.GameState.MapState", new[] { "OnTick", "OnMapModeTick", "OnFrameTick" });
            TryPatchType(harmony, "TaleWorlds.CampaignSystem.MapState",          new[] { "OnTick", "OnMapModeTick", "OnFrameTick" });
            TryPatchType(harmony, "TaleWorlds.CampaignSystem.CampaignEventDispatcher", new[] { "OnTick", "OnHourlyTick", "OnDailyTick" });
            TryPatchType(harmony, "SandBox.View.Map.MapScreen", new[] { "OnFrameTick", "Tick", "OnTick" });
            TryPatchType(harmony, "TaleWorlds.GauntletUI.UIContext", new[] { "Update", "Tick" });
            TryPatchType(harmony, "TaleWorlds.GauntletUI.GauntletLayer", new[] { "OnLateUpdate", "Tick" });
        }

        protected override void OnSubModuleUnloaded()
        {
            try
            {
                var harmony = CreateHarmony();
                harmony?.GetType().GetMethod("UnpatchAll", new[] { typeof(string) })?.Invoke(harmony, new object[] { HId });
            }
            catch (Exception ex)
            {
                MapPerfLog.Error("Unpatch error", ex);
            }
            FlushSummary(force: true);
            MapPerfLog.Info("=== MapPerfProbe stop ===");
        }

        protected override void OnApplicationTick(float dt)
        {
            var nowTs = Stopwatch.GetTimestamp();
            double frameMs = (nowTs - _lastFrameTS) * 1000.0 / Stopwatch.Frequency;
            _lastFrameTS = nowTs;
            if (frameMs > 25.0 && IsOnMap())
                MapPerfLog.Warn($"FRAME spike {frameMs:F1} ms [{(IsPaused() ? "PAUSED" : "RUN")}]");

            for (int g = 0; g < 3; g++)
            {
                int c = GC.CollectionCount(g);
                if (c != _gcLast[g] && IsOnMap())
                {
                    MapPerfLog.Info($"GC Gen{g} collections +{c - _gcLast[g]}");
                    _gcLast[g] = c;
                }
            }

            var curAlloc = GC.GetTotalMemory(false);
            var allocDelta = curAlloc - _lastAlloc;
            _lastAlloc = curAlloc;
            if (IsOnMap() && allocDelta > 5_000_000)
                MapPerfLog.Warn($"ALLOC spike +{allocDelta / 1_000_000.0:F1} MB");

            var ws = GetWS();
            var wsDelta = ws - _lastWs;
            _lastWs = ws;
            if (IsOnMap() && wsDelta > 20_000_000)
                MapPerfLog.Warn($"WS spike +{wsDelta / 1_000_000.0:F1} MB");

            _nextFlush -= dt;
            if (_nextFlush <= 0.0)
            {
                _nextFlush = 2.0;
                if (IsOnMap()) FlushSummary(force: false);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll")]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
            public ulong QuotaPeakPagedPoolUsage;
            public ulong QuotaPagedPoolUsage;
            public ulong QuotaPeakNonPagedPoolUsage;
            public ulong QuotaNonPagedPoolUsage;
            public ulong PagefileUsage;
            public ulong PeakPagefileUsage;
        }

        private static long GetWS()
        {
            try
            {
                if (GetProcessMemoryInfo(GetCurrentProcess(), out var counters, Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS))))
                    return (long)counters.WorkingSetSize;
            }
            catch { }
            return 0;
        }

        private static object CreateHarmony()
        {
            // Bind to Harmony only if Bannerlord.Harmony loaded 0Harmony into AppDomain
            var ht = Type.GetType("HarmonyLib.Harmony, 0Harmony", throwOnError: false);
            if (ht == null) return null;
            return Activator.CreateInstance(ht, new object[] { HId });
        }

        private static void TryPatchType(object harmony, string typeName, string[] methodNames)
        {
            var t = FindType(typeName);
            if (t == null) return;

            var ht = harmony.GetType();
            var hmType = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", throwOnError: false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });

            var pre = typeof(SubModule).GetMethod(nameof(PerfPrefix), BindingFlags.Static | BindingFlags.Public);
            var post = typeof(SubModule).GetMethod(nameof(PerfPostfix), BindingFlags.Static | BindingFlags.Public);
            var preHM = hmCtor?.Invoke(new object[] { pre });
            var postHM = hmCtor?.Invoke(new object[] { post });

            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!NameIn(methodNames, m.Name)) continue;
                if (m.ReturnType != typeof(void)) continue;
                if (m.GetParameters().Length > 2) continue;

                try
                {
                    patchMi?.Invoke(harmony, new object[] { m, preHM, postHM, null, null });
                    MapPerfLog.Info($"Patched {t.FullName}.{m.Name}");
                }
                catch (Exception ex)
                {
                    MapPerfLog.Error($"Patch fail {t.FullName}.{m.Name}", ex);
                }
            }
        }

        private static bool NameIn(string[] names, string s)
        {
            for (int i = 0; i < names.Length; i++) if (names[i] == s) return true;
            return false;
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var tt = asm.GetType(fullName, false);
                if (tt != null) return tt;
            }
            return Type.GetType(fullName, false);
        }

        public static void PerfPrefix(MethodBase __originalMethod, out long __state)
            => __state = Stopwatch.GetTimestamp();

        public static void PerfPostfix(MethodBase __originalMethod, long __state)
        {
            var dt = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            var stat = Stats.GetOrAdd(__originalMethod, _ => new PerfStat(__originalMethod));
            stat.Add(dt);
        }

        private static void FlushSummary(bool force)
        {
            var list = new List<Snapshot>();
            foreach (var kv in Stats)
            {
                var snap = kv.Value.SnapshotAndReset();
                if (snap.Count > 0) list.Add(snap);
            }
            if (list.Count == 0 && !force) return;
            list.Sort((a, b) => b.P95.CompareTo(a.P95));
            int take = Math.Min(8, list.Count);
            MapPerfLog.Info($"-- bucket summary [{(IsPaused() ? "PAUSED" : "RUN")}] --");
            for (int i = 0; i < take; i++)
            {
                var s = list[i];
                MapPerfLog.Info($"{s.Name,-48} avg {s.Avg:F1} ms | p95 {s.P95:F1} | max {s.Max:F1} | n {s.Count}");
            }
        }

        private static bool IsOnMap()
        {
            var gsmType = Type.GetType("TaleWorlds.Core.GameStateManager, TaleWorlds.Core", throwOnError: false);
            var current = gsmType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var active = current?.GetType().GetProperty("ActiveState", BindingFlags.Public | BindingFlags.Instance)?.GetValue(current);
            var name = active?.GetType().FullName ?? string.Empty;
            return name.EndsWith(".MapState", StringComparison.Ordinal) || name == "MapState";
        }

        private static bool IsPaused()
        {
            var campT = Type.GetType("TaleWorlds.CampaignSystem.Campaign, TaleWorlds.CampaignSystem", false);
            var current = campT?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (current == null) return false;
            var tcm = campT.GetProperty("TimeControlMode", BindingFlags.Public | BindingFlags.Instance)?.GetValue(current);
            return string.Equals(tcm?.ToString(), "Stop", StringComparison.Ordinal);
        }

        private sealed class PerfStat
        {
            private double _sum, _max; private int _n;
            private readonly double[] _ring = new double[128]; private int _i;
            public string Name { get; }
            public PerfStat(MethodBase m) => Name = $"{m.DeclaringType.FullName}.{m.Name}";
            public void Add(double ms) { _sum += ms; if (ms > _max) _max = ms; _ring[_i++ & 127] = ms; _n++; }
            public Snapshot SnapshotAndReset()
            {
                int cnt = Math.Min(_n, _ring.Length); var arr = new double[cnt];
                for (int k = 0; k < cnt; k++) arr[k] = _ring[(_i - 1 - k) & 127];
                Array.Sort(arr); int idx = cnt == 0 ? 0 : Math.Max(0, Math.Min(cnt - 1, (int)Math.Floor(cnt * 0.95) - 1));
                var s = new Snapshot { Name = Name, Avg = _n > 0 ? _sum / _n : 0.0, Max = _max, P95 = cnt > 0 ? arr[idx] : 0.0, Count = _n };
                _sum = 0; _max = 0; _n = 0; return s;
            }
        }
        private struct Snapshot { public string Name; public double Avg, Max, P95; public int Count; }
    }
}
