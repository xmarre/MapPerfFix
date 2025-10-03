using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace MapPerfProbe
{
    /// <summary>
    /// Defers periodic hubs into the slicer when frames are hot.
    /// </summary>
    [HarmonyPatch]
    internal static class PeriodicHubDeferrer
    {
        [ThreadStatic] private static bool _reentry;
        private static readonly ConcurrentDictionary<MethodBase, bool> _foreignCache =
            new ConcurrentDictionary<MethodBase, bool>();
        private static readonly ConcurrentDictionary<MethodBase, bool> _disabledByError =
            new ConcurrentDictionary<MethodBase, bool>();

        private static bool HotNow()
        {
            if (!SubModule.IsOnMap())
                return false;
            if (MapPerfConfig.DeferPeriodicOnMap)
                return true;
            if (SubModule.FastSnapshot)
                return true;
            return SubModule.HotOrRecent();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldDefer()
            => MapPerfConfig.Enabled && !_reentry && HotNow();

        private static bool HasForeignPatches(MethodBase method)
        {
            if (method == null)
                return false;

            return _foreignCache.GetOrAdd(method, key =>
            {
                try
                {
                    var info = Harmony.GetPatchInfo(key);
                    if (info == null)
                        return false;

                    bool HasNonSelf(IEnumerable<Patch> patches)
                    {
                        if (patches == null)
                            return false;
                        foreach (var patch in patches)
                        {
                            if (patch == null)
                                continue;
                            if (!string.Equals(patch.owner, SubModule.HarmonyId, StringComparison.Ordinal))
                                return true;
                        }

                        return false;
                    }

                    return HasNonSelf(info.Prefixes)
                           || HasNonSelf(info.Postfixes)
                           || HasNonSelf(info.Transpilers)
                           || HasNonSelf(info.Finalizers);
                }
                catch
                {
                    return false;
                }
            });
        }

        private static bool MatchesPeriodic(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            // Never touch accessors/events.
            if (name.StartsWith("get_", StringComparison.Ordinal)
                || name.StartsWith("set_", StringComparison.Ordinal)
                || name.StartsWith("add_", StringComparison.Ordinal)
                || name.StartsWith("remove_", StringComparison.Ordinal))
            {
                return false;
            }
            return name.IndexOf("Periodic", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Hourly", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Daily", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Weekly", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Quarter", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> TargetMethods()
        {
            var hubTypeNames = new HashSet<string>
            {
                "TaleWorlds.CampaignSystem.CampaignPeriodicEventManager",
                "TaleWorlds.CampaignSystem.CampaignEventDispatcher"
            };

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t?.FullName == null || !hubTypeNames.Contains(t.FullName))
                        continue;

                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                                   BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (m == null)
                            continue;
                        if (m.IsAbstract || m.IsConstructor || m.IsSpecialName)
                            continue;
                        if (m.ContainsGenericParameters)
                            continue;
                        if (m.MethodImplementationFlags.HasFlag(MethodImplAttributes.InternalCall))
                            continue;
                        // Only periodic-looking, void-returning, and NO by-ref params.
                        if (!MatchesPeriodic(m.Name))
                            continue;

                        var mi = m as MethodInfo;
                        if (mi == null || mi.ReturnType != typeof(void))
                            continue;

                        var ps = mi.GetParameters();
                        bool hasByRef = false;
                        for (int i = 0; i < ps.Length; i++)
                        {
                            if (ps[i].ParameterType.IsByRef)
                            {
                                hasByRef = true;
                                break;
                            }
                        }

                        if (!hasByRef)
                            yield return mi;
                    }
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        static bool Prefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (__originalMethod == null)
                return true;
            if (_disabledByError.ContainsKey(__originalMethod))
                return true;
            // Safety: don’t defer non-void (should be filtered already).
            if ((__originalMethod as MethodInfo)?.ReturnType != typeof(void))
                return true;
            // Safety: don’t defer if any by-ref slipped through.
            var ps = __originalMethod.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType.IsByRef)
                    return true;
            }
            if (!MapPerfConfig.DeferPeriodicOnMap && HasForeignPatches(__originalMethod))
            {
                if (MapPerfConfig.DebugLogging && SubModule.ShouldLogSlow("hub-deferrer-foreign-skip", 5.0))
                {
                    MapPerfLog.Info($"[hub-deferrer] foreign patches on {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}; skipping defer");
                }
                return true;
            }
            if (!ShouldDefer())
                return true;
            var canEnq = SubModule.MayEnqueueNow();
            if (!canEnq && SubModule.FastSnapshot)
            {
                // free a bit, then re-check the gate
                PeriodicSlicer.Pump(2.0);
                canEnq = SubModule.MayEnqueueNow();
            }
            if (!canEnq)
                return true;

            // Use bool-returning enqueue to safely fall back inline if the queue refuses us.
            Action action = () =>
            {
                _reentry = true;
                try
                {
                    if (!SubModule.IsOnMapRunning())
                        return;
                    if (!__originalMethod.IsStatic && ReferenceEquals(__instance, null))
                        return;
                    __originalMethod.Invoke(__instance, __args);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException?.Message ?? tie.Message;
                    MapPerfLog.Warn($"[hub-deferrer] {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} threw: {inner}");
                    _disabledByError.TryAdd(__originalMethod, true);
                }
                catch (Exception ex)
                {
                    MapPerfLog.Warn($"[hub-deferrer] {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} threw: {ex.Message}");
                    _disabledByError.TryAdd(__originalMethod, true);
                }
                finally
                {
                    _reentry = false;
                }
            };

            bool enq = PeriodicSlicer.EnqueueAction(action);
            if (!enq)
            {
                PeriodicSlicer.Pump(SubModule.FastSnapshot ? 3.0 : 2.0);
                if (SubModule.MayEnqueueNow())
                    enq = PeriodicSlicer.EnqueueAction(action);
            }

            if (!enq)
            {
                if (MapPerfConfig.DebugLogging && SubModule.ShouldLogSlow("hub-deferrer-fallback", 5.0))
                {
                    PeriodicSlicer.GetQueueStats(out var qlen, out var head, out var tail);
                    MapPerfLog.Info($"[hub-deferrer] inline: {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} qlen={qlen} head={head} tail={tail}");
                }
                return true; // queue full or gated; run original now
            }

            if (MapPerfConfig.DebugLogging && SubModule.ShouldLogSlow("hub-deferrer", 5.0))
            {
                MapPerfLog.Info($"[hub-deferrer] deferred {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}");
            }

            return false;
        }
    }
}
