# W69 checkpoint — source-bound quest execution, dense-pack isolation, log reconciliation, aura/breath safety

Date: 19 September 2026  
Repo: `jeofwong/CopilotBuddy-private`  
Draft PR: #51  
Branch: `audit/next-55-equipment-observation-20260917`

## Verified head

Verified code: **0640174f5b128ea466059c30493702a00307a478**  
Verified tree: **6e2fca4209af9be109907e1ce5d902d303359040**

Exact-head validation:
- integrated Windows x86 run **35423774281**, artifact **10578017430**, conclusion **success**
- host validation run **35423774279**, artifact **10578222288**, conclusion **success**

Do not merge PR51 without explicit user approval. Master remains a separate approved frontier.

## What W69 adds

### 1. Authoritative UseItemOn liveness and source-bound execution

The W68 authoritative item semantics are retained. W69 additionally:
- bounds the post-submission acknowledgement wait separately from submission count,
- waits for delayed authoritative progress without turning timeout into success,
- defers when a submitted item disappears after the bounded acknowledgement window,
- preserves legacy `InvocationCount` behavior,
- binds `quest_strategies.json` to runtime execution identity while preserving legacy dataset identity when no pack exists,
- wires only source-bound `UseItemOn` recipes into generated Wholesome profiles,
- carries quest/objective/item/target/state/range/LOS/max-attempt/success-evidence facts into the generated behavior.

Retained strategy execution group: **9/9**. GossipEvent and Escort are still deliberately non-executable.

### 2. Dense-pack ranged pull isolation

A new LevelBot-owned `PullIsolationCoordinator` plus optional routine contract `IIsolationPullProvider` isolates ordinary open-world dense-pack pulls without creating a second rotation engine.

First supported provider is normal-world Retribution Paladin:
- routine owns the ranged opener (Exorcism),
- LevelBot owns pack-risk geometry, safe approach, retreat anchor and retreat lifetime,
- no Hand of Reckoning/taunt opener,
- no melee-close behavior in the isolation opener,
- dungeon/PvP/player/elite/transport/group/ranged-caster cases are not claimed,
- retreat ends when target separates, reaches close combat, ownership changes, or extra aggro appears.

Retained pull-isolation group: **12/12**. Host and integrated exact-head validation are green.

### 3. Runtime-log reconciliation

Repository `runtime-logs` contains **62** captured old-EXE logs. Large logs were re-read by Git blob rather than relying on the normal content helper.

Major reconciled signatures:
- old `ForcedBehaviorExecutor` / unexpected-iterator storm: later lifetime/cleanup repairs and retained regression coverage exist;
- old Ret instance NRE storm: stacks are dominated by missing/replaced target reads in `IsBoss` and Ret cast predicates; current Ret code has null/identity fencing and retained decision tests;
- `ManaPer5Sec` warnings: current source aliases it to `ManaRegeneration` and retains 20/20 weight-alias tests; warning is old-binary evidence so far;
- aura OOM in `WoWUnit.GetAllAuras()`: current source risk was real and is now bounded before allocation;
- separate Roslyn/plugin OOM: **not the aura bug**. In `2026-09-12_1232_48388.log`, eight OOMs occur in `MetadataReference.CreateFromFile` during `PluginManager.RefreshPlugins`. The same log contains multiple successful refreshes first. Current plugin refresh recompiles source plugins into unique default-context assemblies; repeated-refresh memory ownership remains open;
- navigation/stuck, transport and pulse/plugin latency remain mixed/open and must not be relabelled solved from source inspection alone.

### 4. Aura allocation safety

Log-backed red `fa394380...` established the missing raw-count guard without performing an oversized allocation. Production `0b44b000...` now:
- accepts resolved aura counts 0..255,
- rejects unresolved/corrupt counts before `AuraInfo[]` allocation,
- treats a disappearing/non-world object separately from an otherwise-valid corrupt observation.

The 255 ceiling is original-client-compatible evidence: AzerothCore WotLK documents `MAX_AURAS 255` as the client limit.

Retained aura-count group: **8/8**.

### 5. Underwater breath recovery

Test-only `743f96e...` reproduced the legacy `Math.Min(travelTime, 30.0)` cap. Production **0640174f...** now centralizes `RequiredBreathLeadMilliseconds`:
- short routes retain a 30-second safety floor,
- long routes retain the full distance/speed safety-adjusted travel time,
- zero/nonfinite speed/distance fails closed by requesting immediate recovery.

Retained CollectThings breath group: **7/7**.

This is not complete underwater support: depth, obstacle, alternate-air-source reachability, shoreline selection and live-client acceptance remain open.

## Existing work clarified rather than recreated

- PallyPower: current Paladin support already contains a read-only, version-pinned v3.2.21 Wrath assignment bridge with deterministic blessing/aura tests. Live addon/roster acceptance and broader all-class effect-strength policy remain open.
- Carbonite/addon evidence: offline inventory/quarantine and WorldMapArea candidate XY conversion exist. Raw Carbonite-zone decoding, exact installed-source/licence verification, Z/terrain/path authority and runtime consumption remain open by design.
- Equipment: retained tests cover unknown observation, empty-slot semantics, two-hand/off-hand displacement and authoritative reward identity. Class proficiency, unified loadout policy, hit/expertise/other caps and optimal reward valuation remain open.
- GatherBuddy: post-gather patrol already routes through `Flightor.MoveTo`, whose current owner handles ground/flying remount recovery. Do not add duplicate remount logic without fresh evidence. Full GatherBuddy/rest/vendor/remount ownership remains broader and open.
- Escort: the tracked Escort behavior has 47-case controlled coverage, but the current strategy recipe lacks explicit start interaction, completion mode/destination, item/timer start semantics and bounded reacquisition facts. Automatic Wholesome Escort remains unwired.
- GossipEvent: recipe identity is closer to sufficient, but direct legacy `InteractWith` completion is still partly local-count based. Add an authoritative submission/acknowledgement lifetime before automatic strategy ownership.

## Stream/connector recovery rule

For GitHub/Actions connector polling failures:
1. persist/re-read PR head SHA and workflow run IDs;
2. use short one-shot status reads with bounded retries rather than long stream polling;
3. if a tool read times out, resume the same run ID/SHA; do not republish code or blindly rerun;
4. only rerun a workflow when GitHub reports a real cancellation/failure requiring rerun;
5. if the entire ChatGPT turn is terminated by the platform, a new user turn is still required, but this checkpoint is the recovery source.

## Still open

Continue the broader audit without relabelling controlled tests as total support:
- authoritative Gossip/Event/Escort strategy execution;
- plugin refresh/default-context assembly lifetime and the Roslyn OOM signature;
- all-class stronger/equivalent/exclusive buff policy and legitimate ownership transitions;
- unified class/proficiency/loadout/cap-aware equipment and active reward valuation;
- raw permitted Carbonite/Questie adapters, map/floor provenance, terrain/Z/path validation;
- complete underwater depth/air/obstacle/shoreline recovery;
- full GatherBuddy/rest/remount/vendor ownership;
- remaining navigation/stuck/transport/performance signatures;
- native UI/slot/cursor/LOS and other original-client ABI acceptance;
- independent review plus original-client/TrinityCore/AzerothCore live acceptance.

No master merge, deployment or installed-binary replacement is claimed.
