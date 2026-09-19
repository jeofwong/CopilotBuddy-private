# W67 — provenance, complex quest dependencies, addon bridges and reward identity

19 September 2026. Repository `jeofwong/CopilotBuddy-private` (1367174964), draft PR51, branch `audit/next-55-equipment-observation-20260917`.

## Reconciliation

W66 root pointers were stale. This continuation re-read the live branch before writes and reviewed the intervening commits rather than recreating them. The verified code frontier is **0c918908d0b6372c1b8b20359982e037c87a635b**, tree **daf4476e9836c8aaa4d3199c12d6fb0de760fef2**. Master remains **f462a9bb4eb18acac9069f495177df35672286d5**.

Earlier W63–W66 protections remain requirements and evidence. Nothing below turns a narrow passing group into total quest, addon, buff, equipment, movement or server support.

## Merchant pending-sale liveness

Test-only `85abc00a` reproduced persistent locked/malformed sale observations: 7/12 pass, 5 intended assertions, 0 unexpected. `MerchantSaleAttemptGate` now polls a pending observation no more than once per second and bounds one no-progress episode at ten seconds with result4 deferral. Progress/terminal outcomes reset the pending episode; cancellation identity remains preserved. The older locked-recovery control was moved to the one-second observation boundary rather than removed.

This is not merchant acknowledgement and does not solve post-submission result loss, physical slot/cursor identity or every bulk-sale outcome.

## Dataset provenance and identity

The quest dataset now has an optional strict `quest_data.provenance.json` boundary. With no sidecar, source identity remains explicitly **Unknown**. A declaration is accepted only for original client build12340, an explicit supported source label (`trinitycore-3.3.5` or `azerothcore-wotlk`), complete revision/exporter fields, an exact quest-data SHA256 and exact schema fields. No provenance sidecar was added to the retained dataset and its origin is still unknown.

A mutation check caught a coordinated fingerprint-format drift introduced during this work. The pre-provenance v2 fingerprint representation is pinned and restored; provenance validation uses its own strict SHA representation. Existing recovery identities therefore do not reset merely because provenance support exists.

## Complex prerequisite/exclusive-group semantics

Pinned TrinityCore3.3.5 and AzerothCore evidence was used only where the shared contract is clear.

- Positive `ExclusiveGroup > 0`: sibling alternatives are excluded from pickup when a sibling is already accepted/rewarded, and the scheduler does not select conflicting siblings in the same materialization.
- Negative exclusive groups: when an explicit rewarded predecessor belongs to `ExclusiveGroup < 0`, all members of that group must be rewarded before the downstream pickup is admitted.
- Signed negative `PrevQuestID` remains the previously established **active-parent** rule and is not rewritten into a rewarded-group rule.

The retained JSON exporter/provenance is unknown. Therefore this work does **not** globally reinterpret `PreviousQuestsIds` as TrinityCore's complete OR-list semantics. That remains a provenance/importer question.

## PallyPower read-only assignment bridge

The optional `UsePallyPowerAssignments` setting defaults off. When Singular's blessing/aura setting is Auto, the bridge reads PallyPower only if:

- the addon is loaded,
- `GetAddOnMetadata('PallyPower','Version') == 'v3.2.21'`,
- `PallyPower.IsWrath == true`,
- the explicit Wrath assignment tables are readable.

It translates the reviewed Wrath slots to named blessing/aura families, including Sanctuary and the resistance auras, maps Death Knight to class index10, respects per-player normal overrides, and never writes PallyPower data. Unknown version/flavor/malformed state defers instead of interpreting raw integers. Addon absence retains the normal Singular Auto policy; explicit Singular manual settings retain precedence.

This is assignment integration, not all-class effective-rank optimization.

## Carbonite/map hints

The evidence tool now supports strict WorldMapArea-style XY conversion only for already quarantined build12340 search hints with a source-bound bounds manifest. It preserves point/box shape and source namespace, does not invent Z/floor/reachability/safe ground, and outputs `search-hint-only` / `runtime_enabled=false`. Carbonite raw payload, decoder and restricted database content are not published.

This is geographic candidate conversion, not runtime navigation or quest strategy authority.

## Source-bound special strategy packs

`QuestStrategyPackLoader` validates optional source-bound build12340 recipe packs for `UseItemOn`, `GossipEvent` and `Escort`, tied to the exact quest-data SHA and source revision. Recipes require explicit quest/objective, target identity/type, range/LOS, bounded attempts and authoritative success evidence (`ObjectiveProgress` or `QuestComplete`). Local invocation count and escort destination arrival are not accepted as quest credit.

**No scheduler/profile/executor currently consumes these recipes.** This is an authority/data boundary only. Automatic special-item/gossip/event/escort execution remains open.

## Quest reward identity

Test-only `d27b241a` produced a clean reward-observation red from retained integrated artifact **10574058976**, SHA256 **d53e4cafe2b67f9fb3aa53cb777f57ff5a081ece8473c72cc15139c8803ece90**: reward group **0/12**, 12 intended assertions, 0 unexpected; retained prior groups passed in that artifact.

Production `ActionSelectReward` now observes the bounded live original-client choice set using `GetNumQuestChoices`, `GetQuestItemLink('choice',...)` and live stack counts. Any missing/malformed identity invalidates the whole observed set; it no longer clicks `QuestInfoItem1` as an arbitrary fallback. Scoring and sell-value fallback operate only over that complete live set. A failed observation returns Failure, and the parent `DecoratorContinue` propagates that failure so `CompleteQuest` is not reached.

Final integrated run **35412850647**, artifact **10575210442**, SHA256 **96e49a57538d6abada13c62ea8d3aedbd65d03654bdf0c95f4e8ba8b39b3684b**: completed **success** on exact head `0c918908d0b6372c1b8b20359982e037c87a635b`. This reruns all retained integrated owners, including the reward12 contract, strategy pack, provenance, dependency, merchant, PallyPower and earlier groups. Host run **35412850625**, artifact **10575030460**, SHA256 **afbed4763aa50e841bc6efbfbb69758763d0ff886942489b58c57cf598a0b23a**, completed **success**, compile only. The accessible job log did not expose a stable warning-count summary, so none is invented here.

## Explicitly still open

1. Wire source-bound special recipes into scheduler/profile/executor ownership with authoritative progress acknowledgement; first safe slice should be UseItemOn only. Gossip and Escort need separate interaction/acknowledgement lifetimes.
2. PallyPower is read-only Paladin assignment integration only. All-class stronger/effective-rank and mutually-exclusive family policy remains open; unknown strength must remain unknown.
3. Carbonite/map conversion remains quarantined search hints only; floor/Z/terrain/path validation and runtime enrichment remain open.
4. Equipment still has multiple scoring owners. Cap/loadout/set/proc/reward optimization and unified roll/equip/reward policy remain open. W67 reward work is identity safety, not DPS optimization.
5. Aquatic tests establish conservative liquid observation and rest/consumable admission only. Breath/fatigue, obstacle-aware escape, safe shoreline/air selection and live recovery are unimplemented.
6. Full GatherBuddy/LevelBot/rest/remount ownership after combat, stun, gathering, repair/vendor and recovery remains open.
7. Native UI/slot/cursor/LOS freshness, post-submission acknowledgement, independent review, representative performance, and supervised original-client/server acceptance remain open.

Read `docs/audit/WOTLK_335A_RESEARCH_POLICY.md`, `TRINITYCORE_335_COMPATIBILITY.md` and `QUEST_DATA_PROVENANCE_335.md` with this checkpoint.
