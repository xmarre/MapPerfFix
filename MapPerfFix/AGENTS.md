# AGENTS.md

> Guidance for AI code assistants (e.g., ChatGPT / "codex" agents) working in this repository.

## Project snapshot
- **Repo purpose:** Runtime instrumentation and micro‑optimizations for the **Mount & Blade II: Bannerlord** campaign map. Goals: stable frame times and lower GC/alloc pressure.
- **Primary module:** `MapPerfFix/SubModule.cs` (namespace `MapPerfProbe`). An `MBSubModuleBase` that patches game methods via Harmony and logs hotspots.
- **Key dependencies:** HarmonyLib, TaleWorlds (Bannerlord) assemblies.
- **Language/TFM:** C# targeting the project’s existing framework. Do not change targets unless explicitly requested.
- **Core features:**
  - GC latency tuning for map play vs pause.
  - Throttling of `MapScreen` frame hooks under load.
  - Lightweight hotspot probe (“maphot”) with per‑method cooldowns and optional per‑thread alloc counters.
  - Periodic work slicing for daily/hourly events (`PeriodicSlicer`).
  - Frame/memory spike detection with summary flushes.
  - Adaptive frame‑budget with vsync snap and headroom.

---

## Entry points and invariants
- `SubModule.OnSubModuleLoad()` – patch bootstrap. **Never throw.** Use `SafePatch(...)`.
- `OnApplicationTick(float dt)` – frame/GC/memory tracking, spike detection, adaptive thresholds, and `PeriodicSlicer.Pump(...)`.
- **Throttle:** `PatchMapScreenThrottle(...)` + `MapScreenOnFrameTickPrefix(...)` control map frame hooks.
- **Hotspot probe:** `MapHotPrefix(...)` / `MapHotPostfix(...)` collect cheap timing and optional allocs behind `_mapHotGate`.
- **Broad patchers:** `TryPatchType(...)`, `PatchMapScreenHotspots(...)`, `PatchBehaviorTicks(...)`, `PatchCampaignCoreTicks(...)`, `PatchDispatcherFallback(...)`.

**Invariants**
- Avoid allocations or blocking in hot paths. Prefer `readonly struct`, caches, `AggressiveInlining`, and cooldown‑guarded logging.
- Skip patching property/abstract/generic/special‑name methods.
- Honor `_mapHotGate` and per‑target cooldowns (`MapHotCooldowns`).
- Best‑effort and fail‑open: if a patch fails, keep the game running and log once.

---

## Adaptive frame budget
The budget is learned from measured frame times, snapped to common vsync periods, then given slack (headroom).
- Keep headroom (e.g., ×1.20) when classifying frames as over‑budget.
- Reset streaks when paused or off map.
- Preserve threshold ordering: spike < huge‑frame flush; alloc and WS thresholds are monotone.

Key constants appear in `SubModule.cs` (naming may vary slightly): `OverBudgetStreakCap`, `HotEnableStreak`, `SpikeRunMs`, `SpikePausedMs`, `FlushOnHugeFrameMs`, `AllocSpikeBytes`, `WsSpikeBytes`, `ForceFlushAllocBytes`, `ForceFlushWsBytes`.

---

## Verification for non‑interactive agents
You cannot play or launch the game. Use these checks instead:
1. **Compile‑only safety:** Build the solution with the existing TFM and refs.
2. **Reflection guard audit:** Ensure all patchers use `SafePatch(...)`, skip property/abstract/generic targets, and check `MethodBody != null` before patching.
3. **Harmony presence:** `CreateHarmony()` must tolerate missing `0Harmony`. If absent, log once and continue with frame/GC logging only.
4. **Hot path purity:** Confirm hot‑path helpers are `AggressiveInlining` and allocation‑free; keep logging behind cooldown flags.
5. **Map throttle logic:** Check `MapScreenOnFrameTickPrefix(...)` and postfix paths do not regress reentry flags (`_skipMapOnFrameTick`, `_mapScreenFastTimeValid`, `_mapHotGate`).
6. **Slicer isolation:** `PeriodicSlicer.Pump(msBudget: …)` must run regardless of pause to avoid backlog cliffs; budgets remain small and bounded.
7. **Threshold integrity:** Maintain sensible ordering for all thresholds, and keep configurable ratios if you tune values.
8. **Log rotation:** Ensure `MapPerfLog` rotates and does not spam. Summaries over per‑frame logs.

If you add code, ship compile‑time checks and focused assertions. Do **not** claim manual play‑testing.

---

## Coding guidelines
- Prefer branch‑predictable, allocation‑free hot paths. Use `Volatile`, `Interlocked` for cross‑thread counters.
- Use `Stopwatch.GetTimestamp()` and a cached `TicksToMs` multiplier.
- Guard logs with cooldowns; never per‑frame spam.
- Apply Harmony prefix priority to gating prefixes when available.
- Avoid touching compiler‑generated and generic/abstract methods.

---

## Common tasks for agents
- **Tune thresholds:** Adjust `MapHotDurationMsThreshold`, alloc thresholds, and spike limits while preserving ratios and order.
- **Extend hotspots:** Curate name filters in `MapScreenFrameHooks` and hotspot probes for new hot areas.
- **Improve slicing:** Evolve `PeriodicSlicer` budgets/queues without changing gameplay semantics.
- **Discovery reliability:** Improve `DiscoverSubscriberActions(...)` and alias matching (`MatchesMethodAlias`, `GetZeroParamMethod`).
- **Configurability:** Optional tiny config (env or JSON) to override thresholds at runtime. Defaults must be safe.

---

## Logging and observability
- Prefer **summaries** and periodic flushes over per‑frame logs.
- Use cooldowns: `_frameSpikeCD`, `_memSpikeCD`, `_nextFlush`, `_nextRootBurstAllowed`.
- Trigger `FlushSummary(force: true)` on large spikes with guardrails.

---

## Minimal file map
- `MapPerfFix/SubModule.cs` – main mod logic, patching, probes, thresholds.
- `MapPerfFix/MapPerfLog.cs` – rotating log helper.
- Other helpers by class name (e.g., `MapFieldProbe`, `PeriodicSlicer`). Search by type name before adding new files.

---

## PR style
- Small, surgical, reversible changes.
- Keep public surface stable.
- **Commit prefixes:** `feat(throttle): …`, `perf(hotspot): …`, `fix(patch): …`, `chore(log): …`.
- Add a 1–3 line rationale and a short risk note.

---

## Glossary
- **Over‑budget frame:** Frame exceeds budget × headroom.
- **Hotspot:** Method identified as slow or alloc‑heavy in map processing.
- **Throttle:** Skip some map frame hooks under pressure.
- **Slice:** Defer periodic heavy work across frames.

---

## Safety checklist
- [ ] No new allocations in hot paths (`MapHot*`, `OnApplicationTick`, prefixes).
- [ ] Patches tolerate missing/renamed methods; logs are one‑shot and terse.
- [ ] Cooldowns prevent spam.
- [ ] Streaks/timers reset on pause or when leaving the map.
- [ ] Threshold ordering remains coherent (warn < flush; alloc < force‑flush).

> Note: This document avoids any assumption of human gameplay or manual testing. Agents must rely on compile‑time checks, static review of guards, and bounded instrumentation changes only.
