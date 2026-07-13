# Map Performance Fix

A Bannerlord campaign-map performance module with one hard invariant: it never skips, defers, replays, coalesces, or reorders authoritative campaign callbacks.

## What was wrong

The previous implementation patched `MapState.OnTick`, `MapState.OnMapModeTick`, `Campaign.RealTick`, periodic event hubs, and behavior ticks with prefixes that returned `false` or replayed the methods later through a queue. Those methods are the campaign simulation path. Deferring them changed event order, produced catch-up bursts, and could omit behavior callbacks entirely.

The established module identity and loader contract remain `MapPerfProbe` / `MapPerfProbe.dll`.

## Safe optimizations

- Switches the .NET GC latency mode only while the campaign map is active.
- Reduces confirmed off-screen `PartyVisual.Tick` rendering work only while campaign time is stopped. The resolver supports the current `Tick(float, ref int, ref PartyVisual[])` signature and the legacy single-argument `Tick(float)` signature. Unknown signatures fail open and are not patched.
- Runs every campaign, map-state, periodic, save, UI, and behavior callback at its original time and on its original call path.
- Refuses the visual optimization when another mod patches the same method or when required visibility members cannot be verified.

## Requirements

- Bannerlord.Harmony 2.4.2 or newer
- MCM v5
- A single-player campaign module (`Sandbox`)

## Build

Build `MapPerfFix.sln` in `Release|x64`. The project uses `D:\Spiele\Mount and Blade II Bannerlord` by default. Override it without editing the project:

```powershell
msbuild MapPerfFix.sln /p:Configuration=Release /p:Platform=x64 /p:BannerlordDir="D:\Your\Bannerlord"
```

The output is `MapPerfProbe.dll`, matching `SubModule.xml`.

## Static safety check

```bash
python3 tools/verify_safety.py
```

## Safety boundary

A generic frame-time patch cannot make expensive campaign simulation free. This module reduces rendering and managed-runtime overhead only. Expensive synchronous campaign events still take their real execution time; they are never moved to a later frame or silently dropped.

The repository safety check uses an explicit allowlist for the reviewed `PartyVisual.Tick` patch path and rejects every other Harmony patch surface in compiled code.
