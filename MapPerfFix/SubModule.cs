using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using TaleWorlds.MountAndBlade;

namespace MapPerfProbe
{
    public class SubModule : MBSubModuleBase
    {
        private const string HId = "mmq.mapperfprobe";
        private static readonly ConcurrentDictionary<MethodBase, PerfStat> Stats = new ConcurrentDictionary<MethodBase, PerfStat>();
        private static bool _didPatch;
        private static readonly ConcurrentDictionary<MethodBase, double> _allocCd =
            new ConcurrentDictionary<MethodBase, double>();
        private const double AllocLogCooldown = 2.0;
        private const double AllocLogTtl = 60.0;
        private static double _nextAllocPrune;
        private static int _sample;

        [ThreadStatic] private static int _callDepth;
        [ThreadStatic] private static MethodBase _rootPeriodic;
        [ThreadStatic] private static bool _traceMem;
        [ThreadStatic] private static Dictionary<MethodBase, (double sum, int n)> _rootBucket;
        [ThreadStatic] private static double _rootBurstTotal;
        [ThreadStatic] private static int _rootDepth;
        private const double RootChildPrintCutoffMs = 0.2;
        private static readonly ConcurrentDictionary<MethodBase, double> RootAgg =
            new ConcurrentDictionary<MethodBase, double>();

        private static bool IsPeriodic(MethodBase m)
        {
            var n = m.Name;
            if (n == null) return false;
            return n.IndexOf("DailyTick", StringComparison.Ordinal) >= 0
                   || n.IndexOf("HourlyTick", StringComparison.Ordinal) >= 0
                   || n.IndexOf("WeeklyTick", StringComparison.Ordinal) >= 0
                   || string.Equals(n, "TickPeriodicEvents", StringComparison.Ordinal)
                   || string.Equals(n, "PeriodicDailyTick", StringComparison.Ordinal)
                   || string.Equals(n, "PeriodicHourlyTick", StringComparison.Ordinal)
                   || string.Equals(n, "QuarterDailyPartyTick", StringComparison.Ordinal)
                   || string.Equals(n, "TickPartialHourlyAi", StringComparison.Ordinal)
                   || string.Equals(n, "AiHourlyTick", StringComparison.Ordinal);
        }

        private static long _lastFrameTS = Stopwatch.GetTimestamp();
        private static readonly int[] _gcLast = new int[3];
        private static readonly int[] _gcAgg = new int[3];
        private static double _nextFlush = 0.0;
        private static long _lastAlloc = GC.GetTotalMemory(false);
        private static long _lastWs = GetWS();
        private static double _frameSpikeCD = 0.0;
        private static double _memSpikeCD = 0.0;

        protected override void OnSubModuleLoad()
        {
            if (_didPatch) return;
            _didPatch = true;
            MapPerfLog.Info("=== MapPerfProbe start ===");

            try
            {
                var harmony = CreateHarmony();
                if (harmony == null)
                {
                    MapPerfLog.Warn("Harmony not found; only frame/GC logging.");
                    _didPatch = false;
                    return;
                }

                // High-level map/UI hooks (already working)
                SafePatch("TryPatchType(MapState)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.GameState.MapState", new[] { "OnTick", "OnMapModeTick", "OnFrameTick" }));
                SafePatch("TryPatchType(MapState2)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.MapState", new[] { "OnTick", "OnMapModeTick", "OnFrameTick" }));
                SafePatch("TryPatchType(CampaignEventDispatcher)", () => TryPatchType(harmony, "TaleWorlds.CampaignSystem.CampaignEventDispatcher", new[] { "OnTick", "OnHourlyTick", "OnDailyTick" }));
                SafePatch("TryPatchType(MapScreen)", () => TryPatchType(harmony, "SandBox.View.Map.MapScreen", new[] { "OnFrameTick", "Tick", "OnTick" }));
                SafePatch("TryPatchType(UI)", () => TryPatchType(harmony, "TaleWorlds.GauntletUI.UIContext", new[] { "Update", "Tick" }));
                SafePatch("TryPatchType(Layer)", () => TryPatchType(harmony, "TaleWorlds.GauntletUI.GauntletLayer", new[] { "OnLateUpdate", "Tick" }));

