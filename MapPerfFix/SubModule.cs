using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.InputSystem;
using TaleWorlds.MountAndBlade;

namespace MapPerfProbe
{
    /// <summary>
    /// Safe campaign-map optimizations.
    ///
    /// Invariant: never skip, defer, replay, coalesce, or reorder authoritative
    /// campaign callbacks. Only confirmed rendering-only work may be throttled.
    /// </summary>
    public sealed class SubModule : MBSubModuleBase
    {
        private const string HarmonyId = "mmq.mapperffix.safe";
        private const int PatchRetryFrames = 120;
        private const int PatchCompatibilityCheckFrames = 600;

        private static readonly string[] PartyVisualTypeNames =
        {
            "SandBox.View.Map.PartyVisual"
        };

        private static readonly object PatchSync = new object();
        private static readonly ConcurrentDictionary<Type, VisualAccessors> AccessorCache =
            new ConcurrentDictionary<Type, VisualAccessors>();
        private static readonly Func<Type, VisualAccessors> CreateVisualAccessors =
            VisualAccessors.Create;

        private static Harmony _harmony;
        private static int _frameSequence;
        private static int _nextPatchProbeFrame = PatchRetryFrames;
        private static int _nextPatchCompatibilityFrame;
        private static bool _visualPatchInstalled;
        private static int _visualOptimizationEnabled;
        private static MethodInfo _visualTickMethod;
        private static bool _foreignPatchLogged;
        private static int _inputSampleFrame = -1;
        private static bool _inputSampleValue;

        private static bool _gcModeCaptured;
        private static bool _gcModeOwned;
        private static bool _gcTuningFailed;
        private static GCLatencyMode _originalGcMode;
        private static GCLatencyMode _appliedGcMode;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            MapPerfLog.DebugEnabled = MapPerfConfig.DebugLogging;
            CaptureGcMode();

            try
            {
                _harmony = new Harmony(HarmonyId);
            }
            catch (Exception ex)
            {
                MapPerfLog.Error("Harmony initialization failed", ex);
                _harmony = null;
            }

            MapPerfLog.Info("=== Map Performance Fix 2.0 started ===");
        }

        protected override void OnSubModuleUnloaded()
        {
            Volatile.Write(ref _visualOptimizationEnabled, 0);

            try
            {
                if (_harmony != null)
                    _harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                MapPerfLog.Error("Harmony unpatch failed", ex);
            }

            RestoreGcMode();
            MapPerfLog.Info("=== Map Performance Fix 2.0 stopped ===");
            base.OnSubModuleUnloaded();
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            var frame = Interlocked.Increment(ref _frameSequence);
            if (frame <= 0 || frame > 1_000_000_000)
            {
                Interlocked.Exchange(ref _frameSequence, 1);
                frame = 1;
            }

            MapPerfLog.DebugEnabled = MapPerfConfig.DebugLogging;

            if (!_visualPatchInstalled && frame >= Volatile.Read(ref _nextPatchProbeFrame))
            {
                Volatile.Write(ref _nextPatchProbeFrame, frame + PatchRetryFrames);
                TryInstallVisualPatch();
            }
            else if (_visualPatchInstalled && frame >= Volatile.Read(ref _nextPatchCompatibilityFrame))
            {
                Volatile.Write(ref _nextPatchCompatibilityFrame, frame + PatchCompatibilityCheckFrames);
                DisableVisualPatchIfCompatibilityChanged();
            }

            UpdateGcMode();
        }

        private static void CaptureGcMode()
        {
            if (_gcModeCaptured)
                return;

            try
            {
                _originalGcMode = GCSettings.LatencyMode;
                _appliedGcMode = _originalGcMode;
                _gcModeCaptured = true;
            }
            catch (Exception ex)
            {
                _gcTuningFailed = true;
                MapPerfLog.Warn("GC latency mode is unavailable: " + ex.Message);
            }
        }

        private static void UpdateGcMode()
        {
            if (_gcTuningFailed)
                return;

            if (!_gcModeCaptured)
                CaptureGcMode();

            if (!_gcModeCaptured)
                return;

            if (!MapPerfConfig.Enabled || !MapPerfConfig.TuneGcLatency || !IsCampaignMapActive())
            {
                RestoreGcMode();
                return;
            }

            var desired = IsPaused()
                ? GCLatencyMode.Interactive
                : GCLatencyMode.SustainedLowLatency;

            if (_gcModeOwned && _appliedGcMode == desired && GCSettings.LatencyMode == desired)
                return;

            try
            {
                GCSettings.LatencyMode = desired;
                _appliedGcMode = desired;
                _gcModeOwned = true;
            }
            catch (Exception ex)
            {
                _gcTuningFailed = true;
                MapPerfLog.Warn("Disabling GC latency tuning for this session: " + ex.Message);
                RestoreGcMode();
            }
        }

