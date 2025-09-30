# AGENTS.md

> Guidance for AI code assistants (e.g., ChatGPT / “codex” agents) working in this repository.

## Project snapshot
- **Repo purpose:** Runtime instrumentation + micro-optimizations for the **Mount & Blade II: Bannerlord** campaign map, focusing on frame-time stability and reduced GC/alloc pressure.
- **Primary module:** `MapPerfFix/SubModule.cs` (namespace `MapPerfProbe`) – an `MBSubModuleBase` that patches game methods via **Harmony** and logs/mitigates hotspots.
- **Key dependencies:** HarmonyLib, TaleWorlds (Bannerlord) assemblies.
- **Language:** C#.
- **High-level features:**
  - GC latency tuning (switches modes when map is paused vs running).
  - Throttling of `MapScreen` frame hooks under load.
  - “MapHot” lightweight hotspot instrumentation & logging with per-method cooldowns.
  - Periodic work slicing of daily/hourly events (`PeriodicSlicer`).
  - Frame/memory spike detection with summaries + cooldown-based logging.
  - Adaptive “over-budget” gating based on measured frame-time streaks.

---

## Where to start (entry points & invariants)
- `SubModule.OnSubModuleLoad()` – safe patching bootstrap. **Do not** throw: use `SafePatch(...)` and catch broadly.
- `OnApplicationTick(float dt)` – frame-time/GC/memory tracking, spike detection, adaptive thresholds, and `PeriodicSlicer.Pump(...)`.
- **Throttle:** `PatchMapScreenThrottle(...)` + `MapScreenOnFrameTickPrefix(...)` – gate `MapScreen` frame hooks.
- **Hotspot probe:** `MapHotPrefix(...)` / `MapHotPostfix(...)` – cheap duration/alloc capture guarded by `_mapHotGate` and per-method cooldowns.
- **Type patchers:** `TryPatchType(...)`, `PatchMapScreenHotspots(...)`, `PatchBehaviorTicks(...)`, `PatchCampaignCoreTicks(...)`, `PatchDispatcherFallback(...)`.
- **Subscriber discovery:** `DiscoverSubscriberActions(dispatcher, methodName)`.

**Important invariants**
- **Never block** or allocate in tight paths; prefer `readonly struct`, caches, `AggressiveInlining` on tiny helpers, and branchy logging with cooldowns.
- Skip patching property/abstract/generic/special-name methods (already enforced; keep it that way).
- Hotspot logging must honor `_mapHotGate` and per-target cooldowns (`MapHotCooldowns`).
- Catch and log; keep the game running even if a patch fails.

---

## Adaptive frame budget (for agents)
This repo uses an “over-budget streak” to enable hotspot gating. Targets vary by user refresh rate. If adding logic that depends on frame cadence:
- Prefer **adaptive** budgets learned from measured `frameMs` and **snap** to common vsync periods when close (30/48/50/60/72/75/90/100/120/144/165/…).
- Keep headroom (e.g., +20%) before considering a frame “over budget.”
- Reset streaks when not on map or when paused.

The constants you will see around this:
- `OverBudgetStreakCap`, `HotEnableStreak`
- `SpikeRunMs`, `SpikePausedMs`, `FlushOnHugeFrameMs`
- Memory thresholds: `AllocSpikeBytes`, `WsSpikeBytes`, `ForceFlushAllocBytes`, `ForceFlushWsBytes`

When tuning, **keep the relative order** and intent (spike > flush thresholds, etc.).

---

## Build & run (for agents)
> Exact game/SDK versions can vary between users. Keep changes non-assumptive and configurable.

- **Target framework:** Use the project’s existing target. If creating new projects, prefer matching whatever the repo already uses (e.g., net472/net48 for classic mod templates, or the game’s current target). **Do not change targets** unless asked.
- **References:** TaleWorlds assemblies (and Harmony) must resolve from the user’s Bannerlord install. Prefer **relative paths** or a single env var like `BANNERLORD_DIR` (agents may add a `Directory.Build.props` with a property for this if missing).
- **Output:** Mod DLLs typically land in a `bin/` folder the user can copy into `Mount & Blade II Bannerlord/Modules/<YourModule>/bin/Win64_Shipping_Client/` (paths can vary). Avoid hard-coding.
- **No external network calls** and no runtime dependencies beyond shipped DLLs.

