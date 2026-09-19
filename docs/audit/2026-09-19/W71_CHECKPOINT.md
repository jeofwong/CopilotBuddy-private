# W71 checkpoint — safe container submission + TrinityCore dependent-prerequisite semantics

Date: 19 September 2026
Repo: `jeofwong/CopilotBuddy-private`
Draft PR: #51
Branch: `audit/next-55-equipment-observation-20260917`

## Verified head

Verified code/test head: **5db0de566cea515a21f3706fd7835b82dda9737a**
Verified tree: **f9b8a5cbb14ebad0a419ac1a53ff3ab04dafbad0**
Production prerequisite repair: **16acaaac108d817642993e48bfe25dc10958c298**.

Exact-head validation:
- integrated Windows x86 **35430867637**, artifact **10580233586**, SHA256 `3af129a679ad1a8a4a5c30089b5dcb1794056731f3d6f7ce9cb62a5dd12acf6f`, success
- host Windows validation **35430867668**, artifact **10580507599**, SHA256 `138a96cce762f8cfdd3fe71da22cd05dfa89765bb2b22c664f54d269476d17bb`, **0 errors / 3340 warnings**
- production quest-log owner validation on **16acaaac**: **35430209827**, artifact **10580646500**, SHA256 `8299023fb6fb63392bea04a4997449d7ad2e907480ded378fc5c681b410b809b`, success

Integrated result is **17/17 entries**, all build/run results zero. Selected retained groups:
- dependent previous alternatives **14/14**
- active parent prerequisites **32/32**
- negative exclusive dependencies **10/10**
- container slot identity **9/9**
- Singular required registrations **189/189**
- PluginManager refresh reuse **9/9**
- GossipEvent **14/14**

Do not merge PR51 without explicit user approval.

## Reconciled W70 -> W71 work

### Native container item use is now fail-closed

W70 identified that `WoWItem.UseContainerItem()` derived `BagIndex` and `BagSlot` independently and that unresolved `BagIndex == -1` could alias the backpack.

Current `WoWItem` now:
- resolves the captured item GUID across backpack and equipped bags through one shared resolver,
- rejects zero, missing and duplicate GUID observations,
- maps the resolved slot to original-3.3.5 Lua bag/one-based-slot coordinates,
- revalidates the same GUID at the selected slot immediately before submission,
- asks original-client `GetContainerItemLink` for the slot and checks the expected item entry before `UseContainerItem`,
- returns a boolean through `TryUseContainerItem()` so callers can distinguish refused safe submission from an invocation.

`UseItemOn` now consumes that boolean. A refused container submission:
- does not increment the local invocation counter,
- does not stamp authoritative acknowledgement time,
- is retried only inside a bounded local `SubmissionRefusalTimeout` window,
- then defers without fabricating quest credit.

Container slot identity remains **9/9**. The first exact-head integrated run at `2a945643...` hit the workflow's 25-minute job timeout while a retained Singular builder test was running; the unchanged-SHA rerun completed successfully. That timeout is not classified as a production failure.

This repair is **use-only**. `WoWItem.PickUp()` and cursor-owning delete/equip/auction/bank/mail/stack callers remain separate open contracts. Same-entry ABA and live client acceptance remain unresolved.

## TrinityCore 3.3.5 dependent-prerequisite repair

The governing target remains original WoW **3.3.5a build 12340**, TrinityCore **3.3.5 primary**, AzerothCore WotLK secondary.

Pinned TrinityCore 3.3.5 **8fda442f6c30ca21a622638063ab8b28376f1b25** shows:
- direct signed `PrevQuestID` is checked separately,
- `DependentPreviousQuests` is an **ordered OR**: any rewarded ordinary/nonnegative-group predecessor can satisfy it,
- if the first rewarded candidate belongs to a **negative ExclusiveGroup**, every member of that group must be rewarded; a missing group member fails that dependent gate immediately,
- positive `PrevQuestID` is also inserted into the core's derived dependent list, while the direct field still keeps its independent check.

The scheduler previously treated every `PreviousQuestsIds` entry as independently required. That could incorrectly block quests with valid alternative predecessors and could redirect ancestor correction toward an accepted alternative that was not actually required.