        private static void RestoreGcMode()
        {
            if (!_gcModeCaptured || !_gcModeOwned)
                return;

            try
            {
                GCSettings.LatencyMode = _originalGcMode;
                _appliedGcMode = _originalGcMode;
            }
            catch
            {
                // Best effort during shutdown and state transitions.
            }
            finally
            {
                _gcModeOwned = false;
            }
        }

        private static Type _gameStateManagerType;
        private static PropertyInfo _gameStateManagerCurrent;
        private static PropertyInfo _activeStateProperty;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsCampaignMapActive()
        {
            try
            {
                if (_gameStateManagerType == null)
                {
                    _gameStateManagerType = Type.GetType(
                        "TaleWorlds.Core.GameStateManager, TaleWorlds.Core",
                        false);
                }

                if (_gameStateManagerType == null)
                    return false;

                if (_gameStateManagerCurrent == null)
                {
                    _gameStateManagerCurrent = _gameStateManagerType.GetProperty(
                        "Current",
                        BindingFlags.Static | BindingFlags.Public);
                }

                var manager = _gameStateManagerCurrent?.GetValue(null, null);
                if (manager == null)
                    return false;

                if (_activeStateProperty == null ||
                    !_activeStateProperty.DeclaringType.IsInstanceOfType(manager))
                {
                    _activeStateProperty = manager.GetType().GetProperty(
                        "ActiveState",
                        BindingFlags.Instance | BindingFlags.Public);
                }

                var activeState = _activeStateProperty?.GetValue(manager, null);
                var name = activeState?.GetType().FullName;
                return string.Equals(
                    name,
                    "TaleWorlds.CampaignSystem.GameState.MapState",
                    StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPaused()
        {
            try
            {
                var campaign = Campaign.Current;
                return campaign != null &&
                       campaign.TimeControlMode == CampaignTimeControlMode.Stop;
            }
            catch
            {
                return false;
            }
        }

        private static void TryInstallVisualPatch()
        {
            if (_visualPatchInstalled || _harmony == null)
                return;

            lock (PatchSync)
            {
                if (_visualPatchInstalled || _harmony == null)
                    return;

                for (var i = 0; i < PartyVisualTypeNames.Length; i++)
                {
                    var type = AccessTools.TypeByName(PartyVisualTypeNames[i]);
                    if (type == null)
                        continue;

                    var method = FindSupportedVisualTick(type);
                    if (method == null)
                        continue;

                    if (HasForeignPatches(method))
                    {
                        if (!_foreignPatchLogged)
                        {
                            _foreignPatchLogged = true;
                            MapPerfLog.Warn(
                                "PartyVisual.Tick has patches from another mod; the paused visual optimization was not installed.");
                        }
                        Volatile.Write(ref _nextPatchProbeFrame, int.MaxValue);
                        return;
                    }

                    try
                    {
                        var prefixMethod = typeof(SubModule).GetMethod(
                            nameof(PartyVisualTickPrefix),
                            BindingFlags.Static | BindingFlags.Public);
                        if (prefixMethod == null)
                        {
                            MapPerfLog.Warn("PartyVisual prefix method could not be resolved.");
                            Volatile.Write(ref _nextPatchProbeFrame, int.MaxValue);
                            return;
                        }

                        var prefix = new HarmonyMethod(prefixMethod);
                        _harmony.Patch(method, prefix: prefix);
                        _visualTickMethod = method;
                        _visualPatchInstalled = true;
                        _nextPatchCompatibilityFrame =
                            Volatile.Read(ref _frameSequence) + PatchCompatibilityCheckFrames;
                        Volatile.Write(ref _visualOptimizationEnabled, 1);
                        MapPerfLog.Info(
                            "Installed safe paused off-screen PartyVisual optimization for " +
                            DescribeVisualTick(method) + ".");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Volatile.Write(ref _visualOptimizationEnabled, 0);
                        MapPerfLog.Error("PartyVisual.Tick patch failed", ex);
                        return;
                    }
                }
            }
        }

        private static MethodInfo FindSupportedVisualTick(Type type)
        {
            MethodInfo legacySingleFloat = null;
            var methods = AccessTools.GetDeclaredMethods(type);
            for (var i = 0; i < methods.Count; i++)
            {
                var method = methods[i];
                if (!IsPatchableVisualTick(method))
                    continue;

                if (IsCurrentVisualTickSignature(method))
                    return method;

                if (legacySingleFloat == null && IsLegacyVisualTickSignature(method))
                    legacySingleFloat = method;
            }

            return legacySingleFloat;
        }

        private static bool IsPatchableVisualTick(MethodInfo method)
        {
            if (method == null || method.IsStatic || method.IsAbstract ||
                method.ContainsGenericParameters || method.IsSpecialName)
                return false;
            if (!string.Equals(method.Name, "Tick", StringComparison.Ordinal))
                return false;
            if (method.ReturnType != typeof(void))
                return false;
            return method.GetMethodBody() != null;
        }

        private static bool IsCurrentVisualTickSignature(MethodInfo method)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 3 || parameters[0].ParameterType != typeof(float))
                return false;
            if (parameters[1].ParameterType != typeof(int).MakeByRefType())
                return false;

            var dirtyArrayByRef = parameters[2].ParameterType;
            if (!dirtyArrayByRef.IsByRef)
                return false;

            var dirtyArrayType = dirtyArrayByRef.GetElementType();
            if (dirtyArrayType == null || !dirtyArrayType.IsArray)
                return false;

            return dirtyArrayType.GetElementType() == method.DeclaringType;
        }

        private static bool IsLegacyVisualTickSignature(MethodInfo method)
        {
            var parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(float);
        }

        private static string DescribeVisualTick(MethodInfo method)
        {
            return IsCurrentVisualTickSignature(method)
                ? "Tick(float, ref int, ref PartyVisual[])"
                : "Tick(float)";
        }

        private static void DisableVisualPatchIfCompatibilityChanged()
        {
            var method = _visualTickMethod;
            if (method == null || _harmony == null)
                return;

            if (!HasForeignPatches(method))
                return;

            lock (PatchSync)
            {
                if (!_visualPatchInstalled || _visualTickMethod == null || _harmony == null)
                    return;

                // The prefix must fail open before unpatching. Harmony can throw while
                // rebuilding the wrapper, leaving the prefix physically installed.
                Volatile.Write(ref _visualOptimizationEnabled, 0);

                try
                {
                    _harmony.Unpatch(_visualTickMethod, HarmonyPatchType.Prefix, HarmonyId);
                }
                catch (Exception ex)
                {
                    MapPerfLog.Error(
                        "Could not remove the PartyVisual optimization after a compatibility change",
                        ex);
                }
                finally
                {
                    _visualTickMethod = null;
                    _visualPatchInstalled = false;
                    _foreignPatchLogged = true;
                    Volatile.Write(ref _nextPatchProbeFrame, int.MaxValue);
                }

                MapPerfLog.Warn(
                    "A different mod patched PartyVisual.Tick after startup; the paused visual optimization was disabled.");
            }
        }

        private static bool HasForeignPatches(MethodBase method)
        {
            try
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null)
                    return false;

                return HasForeignOwner(info.Prefixes) ||
                       HasForeignOwner(info.Postfixes) ||
                       HasForeignOwner(info.Transpilers) ||
                       HasForeignOwner(info.Finalizers);
            }
            catch
            {
                // Unknown patch state is not safe enough for a skip-prefix.
                return true;
            }
        }

        private static bool HasForeignOwner(IEnumerable<Patch> patches)
        {
            if (patches == null)
                return false;

            foreach (var patch in patches)
            {
                if (patch != null &&
                    !string.Equals(patch.owner, HarmonyId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        [HarmonyPriority(Priority.Last)]
        public static bool PartyVisualTickPrefix(object __instance)
        {
            if (Volatile.Read(ref _visualOptimizationEnabled) == 0)
                return true;
            if (__instance == null)
                return true;
            if (!MapPerfConfig.Enabled || !MapPerfConfig.OptimizePausedOffscreenVisuals)
                return true;
            if (!IsCampaignMapActive() || !IsPaused())
                return true;
            if (UserIsInteractingThisFrame())
                return true;

            var accessors = GetVisualAccessors(__instance.GetType());
            if (!accessors.IsSupported)
                return true;

            bool value;
            if (!accessors.InScreen.TryRead(__instance, out value))
                return true;
            if (value)
                return true;

            if (!accessors.Hovered.TryRead(__instance, out value))
                return true;
            if (value)
                return true;

            if (!accessors.Selected.TryRead(__instance, out value))
                return true;
            if (value)
                return true;

            if (!accessors.MainParty.TryRead(__instance, out value))
                return true;
            if (value)
                return true;

            if (!accessors.Tracked.TryRead(__instance, out value))
                return true;
            if (value)
                return true;

            var cadence = MapPerfConfig.PausedVisualTickCadence;
            var frame = Volatile.Read(ref _frameSequence);
            return frame <= 0 || cadence <= 1 || (frame % cadence) == 0;
        }

        private static bool UserIsInteractingThisFrame()
        {
            var frame = Volatile.Read(ref _frameSequence);
            if (Volatile.Read(ref _inputSampleFrame) == frame)
                return _inputSampleValue;

            var value = UserIsInteracting();
            _inputSampleValue = value;
            Volatile.Write(ref _inputSampleFrame, frame);
            return value;
        }

        private static bool UserIsInteracting()
        {
            try
            {
                if (Input.GetMouseMoveX() != 0f || Input.GetMouseMoveY() != 0f)
                    return true;

                return Input.IsKeyDown(InputKey.LeftMouseButton) ||
                       Input.IsKeyDown(InputKey.RightMouseButton) ||
                       Input.IsKeyDown(InputKey.MiddleMouseButton) ||
                       Input.IsKeyDown(InputKey.W) ||
                       Input.IsKeyDown(InputKey.A) ||
                       Input.IsKeyDown(InputKey.S) ||
                       Input.IsKeyDown(InputKey.D) ||
                       Input.IsKeyDown(InputKey.Up) ||
                       Input.IsKeyDown(InputKey.Down) ||
                       Input.IsKeyDown(InputKey.Left) ||
                       Input.IsKeyDown(InputKey.Right) ||
                       Input.IsKeyDown(InputKey.LeftShift) ||
                       Input.IsKeyDown(InputKey.RightShift) ||
                       Input.IsKeyDown(InputKey.MouseScrollUp) ||
                       Input.IsKeyDown(InputKey.MouseScrollDown);
            }
            catch
            {
                // Input API mismatch: fail open and run the visual tick.
                return true;
            }
        }

        private static VisualAccessors GetVisualAccessors(Type type)
        {
            var accessors = AccessorCache.GetOrAdd(type, CreateVisualAccessors);
            if (!accessors.IsSupported && accessors.TryMarkUnsupportedLogged())
            {
                MapPerfLog.Debug(
                    "PartyVisual optimization disabled for " + type.FullName +
                    ": required visibility state could not be verified.");
            }

            return accessors;
        }

        private sealed class VisualAccessors
        {
            internal readonly BoolAccessor InScreen;
            internal readonly BoolAccessor Hovered;
            internal readonly BoolAccessor Selected;
            internal readonly BoolAccessor MainParty;
            internal readonly BoolAccessor Tracked;
            private int _unsupportedLogged;

            internal bool IsSupported =>
                InScreen != null && Hovered != null && Selected != null &&
                MainParty != null && Tracked != null;

            private VisualAccessors(
                BoolAccessor inScreen,
                BoolAccessor hovered,
                BoolAccessor selected,
                BoolAccessor mainParty,
                BoolAccessor tracked)
            {
                InScreen = inScreen;
                Hovered = hovered;
                Selected = selected;
                MainParty = mainParty;
                Tracked = tracked;
            }

            internal bool TryMarkUnsupportedLogged()
            {
                return Interlocked.Exchange(ref _unsupportedLogged, 1) == 0;
            }

            internal static VisualAccessors Create(Type type)
            {
                return new VisualAccessors(
                    BoolAccessor.Find(type, "IsInScreenBounds", "_isInScreenBounds"),
                    BoolAccessor.Find(type, "IsHovered", "_isHovered"),
                    BoolAccessor.Find(type, "IsSelected", "_isSelected"),
                    BoolAccessor.Find(type, "IsMainParty", "_isMainParty"),
                    BoolAccessor.Find(type, "IsTracked", "_isTracked"));
            }
        }

        private sealed class BoolAccessor
        {
            private readonly FieldInfo _field;
            private readonly MethodInfo _getter;

            private BoolAccessor(FieldInfo field, MethodInfo getter)
            {
                _field = field;
                _getter = getter;
            }

            internal static BoolAccessor Find(Type type, params string[] names)
            {
                const BindingFlags Flags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly;

                for (var current = type; current != null; current = current.BaseType)
                {
                    for (var i = 0; i < names.Length; i++)
                    {
                        var field = current.GetField(names[i], Flags);
                        if (field != null && field.FieldType == typeof(bool))
                            return new BoolAccessor(field, null);

                        var property = current.GetProperty(names[i], Flags);
                        if (property == null || property.PropertyType != typeof(bool))
                            continue;

                        var getter = property.GetGetMethod(true);
                        if (getter != null && getter.GetParameters().Length == 0)
                            return new BoolAccessor(null, getter);
                    }
                }

                return null;
            }

            internal bool TryRead(object instance, out bool value)
            {
                try
                {
                    if (_field != null)
                    {
                        value = (bool)_field.GetValue(instance);
                        return true;
                    }

                    if (_getter != null)
                    {
                        value = (bool)_getter.Invoke(instance, null);
                        return true;
                    }
                }
                catch
                {
                    // Fail open in the caller.
                }

                value = false;
                return false;
            }
        }
    }
}