**Testing (manual)**
- On map (running vs paused) for 30/60/120/144/165 fps scenarios.
- Toggle “fast time” and verify throttle + hotspot behavior (lower logging while paused).
- Induce allocated memory spikes to ensure cooldowns + summary flushes work but remain bounded.
- Watch logs: `MapPerfLog.Info/Warn/Error` lines should remain sparse and informative.

---

## Coding guidelines
- Prefer allocation-free, branch-predictable paths; rely on `Volatile.Read/Write`, `Interlocked` where cross-thread counters are used.
- Use `Stopwatch.GetTimestamp()` with `TicksToMs` for fast timing; avoid `DateTime` in hot paths.
- Keep logging guarded by cooldowns; **never** log every frame.
- Use `AggressiveInlining` on micro helpers like predicate/name-matching if they’re hit often.
- Maintain **best-effort** and **fail-open** philosophy: if a patch fails, the game should keep running.
- When adding new patches:
  - Give Harmony prefix methods highest priority (set `priority` where supported).
  - Skip special-name methods and compiler-generated types (`<>`, `DisplayClass`, `d__`, `MoveNext`).
  - Validate `MethodBody` existence and avoid generic/abstract targets.
- Respect existing cool-down fields and prune windows (e.g., `MapHotCooldownPruneLimit`, `MapHotCooldownPruneWindowMultiplier`).

---

## Common tasks for agents
- **Tune thresholds**: adjust `MapHotDurationMsThreshold`, alloc thresholds, and spike limits while preserving intended ratios.
- **Extend hotspots**: add/curate name filters in `Hit(...)` and `MapScreenFrameHooks` for newly-identified hot areas.
- **Slicing improvements**: evolve `PeriodicSlicer` budgets/queues w/o affecting gameplay correctness.
- **Discovery reliability**: improve `DiscoverSubscriberActions` to catch more zero-arg handlers using `MatchesMethodAlias(...)` and `GetZeroParamMethod(...)`.
- **Make behavior adaptive**: e.g., compute frame budgets from observed data, but keep a small slack and snap-to-vsync behavior.
- **Configurability**: (optional) add a tiny config reader (JSON or env-vars) to override thresholds without code changes. Keep defaults safe.

When performing any of the above, add **focused** unit/integration points only if practical and zero-risk. Otherwise, leave as manual test instructions.

---

## Logging & observability
- Prefer **summaries** on spikes and periodic flushes over spammy per-frame logs.
- Use the existing cooldowns: `_frameSpikeCD`, `_memSpikeCD`, `_nextFlush`, `_nextRootBurstAllowed`.
- Large spikes should trigger `FlushSummary(force: true)` (with guardrails).

---

## File layout (minimum you need)
- `MapPerfFix/SubModule.cs` – main mod logic, patching, probes, and thresholds.
- Other files may provide small helpers (e.g., `MapFieldProbe`, `MapPerfLog`, `PeriodicSlicer`). If you reference them, search by class name first.

---

## Style & PR expectations (for agents committing changes)
- Keep changes **small, surgical, and reversible**.
- Maintain existing public API surface; avoid breaking exported types.
- **Commit message template:**
  - `feat(throttle): …` for new throttling capabilities
  - `perf(hotspot): …` for hotspot/alloc reductions
  - `fix(patch): …` for patch safety/compatibility
  - `chore(log): …` for logging/cooldown tweaks
- Include a 1–3 line rationale + risk notes in the commit body.

---

## Glossary
- **Over-budget frame**: a frame whose duration exceeds the learned/snapped frame budget × headroom.
- **Hotspot**: a method identified as slow or alloc-heavy within map frame processing.
- **Throttle**: temporarily skipping some `MapScreen` frame hook calls to avoid cascade work during fast time or budget pressure.
- **Slice**: deferring periodic heavy work across frames instead of running all at once.

---

## Safety checklist before submitting a change
- [ ] No added allocations in the hottest paths (`MapHot*`, `OnApplicationTick`, prefixes).
- [ ] Harmony patches are robust to missing/renamed methods (best-effort, with logs).
- [ ] Logs are behind cooldowns and won’t spam.
- [ ] Streak counters and timers reset appropriately when paused or leaving the map.
- [ ] Spike thresholds maintain sensible ordering (warn < flush).

If something is ambiguous, prefer a conservative default and leave a short comment with the reasoning.