                // 🔎 NEW: instrument the actual campaign behaviors that daily/hourly logic calls into
                SafePatch("PatchBehaviorTicks", () => PatchBehaviorTicks(harmony));
                SafePatch("PatchCampaignCoreTicks", () => PatchCampaignCoreTicks(harmony));
                SafePatch("PatchDispatcherFallback", () => PatchDispatcherFallback(harmony));
            }
            catch (Exception ex)
            {
                MapPerfLog.Error("OnSubModuleLoad fatal", ex);
                _didPatch = false; // fail safe, keep game running
            }
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
            _didPatch = false;
            FlushSummary(force: true);
            MapPerfLog.Info("=== MapPerfProbe stop ===");
        }

        protected override void OnApplicationTick(float dt)
        {
            var nowTs = Stopwatch.GetTimestamp();
            double frameMs = (nowTs - _lastFrameTS) * 1000.0 / Stopwatch.Frequency;
            _lastFrameTS = nowTs;
            if (IsOnMap() && frameMs > 50.0 && _frameSpikeCD <= 0.0)
            {
                MapPerfLog.Warn($"FRAME spike {frameMs:F1} ms [{(IsPaused() ? "PAUSED" : "RUN")}]");
                if (frameMs > 200.0) FlushSummary(true);
                _frameSpikeCD = 1.0;
            }

            for (int g = 0; g < 3; g++)
            {
                int c = GC.CollectionCount(g);
                if (c != _gcLast[g])
                {
                    _gcAgg[g] += c - _gcLast[g];
                    _gcLast[g] = c;
                }
            }

            var curAlloc = GC.GetTotalMemory(false);
            var allocDelta = curAlloc - _lastAlloc;
            _lastAlloc = curAlloc;

            var ws = GetWS();
            var wsDelta = ws - _lastWs;
            _lastWs = ws;

            if (IsOnMap() && allocDelta > 25_000_000 && _memSpikeCD <= 0.0)
            {
                MapPerfLog.Warn($"ALLOC spike +{allocDelta / 1_000_000.0:F1} MB");
                _memSpikeCD = 5.0;
            }

            if (IsOnMap() && wsDelta > 75_000_000 && _memSpikeCD <= 0.0)
            {
                MapPerfLog.Warn($"WS spike +{wsDelta / 1_000_000.0:F1} MB");
                _memSpikeCD = 5.0;
            }

            if (allocDelta > 150_000_000 || wsDelta > 250_000_000)
                FlushSummary(true);

            _nextFlush -= dt;
            if (_frameSpikeCD > 0.0)
                _frameSpikeCD = Math.Max(0.0, _frameSpikeCD - dt);
            if (_memSpikeCD > 0.0)
                _memSpikeCD = Math.Max(0.0, _memSpikeCD - dt);
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

        private static void SafePatch(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { MapPerfLog.Error($"Patch phase failed: {name}", ex); }
        }

        private const BindingFlags HookBindingFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static void TryPatchType(object harmony, string typeName, string[] methodNames)
        {
            var t = FindType(typeName);
            if (t == null) return;

            var ht = harmony.GetType();
            var harmonyAsm = ht.Assembly;
            var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (patchMi == null)
            {
                MapPerfLog.Error("Harmony Patch() method not found — aborting this patch phase.");
                return;
            }

            var pre = typeof(SubModule).GetMethod(nameof(PerfPrefix), HookBindingFlags);
            var post = typeof(SubModule).GetMethod(nameof(PerfPostfix), HookBindingFlags);
            var fin = typeof(SubModule).GetMethod(nameof(PerfFinalizer), HookBindingFlags);
            if (hmType == null || hmCtor == null || pre == null || post == null || fin == null)
            {
                MapPerfLog.Error($"HarmonyMethod ctor or prefix/postfix/finalizer not found (type={hmType != null}, ctor={hmCtor != null}, pre={pre != null}, post={post != null}, fin={fin != null}).");
                return;
            }
            var preHM = hmCtor.Invoke(new object[] { pre });
            var postHM = hmCtor.Invoke(new object[] { post });
            var finHM = hmCtor.Invoke(new object[] { fin });

            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!NameIn(methodNames, m.Name)) continue;
                if (m.ReturnType != typeof(void)) continue;
                if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true) continue;
                var ps = m.GetParameters();
                if (ps.Length > 2) continue;
                bool byref = false;
                for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) { byref = true; break; }
                if (byref) continue;

                try
                {
                    patchMi.Invoke(harmony, new object[] { m, preHM, postHM, null, finHM });
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

        // -------- NEW: broad behavior/dispatcher instrumentation ----------
        private static void PatchBehaviorTicks(object harmony)
        {
            var ht = harmony.GetType();
            var harmonyAsm = ht.Assembly;
            var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (patchMi == null)
            {
                MapPerfLog.Error("Harmony Patch() method not found — aborting this patch phase.");
                return;
            }

            var pre = typeof(SubModule).GetMethod(nameof(PerfPrefix), HookBindingFlags);
            var post = typeof(SubModule).GetMethod(nameof(PerfPostfix), HookBindingFlags);
            var fin = typeof(SubModule).GetMethod(nameof(PerfFinalizer), HookBindingFlags);
            if (hmType == null || hmCtor == null || pre == null || post == null || fin == null)
            {
                MapPerfLog.Error($"HarmonyMethod ctor or prefix/postfix/finalizer not found (type={hmType != null}, ctor={hmCtor != null}, pre={pre != null}, post={post != null}, fin={fin != null}).");
                return;
            }
            var preHM = hmCtor.Invoke(new object[] { pre });
            var postHM = hmCtor.Invoke(new object[] { post });
            var finHM = hmCtor.Invoke(new object[] { fin });

            string[] nameHits = { "DailyTick", "HourlyTick", "WeeklyTick", "OnDailyTick", "OnHourlyTick" };

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name;
                if (!(an.StartsWith("TaleWorlds") || an.StartsWith("SandBox"))) continue;

                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    if (!IsSubclassOfByName(t, "TaleWorlds.CampaignSystem.CampaignBehaviorBase")) continue;
                    if (t.Namespace != null &&
                        (t.Namespace.StartsWith("TaleWorlds.Gauntlet", StringComparison.Ordinal) ||
                         t.Namespace.StartsWith("TaleWorlds.TwoDimension", StringComparison.Ordinal)))
                        continue;

                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!AnyNameMatch(m.Name, nameHits)) continue;
                        if (m.ReturnType != typeof(void)) continue;
                        if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true) continue;
                        var ps = m.GetParameters();
                        if (ps.Length > 2) continue;
                        bool byref = false;
                        for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) { byref = true; break; }
                        if (byref) continue;
                        try
                        {
                            patchMi.Invoke(harmony, new object[] { m, preHM, postHM, null, finHM });
                            MapPerfLog.Info($"Patched {t.FullName}.{m.Name}");
                        }
                        catch (Exception ex)
                        {
                            MapPerfLog.Error($"Patch fail {t.FullName}.{m.Name}", ex);
                        }
                    }
                }
            }
        }

        private static void PatchCampaignCoreTicks(object harmony)
        {
            var ht = harmony.GetType();
            var harmonyAsm = ht.Assembly;
            var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (patchMi == null)
            {
                MapPerfLog.Error("Harmony Patch() method not found — aborting this patch phase.");
                return;
            }

            var pre = typeof(SubModule).GetMethod(nameof(PerfPrefix), HookBindingFlags);
            var post = typeof(SubModule).GetMethod(nameof(PerfPostfix), HookBindingFlags);
            var fin = typeof(SubModule).GetMethod(nameof(PerfFinalizer), HookBindingFlags);
            if (hmType == null || hmCtor == null || pre == null || post == null || fin == null)
            {
                MapPerfLog.Error($"HarmonyMethod ctor or prefix/postfix/finalizer not found (type={hmType != null}, ctor={hmCtor != null}, pre={pre != null}, post={post != null}, fin={fin != null}).");
                return;
            }
            var preHM = hmCtor.Invoke(new object[] { pre });
            var postHM = hmCtor.Invoke(new object[] { post });
            var finHM = hmCtor.Invoke(new object[] { fin });

            // Focused set: core campaign + party/settlement/AI/economy
            string[] typeHits =
            {
                ".CampaignSystem.Campaign",
                ".CampaignSystem.CampaignAiManager",
                ".CampaignSystem.MobileParty",
                ".CampaignSystem.Party.PartyAi",
                ".CampaignSystem.Settlements.SettlementComponent",
                ".CampaignSystem.Settlements.Town",
                ".CampaignSystem.Settlements.Village",
                ".CampaignSystem.Army",
                ".CampaignSystem.Clan",
                ".CampaignSystem.Kingdom",
                ".CampaignSystem.Economy",
                ".CampaignSystem.Pathfinding",
            };

            bool HitType(string full)
            {
                if (string.IsNullOrEmpty(full)) return false;
                for (int i = 0; i < typeHits.Length; i++)
                    if (full.Contains(typeHits[i])) return true;
                return false;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name;
                if (!an.StartsWith("TaleWorlds")) continue;
                Type[] types; try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    if (!HitType(t.FullName)) continue;
                    if (t.Namespace != null &&
                        (t.Namespace.StartsWith("TaleWorlds.Gauntlet", StringComparison.Ordinal) ||
                         t.Namespace.StartsWith("TaleWorlds.TwoDimension", StringComparison.Ordinal)))
                        continue;

                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var name = m.Name;
                        bool nameHit = name == "Tick" || name.EndsWith("Tick") || name.StartsWith("Tick") ||
                                       name.StartsWith("Update") || name.EndsWith("Update");
                        if (!nameHit) continue;
                        if (m.ReturnType != typeof(void)) continue;
                        if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true) continue;
                        var ps = m.GetParameters();
                        if (ps.Length > 2) continue;
                        bool byref = false;
                        for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) { byref = true; break; }
                        if (byref) continue;

                        try { patchMi.Invoke(harmony, new object[] { m, preHM, postHM, null, finHM }); MapPerfLog.Info($"Patched {t.FullName}.{name}"); }
                        catch (Exception ex) { MapPerfLog.Error($"Patch fail {t.FullName}.{name}", ex); }
                    }
                }
            }
        }

        private static void PatchDispatcherFallback(object harmony)
        {
            // Some builds rename/move the dispatcher; this scans for an obvious OnDaily/OnHourly hub.
            var ht = harmony.GetType();
            var harmonyAsm = ht.Assembly;
            var hmType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod")
                        ?? Type.GetType($"HarmonyLib.HarmonyMethod, {harmonyAsm.FullName}", false);
            var hmCtor = hmType?.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMi = ht.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });
            if (patchMi == null)
            {
                MapPerfLog.Error("Harmony Patch() method not found — aborting this patch phase.");
                return;
            }

            var pre = typeof(SubModule).GetMethod(nameof(PerfPrefix), HookBindingFlags);
            var post = typeof(SubModule).GetMethod(nameof(PerfPostfix), HookBindingFlags);
            var fin = typeof(SubModule).GetMethod(nameof(PerfFinalizer), HookBindingFlags);
            if (hmType == null || hmCtor == null || pre == null || post == null || fin == null)
            {
                MapPerfLog.Error($"HarmonyMethod ctor or prefix/postfix/finalizer not found (type={hmType != null}, ctor={hmCtor != null}, pre={pre != null}, post={post != null}, fin={fin != null}).");
                return;
            }
            var preHM = hmCtor.Invoke(new object[] { pre });
            var postHM = hmCtor.Invoke(new object[] { post });
            var finHM = hmCtor.Invoke(new object[] { fin });

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name;
                if (!an.StartsWith("TaleWorlds")) continue;
                Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (!t.FullName.Contains("Campaign") || !t.FullName.Contains("Dispatch")) continue;
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!(m.Name == "OnDailyTick" || m.Name == "OnHourlyTick")) continue;
                        if (m.ReturnType != typeof(void)) continue;
                        if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType?.IsGenericTypeDefinition == true) continue;
                        var ps = m.GetParameters();
                        if (ps.Length > 2) continue;
                        bool byref = false;
                        for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) { byref = true; break; }
                        if (byref) continue;
                        try { patchMi.Invoke(harmony, new object[] { m, preHM, postHM, null, finHM }); MapPerfLog.Info($"Patched {t.FullName}.{m.Name}"); }
                        catch (Exception ex) { MapPerfLog.Error($"Patch fail {t.FullName}.{m.Name}", ex); }
                    }
                }
            }
        }

        private static bool IsSubclassOfByName(Type t, string baseFullName)
        {
            for (var cur = t; cur != null; cur = cur.BaseType) if (cur.FullName == baseFullName) return true;
            return false;
        }

        private static bool AnyNameMatch(string name, string[] set)
        {
            for (int i = 0; i < set.Length; i++) if (name == set[i]) return true;
            return false;
        }
        // -------------------------------------------------------------------

        public struct State { public long ts; public long mem; }

        public static void PerfPrefix(MethodBase __originalMethod, out State __state)
        {
            __state = default;
            _callDepth++;
            // Choose the first periodic seen as the root this burst
            if (_rootPeriodic == null && IsPeriodic(__originalMethod))
            {
                _rootPeriodic = __originalMethod;
                _rootDepth = _callDepth;
                _traceMem = true; // turn on per-call alloc sampling for this burst
                _rootBucket = new Dictionary<MethodBase, (double, int)>(64);
                _rootBurstTotal = 0.0;
            }

            // Burst sample memory inside periodic roots; otherwise 1/16 sampling
            bool sample = _traceMem || ((Interlocked.Increment(ref _sample) & 0xF) == 0);
            if (sample) __state.mem = GC.GetTotalMemory(false);
            __state.ts = Stopwatch.GetTimestamp();
        }

        public static void PerfPostfix(MethodBase __originalMethod, State __state)
        {
            var dt = (Stopwatch.GetTimestamp() - __state.ts) * 1000.0 / Stopwatch.Frequency;
            var stat = Stats.GetOrAdd(__originalMethod, _ => new PerfStat(__originalMethod));
            stat.Add(dt);

            // Attribute to current periodic root
            var root = _rootPeriodic;
            if (root != null)
            {
                if (ReferenceEquals(__originalMethod, root))
                {
                    // window total (inclusive) + burst self
                    RootAgg.AddOrUpdate(root, dt, (_, v) => v + dt);
                    _rootBurstTotal += dt;
                }
                else if (_rootBucket != null && _callDepth == _rootDepth + 1)
                {
                    // direct children only (no double count)
                    if (!_rootBucket.TryGetValue(__originalMethod, out var ag)) ag = default;
                    ag.sum += dt; ag.n++;
                    _rootBucket[__originalMethod] = ag;
                }
            }

            if (__state.mem != 0)
            {
                var tNow = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                var alloc = GC.GetTotalMemory(false) - __state.mem;
                if (alloc > 20_000_000)
                {
                    if (!_allocCd.TryGetValue(__originalMethod, out var next) || tNow >= next)
                    {
                        _allocCd[__originalMethod] = tNow + AllocLogCooldown;
                        var owner = __originalMethod.DeclaringType?.FullName ?? "<global>";
                        MapPerfLog.Warn($"ALLOC+ {alloc / 1_000_000.0:F1} MB @ {owner}.{__originalMethod.Name}");
                    }
                }

                PruneAllocCooldowns(tNow);
            }

            if (_rootPeriodic != null && _callDepth == _rootDepth)
            {
                // Dump top children once per burst (limit spam)
                if (_rootBucket != null)
                {
                    var list = new List<KeyValuePair<MethodBase, (double sum, int n)>>(_rootBucket);
                    list.Sort((a, b) => b.Value.sum.CompareTo(a.Value.sum));
                    var rOwner = root?.DeclaringType?.FullName ?? "<global>";
                    var rName = root?.Name ?? "<none>";
                    int take = Math.Min(8, list.Count);
                    MapPerfLog.Info($"[root-burst] {rOwner}.{rName} — top children:");
                    bool anyAboveCutoff = false;
                    for (int i = 0; i < take; i++)
                    {
                        var kv = list[i];
                        if (kv.Value.sum >= RootChildPrintCutoffMs)
                        {
                            var o = kv.Key.DeclaringType?.FullName ?? "<global>";
                            var pct = _rootBurstTotal > 0 ? (100.0 * kv.Value.sum / _rootBurstTotal) : 0;
                            MapPerfLog.Info($"  ↳ {o}.{kv.Key.Name}  total {kv.Value.sum:F1} ms ({pct:F0}%) (n {kv.Value.n})");
                            anyAboveCutoff = true;
                        }
                    }
                    if (!anyAboveCutoff)
                    {
                        for (int i = 0; i < take; i++)
                        {
                            var kv = list[i];
                            var o = kv.Key.DeclaringType?.FullName ?? "<global>";
                            var pct = _rootBurstTotal > 0 ? (100.0 * kv.Value.sum / _rootBurstTotal) : 0;
                            MapPerfLog.Info($"  ↳ {o}.{kv.Key.Name}  total {kv.Value.sum:F1} ms ({pct:F0}%) (n {kv.Value.n})");
                        }
                    }
                    double childSum = 0;
                    for (int i = 0; i < list.Count; i++) childSum += list[i].Value.sum;
                    var selfMs = _rootBurstTotal - childSum;
                    if (selfMs < 0) selfMs = 0;
                    if (take > 0 || selfMs >= RootChildPrintCutoffMs)
                        MapPerfLog.Info($"  ↳ self total {selfMs:F1} ms");
                }
                _rootBucket = null;
                _rootPeriodic = null;
                _traceMem = false;
                _rootDepth = 0;
                _rootBurstTotal = 0.0;
            }
        }

        public static Exception PerfFinalizer(MethodBase __originalMethod, State __state, Exception __exception)
        {
            if (__exception != null && _rootBucket != null && _rootPeriodic != null && _callDepth == _rootDepth)
            {
                var list = new List<KeyValuePair<MethodBase, (double sum, int n)>>(_rootBucket);
                list.Sort((a, b) => b.Value.sum.CompareTo(a.Value.sum));
                var rOwner = _rootPeriodic?.DeclaringType?.FullName ?? "<global>";
                var rName = _rootPeriodic?.Name ?? "<none>";
                int take = Math.Min(8, list.Count);
                MapPerfLog.Info($"[root-burst:EX] {rOwner}.{rName} — top children:");
                bool anyAboveCutoff = false;
                for (int i = 0; i < take; i++)
                {
                    var kv = list[i];
                    if (kv.Value.sum >= RootChildPrintCutoffMs)
                    {
                        var o = kv.Key.DeclaringType?.FullName ?? "<global>";
                        var pct = _rootBurstTotal > 0 ? (100.0 * kv.Value.sum / _rootBurstTotal) : 0;
                        MapPerfLog.Info($"  ↳ {o}.{kv.Key.Name}  total {kv.Value.sum:F1} ms ({pct:F0}%) (n {kv.Value.n})");
                        anyAboveCutoff = true;
                    }
                }
                if (!anyAboveCutoff)
                {
                    for (int i = 0; i < take; i++)
                    {
                        var kv = list[i];
                        var o = kv.Key.DeclaringType?.FullName ?? "<global>";
                        var pct = _rootBurstTotal > 0 ? (100.0 * kv.Value.sum / _rootBurstTotal) : 0;
                        MapPerfLog.Info($"  ↳ {o}.{kv.Key.Name}  total {kv.Value.sum:F1} ms ({pct:F0}%) (n {kv.Value.n})");
                    }
                }
                double childSum = 0;
                for (int i = 0; i < list.Count; i++) childSum += list[i].Value.sum;
                var selfMs = _rootBurstTotal - childSum;
                if (selfMs < 0) selfMs = 0;
                if (take > 0 || selfMs >= RootChildPrintCutoffMs)
                    MapPerfLog.Info($"  ↳ self total {selfMs:F1} ms");
            }

            // ensure depth + burst cleanup even if the original threw
            if (--_callDepth <= 0 || (_rootPeriodic != null && _callDepth < _rootDepth))
            {
                _callDepth = 0;
                _rootBucket = null;
                _rootPeriodic = null;
                _traceMem = false;
                _rootDepth = 0;
                _rootBurstTotal = 0.0;
            }
            return __exception; // don't swallow
        }

        private static void PruneAllocCooldowns(double now)
        {
            double scheduled;
            do
            {
                scheduled = Volatile.Read(ref _nextAllocPrune);
                if (scheduled > now) return;
            }
            while (Interlocked.CompareExchange(ref _nextAllocPrune, now + 10.0, scheduled) != scheduled);

            foreach (var kv in _allocCd)
            {
                var lastLogTime = kv.Value - AllocLogCooldown;
                if (now - lastLogTime > AllocLogTtl)
                    _allocCd.TryRemove(kv.Key, out _);
            }
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
            // NEW: show top periodic roots (inclusive time over window)
            if (!RootAgg.IsEmpty)
            {
                var roots = new List<KeyValuePair<MethodBase, double>>(RootAgg);
                RootAgg.Clear();
                roots.Sort((a, b) => b.Value.CompareTo(a.Value));
                int rtake = Math.Min(5, roots.Count);
                for (int i = 0; i < rtake; i++)
                {
                    var r = roots[i];
                    var owner = r.Key.DeclaringType?.FullName ?? "<global>";
                    MapPerfLog.Info($"[root] {owner}.{r.Key.Name}  total {r.Value:F1} ms");
                }
            }
            if (_gcAgg[0] + _gcAgg[1] + _gcAgg[2] > 0)
            {
                MapPerfLog.Info($"GC window: Gen0 +{_gcAgg[0]}, Gen1 +{_gcAgg[1]}, Gen2 +{_gcAgg[2]}");
                _gcAgg[0] = _gcAgg[1] = _gcAgg[2] = 0;
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
