# W72 checkpoint — exact negative-parent status + raw failed quest observation

Date: 19 September 2026
Repo: `jeofwong/CopilotBuddy-private`
Draft PR: #51
Branch: `audit/next-55-equipment-observation-20260917`

## Verified head

Verified code/test head: **7a471da4d674ca1dffdc6169340b091e96a36e46**
Verified tree: **3bafec33ca952646ff1093f2a4a84d9a82c9e1bf**

Exact-head validation:
- integrated Windows x86 **35431678604**, artifact **10580349205**, SHA256 `87845c7744dfd9d2f34bef922b08d11857966dae7b94cf93de2286c2311d61af`, success
- quest-log owner Windows x86 **35431678616**, artifact **10579879846**, SHA256 `947d2e289ef8d6515d03df30774cbe0fa21be0bf27130b86bc0b8d35006e0d61`, success
- host Windows validation **35431678735**, artifact **10580898437**, SHA256 `c5ed190674d58fceb2426e70c611faacd6de8cdce0e9fa96c224e29bf3899347`, **0 errors / 3340 warnings**

Integrated result is **17/17 entries**. Selected retained groups:
- active parent prerequisites **39/39**
- dependent previous alternatives **14/14**
- negative exclusive dependencies **10/10**
- container slot identity **9/9**
- Singular required registrations **189/189**
- quest-log observation **27/27**
- raw-ready owner **16/16**

Do not merge PR51 without explicit user approval.

## TC-primary negative direct PrevQuestID status

Pinned TrinityCore 3.3.5 **8fda442f6c30ca21a622638063ab8b28376f1b25** requires a negative direct `PrevQuestID` parent to have `QUEST_STATUS_INCOMPLETE`. A ready/completed or failed quest does not satisfy that direct prerequisite.

Pinned AzerothCore WotLK **8337a378ac325e62a6a91e00c6a5e944205e8536** is broader and accepts non-`NONE` previous status. This remains an explicit compatibility difference. The scheduler follows the repository's TC-primary policy rather than silently guessing the source core from a realm or dataset name.

Original 3.3.5 UI archive **d0339b17b0221db76e6acd2dc2915d224a5b62ca** independently exposes failed quest state: stock `WatchFrame.lua` treats `GetQuestLogTitle(...).isComplete < 0` as not complete. The host player descriptor already represents `Completed = 1` and `Failed = 2`.

## Test-first history

Initial test-only **00ddb5aaed33eb5f8b4cb32dfe1c72ee8e79f831** changed only the active-parent fixture:
- integrated **35431251731**, artifact **10580279009**
- ActiveParent **30/34**, **4 intended assertions**, **0 unexpected**
- failures were ready/completed negative parents, with and without old rewarded history
- host build was green

Strengthened test-only **57ca3e177d07a525bacefa3b40df51a73daeb632** added failed-parent controls and raw failed-status ownership:
- integrated **35431490715**, artifact **10579854779**, SHA256 `1f8bd87416c9474fe1110d874c9e14097bf21222c94a6fb383ad6aca5e7b666d`
- ActiveParent **30/39**, **9 intended assertions**, **0 unexpected**
- quest-log owner **35431490687**, artifact **10580443591**, SHA256 `bffeb854247259e6e966beda2415295bef45dba75ad6d524a4a455ad78f1d2f2`
- QuestLogObservation **26/27**, with the only failure being the missing raw failed-ID snapshot contract
- failed-bit snapshot freshness already passed, so the missing behavior was exposure/wiring rather than raw change detection

Production **7a471da4** changes only:
- `Styx/Logic/Questing/QuestLogSnapshot.cs`
- `runtime-snapshot/Bots/WholesomeAutoQuest-master/QuestScheduler.cs`

## Implemented status contract

`QuestLogSnapshot` now keeps immutable:
- `AcceptedQuestIds`
- `ReadyQuestIds` from raw Completed bit
- `FailedQuestIds` from raw Failed bit

The same raw observation bytes remain subject to existing owner/freshness revalidation. No new native read, server call or invented status epoch was added.

`QuestSchedulerAcceptedQuest` now carries `IsFailed`. The live scheduler maps `observation.FailedQuestIds` into that accepted state.

Negative direct parent admission now requires:
- signed parent is representable,
- parent is currently accepted,
- parent is **not completed/ready**,
- parent is **not failed**.

Old rewarded history does not override a current ready or failed state. Accepted incomplete repeat parents still work. Positive direct and dependent-previous semantics from W71 remain unchanged.

Final green:
- ActiveParent **39/39**
- QuestLogObservation **27/27**
- RawReady **16/16**
- all 17 integrated entries pass

## Live offer remains final server-visible acceptance gate

The scheduler still does not reconstruct every server-only class/skill/reputation/breadcrumb/seasonal/condition rule from local JSON. `ForcedQuestPickUp` remains the final live gate: it reads gossip/native offered quest IDs, rejects a loaded list missing the target, confirms target quest identity before `AcceptQuest`, and bounds repeated missing-offer evidence.

This can still allow wasted travel to an unavailable quest. It does not authorize accepting a wrong or unoffered quest.

## Next native cursor frontier

Do not blanket-convert all cursor callers.

The first safe shared slice should be **single-item pickup only**:
- current `WoWItem.PickUp()` still derives `BagIndex + 1` and `BagSlot + 1` independently
- reuse the verified GUID/slot resolver and last-moment GUID revalidation from safe container use
- require an **empty cursor** before pickup; original 3.3.5 provides `GetCursorInfo()`, so non-item cursor payloads must also fail closed
- Lua must recheck expected item entry before `PickupContainerItem`
- after pickup, original 3.3.5 `CursorHasItem()` is a compatible acknowledgement primitive
- do not clear or replace a caller-owned cursor implicitly
- same-entry ABA between managed revalidation and Lua remains a native limitation

Caller-specific follow-up is separate:
- `DeleteItems` and MrItemRemover: destructive deletion/confirmation lifecycle
- `EquipItem` and AutoEquip: equip slot + bind confirmation + final equipped-item acknowledgement
- AuctionHouse owners: sell-slot/post acknowledgement
- ProfessionBuddy bank/mail/stack/AH: intentional multi-step cursor transactions; never route these through a single-item helper blindly

Original 3.3.5 deletion evidence:
- `DELETE_ITEM_CONFIRM` shows `DELETE_ITEM` below quality 3 and `DELETE_GOOD_ITEM` at quality 3+
- both popup accepts call `DeleteCursorItem()` again
- `DELETE_GOOD_ITEM` requires the confirmation edit box to match `DELETE_ITEM_CONFIRM_STRING`
- popup cancellation clears the cursor
Therefore a current one-shot `DeleteCursorItem()` invocation is not deletion acknowledgement.

## Other open frontiers

Retain all W71/W70 open items, including:
- positive ExclusiveGroup repeatable/daily/weekly/seasonal semantics
- class/skill/reputation/breadcrumb planning optimization
- all-class buff strength/ownership
- equipment proficiency/loadout/caps/reward valuation
- addon terrain/floor/Z/path authority
- underwater/depth/air/shoreline
- GatherBuddy/rest/vendor/remount ownership
- native cursor/LOS/ABI boundaries
- Escort and event-chain recipes
- independent review and supervised original-client/core acceptance

For ordinary GitHub/Actions stalls: recover by exact head SHA + run ID, short one-shot reads and bounded retries; no blind reruns or recreated work.
