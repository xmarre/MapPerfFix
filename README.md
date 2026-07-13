# MapPerfProbe

A Bannerlord campaign-map performance module with one hard invariant: campaign, AI, event, periodic, save, and UI callbacks remain synchronous and execute on their original call path.

## Emergency notice

Version 2.2.0 contains a broken module dependency declaration. Bannerlord can parse module descriptors even when a module is unchecked, so leaving 2.2.0 in the `Modules` directory can prevent the launcher from selecting the mod and can crash startup.

Delete the complete `Modules\MapPerfProbe` directory before installing 2.2.1. Disabling 2.2.0 in the launcher is not sufficient.

## Supported release

MapPerfProbe 2.2.1 targets:

- Mount & Blade II: Bannerlord 1.3.15;
- The Old Realms: War in the Mountains 1.16;
- Bannerlord.Harmony;
- Mod Configuration Menu v5, module ID `Bannerlord.MBOptionScreen`.

## Loader hotfix in 2.2.1

Version 2.2.0 declared a dependency on `MCMv5` with `DependentVersion="v5"`.

That violated two loader invariants:

- the installed MCM module ID is `Bannerlord.MBOptionScreen`, so the dependency could never be satisfied;
- Bannerlord module versions use complete version values, so the abbreviated `v5` value was unsafe during module discovery.

Version 2.2.1:

- uses `Bannerlord.MBOptionScreen` as the dependency ID;
- removes the malformed abbreviated version constraint;
- corrects the released DLL references for `TaleWorlds.MountAndBlade` and `TaleWorlds.CampaignSystem` from synthetic `0.0.0.0` identities to the supplied game assemblies' `1.0.0.0` identity;
- omits the stale PDB from the emergency release package.

## Installation

1. Delete any existing `Modules\MapPerfProbe` directory.
2. Extract the 2.2.1 archive into Bannerlord's `Modules` directory.
3. Verify this exact path exists: `Modules\MapPerfProbe\SubModule.xml`.
4. Enable MapPerfProbe in the launcher.
5. Load it after Harmony, MCM, and the Bannerlord campaign modules.

## Runtime behavior

### Hidden mobile-party visual optimization

MapPerfProbe supports the legacy `SandBox.View.Map.PartyVisual` implementation. It skips `PartyVisual.Tick` only when the party is already invisible, fully faded, not a settlement, and has no pending level-mask refresh. Campaign movement, AI, events, saves, and periodic callbacks remain unchanged.

When `SandBox.View.Map.Visuals.MobilePartyVisual` is present, the module leaves that implementation unpatched.

### TOR callback profiler

Matching `TOR_Core.Campaign*` methods receive timing-only Harmony prefixes and postfixes. The profiler records call counts, total time, average time, maximum time, and slow calls. It does not replace, defer, reorder, or skip TOR callbacks.

### GC latency

The module can select a lower-latency .NET GC mode while the campaign map is active. This changes managed-runtime collection policy only.

## Runtime indicators

The bootstrap attempts to display:

```text
MapPerfProbe 2.2.1 LOADED. Log: <selected probe.log path>
```

It also attempts to create these files beside the loaded DLL:

```text
MapPerfProbe.loaded.txt
MapPerfProbe-bootstrap.log
```

Additional bootstrap log locations:

```text
%TEMP%\MapPerfProbe\bootstrap.log
%LOCALAPPDATA%\MapPerfProbe\bootstrap.log
<Bannerlord executable directory>\MapPerfProbe-bootstrap.log
```

Primary runtime log:

```text
Documents\Mount and Blade II Bannerlord\Logs\MapPerfProbe\probe.log
```

## Module identity

```text
Module ID: MapPerfProbe
DLL:       MapPerfProbe.dll
Bootstrap: MapPerfProbe.BootstrapSubModule
Main:      MapPerfProbe.SubModule
```

The repository remains named `MapPerfFix`; the established game loader DLL is `MapPerfProbe.dll`.

## Build

Build `MapPerfFix.sln` in `Release|x64` against an actual Bannerlord 1.3.15 installation:

```powershell
msbuild MapPerfFix.sln /restore /p:Configuration=Release /p:Platform=x64 /p:BannerlordDir="D:\Your\Bannerlord"
```

Synthetic TaleWorlds reference assemblies are not a valid release build input.

## Safety verification

```bash
python3 tools/sync_version.py --check
python3 tools/verify_safety.py
```

The verifier rejects removed simulation-deferral sources, direct campaign tick hooks, deferred work queues, unreviewed Harmony patch surfaces, the obsolete `MCMv5` module ID, abbreviated dependency versions, the wrong DLL name, and a hard `StoryMode` dependency.
