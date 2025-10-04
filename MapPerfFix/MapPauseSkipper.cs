using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace MapPerfProbe
{
    internal static class MapPauseSkipper
    {
        private static readonly string HarmonyId = SubModule.HarmonyId + ".pause-skipper";
        private static readonly ConcurrentDictionary<(Type Type, string Name), MemberInfo> _boolCache =
            new ConcurrentDictionary<(Type Type, string Name), MemberInfo>();
        private static Harmony _harmony;

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                _harmony = new Harmony(HarmonyId);
            }
            catch (Exception ex)
            {
                MapPerfLog.Warn($"MapPauseSkipper Harmony init failed: {ex.Message}");
                _harmony = null;
                return;
            }

            TryPatchMapScreenOnFrameTick();
            TryPatchPartyVisualTicks();
        }

        private static void TryPatchMapScreenOnFrameTick()
        {
            if (_harmony == null) return;
            try
            {
                var mapScreenType = AccessTools.TypeByName("SandBox.View.Map.MapScreen");
                if (mapScreenType == null) return;

                var method = AccessTools.Method(mapScreenType, "OnFrameTick", new[] { typeof(float) });
                if (!IsPatchable(method)) return;

                _harmony.Patch(method, prefix: new HarmonyMethod(typeof(MapPauseSkipper), nameof(OnFrameTickPrefix)));
            }
            catch (Exception ex)
            {
                MapPerfLog.Warn($"MapPauseSkipper MapScreen.OnFrameTick patch failed: {ex.Message}");
            }
        }

        private static void TryPatchPartyVisualTicks()
        {
            if (_harmony == null) return;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    var fullName = type?.FullName;
                    if (string.IsNullOrEmpty(fullName)) continue;
                    if (!fullName.Contains("SandBox.View.Map")) continue;
                    if (!(fullName.Contains("PartyVisual") || fullName.Contains("ArmyVisual"))) continue;

                    MethodInfo method = null;
                    try
                    {
                        method = AccessTools.Method(type, "Tick", new[] { typeof(float) });
                    }
                    catch
                    {
                        // Ignore and continue
                    }
                    if (!IsPatchable(method)) continue;

                    try
                    {
                        _harmony.Patch(method, prefix: new HarmonyMethod(typeof(MapPauseSkipper), nameof(PartyVisualTickPrefix)));
                    }
                    catch (Exception ex)
                    {
                        MapPerfLog.Warn($"MapPauseSkipper {fullName}.Tick patch failed: {ex.Message}");
                    }
                }
            }
        }

        private static bool IsPatchable(MethodInfo method)
        {
            if (method == null) return false;
            if (method.IsAbstract) return false;
            if (method.ContainsGenericParameters) return false;
            if (method.IsSpecialName) return false;
            return method.GetMethodBody() != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool OnFrameTickPrefix()
            => !(MapPerfConfig.HardPauseSkip && InitGate.MapReady() && IsPaused());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool PartyVisualTickPrefix(object __instance)
        {
            if (!InitGate.MapReady()) return true;
            if (!IsPaused()) return true;
            if (!MapPerfConfig.SkipPausedVisuals) return true; // MCM toggle

            try
            {
                var type = __instance.GetType();
                var isHovered = GetBool(type, __instance, "IsHovered") ?? GetBool(type, __instance, "_isHovered") ?? false;
                var isSelected = GetBool(type, __instance, "IsSelected") ?? GetBool(type, __instance, "_isSelected") ?? false;
                var isMainParty = GetBool(type, __instance, "IsMainParty") ?? GetBool(type, __instance, "_isMainParty") ?? false;
                var isTracked = GetBool(type, __instance, "IsTracked") ?? GetBool(type, __instance, "_isTracked") ?? false;
                if (isHovered || isSelected || isMainParty || isTracked) return true;

                var inFrustum = GetBool(type, __instance, "IsInScreenBounds")
                                ?? GetBool(type, __instance, "_isInScreenBounds") ?? false;
                if (inFrustum) return true;
            }
            catch
            {
                // Ignore and fall through to skip
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPaused()
        {
            var campaign = Campaign.Current;
            return campaign != null && campaign.TimeControlMode == CampaignTimeControlMode.Stop;
        }

        private static bool? GetBool(Type type, object instance, string name)
        {
            if (type == null) return null;
            var key = (Type: type, Name: name);
            var member = _boolCache.GetOrAdd(key, k =>
            {
                var fi = AccessTools.Field(k.Type, k.Name);
                if (fi != null && fi.FieldType == typeof(bool)) return (MemberInfo)fi;

                var pi = AccessTools.Property(k.Type, k.Name);
                if (pi != null && pi.PropertyType == typeof(bool)) return (MemberInfo)pi;

                return null;
            });

            var fi = member as FieldInfo;
            if (fi != null)
            {
                try
                {
                    return (bool)fi.GetValue(instance);
                }
                catch
                {
                    return null;
                }
            }

            var pi = member as PropertyInfo;
            if (pi != null)
            {
                try
                {
                    var getter = pi.GetMethod;
                    return getter != null ? (bool)getter.Invoke(instance, Array.Empty<object>()) : (bool?)null;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }
    }
}
