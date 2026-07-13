# MapPerfProbe

A Bannerlord campaign-map performance module with one hard invariant: campaign, AI, event, periodic, save, and UI callbacks remain synchronous and execute on their original call path.

## What version 2.1 does

### Hidden mobile-party visual optimization

Bannerlord 1.2.x performs `AgentVisuals` animation work for mobile parties before checking whether the party has fully faded out. MapPerfProbe skips `PartyVisual.Tick` only when all of the following are already true:

- the visual belongs to a mobile party;
- the party is not visible to the player;
- its visual alpha is already zero;
- no level-mask refresh is pending.

The campaign party continues to move, run AI, participate in events, and receive all periodic callbacks. Its visual tick resumes on the first frame where visibility returns. Settlements and fading or visible parties are never skipped.

Bannerlord's newer `SandBox.View.Map.Visuals.MobilePartyVisual` implementation already gates hidden `AgentVisuals`, so MapPerfProbe detects that implementation and does not install a redundant skip.

### TOR callback profiler

MapPerfProbe instruments TOR campaign tick methods with timing-only Harmony prefixes and postfixes. It records calls, total time, average time, maximum time, and slow-call counts. These patches never return `false`, alter arguments, reschedule work, or replace methods.

The profiler is enabled by default so the remaining campaign simulation hotspot can be identified from real gameplay rather than hidden behind broad tick skipping.

### GC latency

The module can select a lower-latency .NET GC mode while the campaign map is active. This changes managed-runtime collection policy only.

## Logging

Version 2.1.1 loads a minimal bootstrap submodule before the MCM-dependent main submodule. It writes an early sentinel before MCM settings or Harmony are touched:

```text
%TEMP%\MapPerfProbe\bootstrap.log
```

The sentinel includes the exact `MapPerfProbe.dll` path loaded by Bannerlord. If this file is absent after launching with the module enabled, Bannerlord did not load the DLL or did not instantiate the bootstrap submodule.

After bootstrap, MapPerfProbe creates `probe.log` and displays its selected path in game. It also mirrors log lines to Bannerlord's engine debug output when that API is available.

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
- fully-hidden visual skip counts and skip rate;
- individual slow TOR callbacks;
- aggregate TOR callback reports every 30 seconds by default.

## Module identity

The established loader contract remains:

```text
Module ID: MapPerfProbe
DLL:       MapPerfProbe.dll
Bootstrap: MapPerfProbe.BootstrapSubModule
Main:      MapPerfProbe.SubModule
```

The descriptor uses the original `Singleplayer=true` / `Multiplayer=false` schema and does not require `StoryMode`, allowing TOR sandbox loadouts to instantiate the module.

## Requirements

- Bannerlord.Harmony
- MCM v5
- Bannerlord single-player campaign modules

## Build

Build `MapPerfFix.sln` in `Release|x64`. The project uses `D:\Spiele\Mount and Blade II Bannerlord` by default. Override it without editing the project:

```powershell
msbuild MapPerfFix.sln /p:Configuration=Release /p:Platform=x64 /p:BannerlordDir="D:\Your\Bannerlord"
```

The output is `MapPerfProbe.dll`, matching `SubModule.xml`.

## Safety verification

```bash
python3 tools/verify_safety.py
```

The verifier rejects the removed simulation-deferral sources, direct campaign tick hooks, deferred work queues, unreviewed Harmony patch surfaces, the wrong loader schema, and a hard `StoryMode` dependency. The only method permitted to skip an original call is the fully-hidden legacy party-visual prefix.
