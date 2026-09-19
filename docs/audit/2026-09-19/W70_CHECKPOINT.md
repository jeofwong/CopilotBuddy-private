# W70 checkpoint — plugin refresh OOM reuse + authoritative GossipEvent

Date: 19 September 2026
Repo: `jeofwong/CopilotBuddy-private`
Draft PR: #51
Branch: `audit/next-55-equipment-observation-20260917`

## Verified head

Verified code: **2456369563478b1f0c11c902649d7b308169ddfc**
Verified tree: **90501a9a3233d9bd043621abf2ae5671c658cf05**

Exact-head validation:
- integrated Windows x86 run **35425658391**, artifact **10578078516**, success
- host validation run **35425658309**, artifact **10578774562**, success
- quest-log owner run **35425658361**, artifact **10578159821**, success

Do not merge PR51 without explicit user approval.

## W70 additions

### Repeated plugin-refresh OOM source repair

Old-EXE log `2026-09-12_1232_48388.log` contains repeated same-process plugin refresh and 8 OOMs in Roslyn metadata-reference creation. This is distinct from the earlier aura-reader OOM.

Production `PluginManager` now fingerprints the exact C# + .resx source inputs and reuses the successfully compiled plugin type set when an unchanged source plugin is refreshed. Each refresh still receives fresh plugin object instances. Changed source recompiles. Failed changed-source compile leaves the prior valid cache untouched. Source mutation during compile is never published as reusable. Partial type sets caused by constructor failure are not cached.

Retained plugin refresh group: **9/9**. This is source/controlled evidence, not live long-duration memory acceptance.

### Authoritative single-option GossipEvent strategy

Wholesome can now execute an exact source-bound `QuestStrategyKind.GossipEvent` only under a conservative first-slice contract:
- exact quest + zero-based objective ownership,
- creature target only,
- exact source-bound target ID,
- generated hotspot can be borrowed only when the generic objective target ID matches the gossip target ID,
- explicit XYZ is required,
- exactly one source-bound MobId is required,
- target discovery remains bounded around the source hotspot anchor,
- exact zero-based gossip option index,
- open gossip frame is revalidated against the exact interacted NPC via `UnitGUID('npc')`,
- bounded navigation, NPC wait, gossip-open wait and post-submission acknowledgement windows,
- authoritative success is objective-count increase or quest completion only,
- interaction count, menu closure, target disappearance and retry exhaustion are never quest success.

Generated defaults:
- AcknowledgementTimeout 5000 ms
- GossipOpenTimeout 3000 ms
- TargetWaitTimeout 30000 ms
- NavigationTimeout 120000 ms

Retained GossipEvent group: **14/14**. Complete tracked behavior compiles in the controlled harness; no NPC/gossip/live client is attached.

Escort remains deliberately non-executable. Its current recipe still lacks explicit start interaction, completion mode/destination, item/timer-start semantics and bounded reacquisition authority.

## Retained earlier W69 scope

Still green at W70:
- dense-pack isolation **12/12**
- aura-count safety **8/8**
- CollectThings breath recovery **7/7**
- quest strategy execution **9/9**
- plugin refresh reuse **9/9**
- GossipEvent strategy **14/14**
- equipment observation **65/65**
- equipment hand replacement **38/38**

UseItemOn remains source-bound with bounded authoritative acknowledgement. PallyPower read-only v3.2.21 Wrath assignment bridge remains retained. Carbonite/addon evidence remains quarantined and non-runtime-authoritative.

## Runtime-log reconciliation

All 62 captured logs were reviewed, including oversized blobs through Git blob reads.

Source-addressed signatures:
- ForcedBehaviorExecutor iterator storm
- Ret Paladin missing/replaced-target NRE storm
- ManaPer5Sec legacy alias
- WoWUnit aura-count OOM
- repeated unchanged-source PluginManager refresh compilation OOM

Still open/mixed:
- navigation/stuck/transport recurrences
- plugin/root latency
- live/original-client acceptance

## Newly confirmed next source risk: native container slot identity

`WoWItem.UseContainerItem()` currently submits:
`UseContainerItem(BagIndex + 1, BagSlot + 1)`.

`BagIndex == -1` is used both for backpack and for unresolved container identity; `BagSlot` also calls `BagIndex` again. A disappearing/moved item can therefore collapse unresolved observations to Lua bag/slot 0 or combine observations from different moments.

Prepared next test (not yet published at this checkpoint) requires:
- GUID-based resolution across backpack/bags,
- missing/zero/duplicate observations fail closed,
- chosen GUID slot revalidated immediately before Lua,
- Lua rechecks expected item entry before using the slot,
- public UseContainerItem no longer derives BagIndex and BagSlot independently.

Residual same-entry ABA/live cursor acceptance will remain a live/native limitation even after this source repair.

## Other open frontiers

- all-class stronger/equivalent/exclusive buff strength and ownership policy; current equivalences are scattered and generally name-based
- unified equipment class/proficiency/loadout/cap/reward valuation
- raw permitted Carbonite/Questie adapters, floor/Z/terrain/path authority
- complete underwater/depth/air/shoreline recovery
- full GatherBuddy/rest/vendor/remount ownership
- remaining native UI/slot/cursor/LOS boundaries
- Escort/event-chain recipes beyond single GossipEvent
- independent review and original-client/TrinityCore/AzerothCore live acceptance

## GitHub connector recovery

For normal connector/Actions stalls:
1. keep exact PR head SHA + workflow run IDs;
2. use short one-shot reads with bounded retries;
3. do not long-poll;
4. do not blind-rerun or republish;
5. treat temporary job-log BlobNotFound while a job writes as transient.

If the entire ChatGPT turn is terminated by the platform, a new user turn is required; W70 is the durable recovery source.
