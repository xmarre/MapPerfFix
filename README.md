# MapPerfProbe

A Bannerlord campaign-map performance module with one hard invariant: campaign, AI, event, periodic, save, and UI callbacks remain synchronous and execute on their original call path.

## Supported release

MapPerfProbe 2.2.0 is built for:

- Mount & Blade II: Bannerlord 1.3.15;
- The Old Realms: War in the Mountains 1.16;
- Bannerlord.Harmony;
- MCM v5.

The supplied Bannerlord 1.3.15 assemblies retain every directly referenced runtime contract used by the module:

- `MBSubModuleBase.OnSubModuleLoad()`;
- `MBSubModuleBase.OnSubModuleUnloaded()`;
- `MBSubModuleBase.OnBeforeInitialModuleScreenSetAsRoot()`;
- `MBSubModuleBase.OnApplicationTick(float)`;
- `Campaign.Current` and `Campaign.TimeControlMode`;
- `CampaignTimeControlMode.Stop`;
- `TaleWorlds.Library.InformationManager.DisplayMessage(InformationMessage)`;
- `InformationMessage(string)`.

The supplied TOR 1.16 assembly exposes 26 eligible `TOR_Core.Campaign*` methods matched by the timing profiler. The method set is discovered at runtime; no TOR callback is replaced, delayed, or skipped.

## What version 2.2 does

### Hidden mobile-party visual optimization

MapPerfProbe supports the legacy `SandBox.View.Map.PartyVisual` implementation. It skips `PartyVisual.Tick` only when all of the following are already true:

- the visual belongs to a mobile party;
- the party is not visible to the player;
- its visual alpha is already zero;
- no level-mask refresh is pending.

The campaign party continues to move, run AI, participate in events, and receive every periodic callback. Its visual tick resumes on the first frame where visibility returns. Settlements and fading or visible parties are never skipped.

When the newer `SandBox.View.Map.Visuals.MobilePartyVisual` implementation is present, MapPerfProbe leaves that path unpatched because it already gates hidden `AgentVisuals` work.

### TOR callback profiler

MapPerfProbe instruments matching TOR campaign methods with timing-only Harmony prefixes and postfixes. It records calls, total time, average time, maximum time, and slow-call counts. These patches never return `false`, alter arguments, reschedule work, or replace methods.

The profiler is enabled by default so remaining campaign-simulation hotspots can be identified from real gameplay.

### GC latency

The module can select a lower-latency .NET GC mode while the campaign map is active. This changes managed-runtime collection policy only.

## Proving that the module is running

MapPerfProbe provides three independent runtime indicators.

### Main-menu status

After Bannerlord creates its initial module screen, the bootstrap displays:

```text
MapPerfProbe 2.2.0 LOADED. Log: <selected probe.log path>
```

The displayed version is read from the compiled assembly.

### Marker beside the loaded DLL

When `MapPerfProbe.dll` is instantiated, the bootstrap attempts to create:

```text
MapPerfProbe.loaded.txt
MapPerfProbe-bootstrap.log
```

Both files are written beside the DLL Bannerlord actually loaded when that directory is writable. `MapPerfProbe.loaded.txt` is overwritten on each load and includes the exact assembly path.

### Bootstrap logs

The bootstrap also appends to every writable diagnostic location:

```text
%TEMP%\MapPerfProbe\bootstrap.log
%LOCALAPPDATA%\MapPerfProbe\bootstrap.log
<Bannerlord executable directory>\MapPerfProbe-bootstrap.log
```

If there is no main-menu message, no adjacent marker, and no bootstrap log, Bannerlord did not instantiate `MapPerfProbe.BootstrapSubModule`. Check the installed `SubModule.xml`, enabled module state, DLL location, and dependency/load-order errors.

## Runtime log

After bootstrap, MapPerfProbe creates `probe.log` and displays its selected path in game. It also mirrors records to Bannerlord's engine debug output when that API is available.

Primary path:

```text
Documents\Mount and Blade II Bannerlord\Logs\MapPerfProbe\probe.log
```

Fallbacks:

```text
%LOCALAPPDATA%\MapPerfProbe\probe.log
<Bannerlord executable directory>\MapPerfProbe.log
```

The log includes:

- bootstrap, module, and logger startup confirmation;
- whether the legacy visual optimization installed;
- fully hidden visual skip counts and skip rate;
- the actual number of TOR methods instrumented;
- individual slow TOR callbacks;
- aggregate TOR callback reports every 30 seconds by default.

## Module identity

```text
Module ID: MapPerfProbe
DLL:       MapPerfProbe.dll
Bootstrap: MapPerfProbe.BootstrapSubModule
Main:      MapPerfProbe.SubModule
```

The repository remains named `MapPerfFix`; the established game loader contract is `MapPerfProbe.dll` and must not be renamed.

The descriptor uses Bannerlord's current schema:

```text
DefaultModule=false
ModuleCategory=Singleplayer
ModuleType=Community
```

Each submodule contains the required `Assemblies` element. `StoryMode` is not a hard dependency, allowing TOR sandbox loadouts to instantiate the module.

## Build

Build `MapPerfFix.sln` in `Release|x64`. The project uses `D:\Spiele\Mount and Blade II Bannerlord` by default. Override it without editing the project:

```powershell
msbuild MapPerfFix.sln /restore /p:Configuration=Release /p:Platform=x64 /p:BannerlordDir="D:\Your\Bannerlord"
```

The output is `MapPerfProbe.dll`, matching `SubModule.xml`.

Verify the compiled version:

```powershell
[Reflection.AssemblyName]::GetAssemblyName("D:\Path\To\MapPerfProbe.dll").Version
```

Expected result:

```text
2.2.0.0
```

## Safety verification

```bash
python3 tools/sync_version.py --check
python3 tools/verify_safety.py
```

The verifier rejects removed simulation-deferral sources, direct campaign tick hooks, deferred work queues, unreviewed Harmony patch surfaces, obsolete loader tags, invalid module/submodule element order, missing `Assemblies` nodes, the wrong DLL name, and a hard `StoryMode` dependency. The only method permitted to skip an original call is the reviewed fully hidden legacy party-visual prefix.