Test-first history:
- **7619bc0c** introduced the initial alternative regression: **4/10 pass, 6 intended assertions, 0 unexpected**
- **d0f4b8d0** strengthened source-order/core-shaped controls: **5/12 pass, 7 intended assertions, 0 unexpected**
- **2414e769** added fail-closed missing-predecessor-metadata controls: **7/14 pass, 7 intended assertions, 0 unexpected**, integrated artifact **10580931092**
- production **16acaaac** changed only `QuestScheduler.cs`; the new group went **14/14**, but one older ActiveParent fixture lacked metadata for its explicit required ID
- test-only **5db0de56** added that core-shaped metadata without changing any assertion; final integrated is green

Current behavior:
- direct positive `PrevQuestID` remains independently required,
- negative direct `PrevQuestID` remains a separate contract,
- `PreviousQuestsIds` is evaluated in stored order as the TrinityCore dependent alternative gate,
- a rewarded known ordinary/nonnegative predecessor satisfies the dependent gate,
- a rewarded known negative-group predecessor requires every locally known member of that group and fails immediately when a known member is not rewarded,
- a completed predecessor with missing metadata cannot authorize pickup; a later rewarded known alternative may still satisfy,
- ancestor correction now traverses only blocking prerequisite roots rather than every historical alternative.

The local runtime loader reads the aggregate `quest_data/quest_data.json`; however its provenance may still be unknown and it is not the realm SQL database. Complete negative-group membership is therefore source-correlated, not live-server-certified.

## Live offer remains the final pickup authority

Wholesome planning data still lacks complete server-side acceptance state such as class, skill, reputation thresholds, breadcrumb state, daily/weekly/monthly/seasonal lockouts and arbitrary quest-availability conditions.

The final `ForcedQuestPickUp` owner nevertheless reads the live original-client gossip/native available-quest lists, records the offered quest IDs, rejects a loaded list that does not contain the requested quest, and accepts only after target quest identity is positively confirmed. Missing-offer evidence is bounded by `QuestPickupMismatchTracker` rather than treated as success.

Therefore incomplete planning prerequisites can still cause unnecessary travel, but they are not permission to blindly accept an unoffered/wrong quest. Do not claim complete server prerequisite reconstruction.

## Next prerequisite discrepancy

Pinned TrinityCore 3.3.5 requires a **negative direct PrevQuestID** parent to have `QUEST_STATUS_INCOMPLETE`. A ready/completed-but-unturned-in parent is not sufficient.

Pinned AzerothCore WotLK **8337a378ac325e62a6a91e00c6a5e944205e8536** uses a broader non-`NONE` active test. Current Wholesome still accepts any quest-log presence for the negative direct parent, including a ready parent; retained tests currently encode that older behavior.

The client observation needed to distinguish these states already exists:
- `QuestLogSnapshot.AcceptedQuestIds`
- raw Completed flag -> `ReadyQuestIds`
- `QuestSchedulerAcceptedQuest.IsCompleted`

NEXT: test-first the TC-primary conservative intersection: accepted **and not ready** for a negative direct parent. Preserve the AzerothCore difference explicitly; do not invent source-core identity or silently switch semantics.

## Other open frontiers

- cursor ownership for `WoWItem.PickUp`, DeleteItems/MrItemRemover, EquipItem/AutoEquip, AuctionHouse and ProfessionBuddy transfer flows
- positive ExclusiveGroup repeatable/daily/weekly/seasonal semantics; current snapshot lacks sufficient lockout authority
- class/skill/reputation/breadcrumb/conditions as planning optimization inputs; live offer remains final authority
- all-class stronger/equivalent/exclusive buff strength and ownership
- equipment proficiency/loadout/caps/reward valuation
- raw permitted addon terrain/floor/Z/path authority
- full underwater/depth/air/shoreline recovery
- full GatherBuddy/rest/vendor/remount ownership
- remaining native LOS/cursor/ABI boundaries
- Escort/event-chain recipes beyond single GossipEvent
- independent review and supervised original-client/core acceptance

For GitHub/Actions stalls, resume by exact head SHA + run IDs, use short one-shot reads and bounded retries, and never blind-rerun or recreate already-landed work.
