# W78 — completed normal-objective restart admission, verified offline

Repository: `jeofwong/CopilotBuddy-private` (1367174964). Draft/unmerged PR51, branch `audit/next-55-equipment-observation-20260917`.

## Scope and coordinates

This continues W77, not a restart of the auction experiment. Focus remains Wholesome questing, navigation and Singular combat, original WoW3.3.5a/build12340 only. TrinityCore3.3.5 is primary; AzerothCore WotLK secondary. No desktop mouse automation, new unverified Lua API, merge, force push, deployment or installed-file changes. The full review after18 September2026 21:58 Malaysia/13:58 UTC (ancestor4c6b1b2e75af4dd0eaffde11d38ebbc84a21f4c0) remains open. Preserve every earlier requirement, evidence limit and backup/exclusion rule.

Entry documentation head: `62b990c976577467859a49d56a983d499df36fd5`.
**Verified code/test head: `44cfdca70a59f3cd660364748687782f23a2793d`.**
**Verified tree: `2085deefa367cf9bf2d65a1f660c8a50a15035b1`.**
Any subsequent checkpoint-only commit is not a new tested production revision. Reconcile live refs before writing.

## What was reproduced

UseItemOn and GossipEvent's ObjectiveProgress mode captured a new baseline on OnStart and required a further counter increase. A selected NPC/GameObject counter already at its required amount could therefore repeat after restart even while other quest work remained unfinished. Separately, completion observed during item-facing or gossip-menu setup did not prevent the pending submission.

The37-case fixture compiles the complete tracked UseItemOn/GossipEvent source without rewriting their methods. It uses the real CustomForcedBehavior constructors and OnStart, real TreeSharp ticks, real Quest.GetData packed-counter reads over explicitly allocated fixture bytes, controlled cached requirement arrays, and controlled external world/UI observations. It does not attach a game, execute native item operations, or simulate the complete server.

Cases cover all four raw counter slots, partial counts, existing progress acknowledgement below the final requirement, fresh reset/reacceptance, zero or missing objective metadata, missing quest, failed quest, nonpositive required counts, separate QuestComplete and legacy InvocationCount modes, no dispatch for an already-finished objective, and completion during item-facing/gossip-menu observation. Valid unchanged item/gossip dispatches remain positive controls.

## Test-first history, including the fixture mistake

1. Test-only `921c951118686176bad9218d95b8445f9871bfc5`: run35454576737/art10587463044, ZIP SHA256 `07aa57e85f466680a75eb3e3229af390c870222b0040882bd0a606ddea2bae4a`. Result0/37,0 assertions,37 unexpected NullReferenceExceptions in base WarnUnusedAttributes. The fixture had reused uninitialized owner objects before real OnStart, leaving base constructor state absent. This is NOT gameplay assertion red. CRC and all183 inner hashes verified; retain it as failed fixture evidence.
2. Test-only correction `5ccbce88041ba41fa4886988034cac7d185f905d`, treee1605a3f63d97a4a85edf2eb046fdb4a73c05f84: construct both real owners with explicit valid arguments and assert constructor admission. All37 expectations retained; production/workflows unchanged. Integrated35455019434/art10587603166, SHA256 `62e1971dc330de5344ad0653982f217ed31a8c64e1b7752edced97d6a777c4ce`: **23/37,14 intended assertions,0 unexpected**, all16 other integrated entries passing. CRC/all183 inner hashes verified.
3. Production `44cfdca70a59f3cd660364748687782f23a2793d`: **37/37,0 assertions,0 unexpected**, integrated17/17. No fixtures/assertions/workflows changed after the clean red.

The preliminary unreferenced candidate53312c130a0d5adcbdc4f909839c108a15d78c51 contained an unrelated diagnostic-label edit. Diff review caught it BEFORE branch publication;44cfdca7 was created directly from5ccbce88 without that edit. Do not resume53312c13. Final GossipEvent diff includes an incidental final-newline removal.

## Narrow production change

New `Styx/Logic/Questing/QuestObjectiveCompletion.cs` provides positive-only `IsNormalObjectiveComplete(Quest, rawObjectiveIndex)` evidence for indices0..3. It requires explicit nonzero normal-objective identity, positive required count, a matching accepted quest descriptor, no failed flag, sufficient observed count, matching repeated observations and retained player/memory identity. It uses the existing descriptor reader and temporarily disables only its read cache, restoring the caller's cache state afterward. It introduces no native offset and no Lua calls.

UseItemOn/GossipEvent consult this helper in ObjectiveProgress mode before requiring a new baseline delta. UseItemOn rechecks completion after facing immediately before item submission. GossipEvent checks IsDone immediately before option selection and NPC interaction. Existing static delta acknowledgement, legacy InvocationCount, QuestComplete, counter/attempt policy, generated collection guard, recipes, combat/navigation and vendor code remain unchanged.

**False means no positive completion proof, including unknown; it is not permission to act or proof unfinished.** This helper is not a global quest-admission policy or a new per-character completion database. Matching samples do not prove absence of same-value ABA, and the broader world/UI owner lifetimes are not certified by this repair.

## Original-version evidence

The existing bot Quest model separates `NormalObjectiveIDs`/`NormalObjectiveRequiredCounts` from collected-item requirements; its Quest.GetData reader obtains four packed descriptor counters. This distinction was checked against both pinned core sources:
- TrinityCore3.3.5 `8fda442f6c30ca21a622638063ab8b28376f1b25`, `src/server/game/Quests/QuestDef.h`, blob686efa9db8c08604653067da32cd491aaffe809a.
- AzerothCore `8337a378ac325e62a6a91e00c6a5e944205e8536`, same path, blob9f08d6b918c293d4ce681af6b0fcafb62cee4783.

Both separately declare RequiredItemId/RequiredItemCount and RequiredNpcOrGo/RequiredNpcOrGoCount; signed NPC/GameObject identity is not a collected-item index or a compressed displayed Lua row. These are source contracts, not a detection of the user's realm/database version. No assumed GetQuestLogIndexByID or later C_QuestLog API was added.

## Exact-head green evidence

| Evidence | Result |
|---|---|
| Integrated35455659843 / artifact10588032523 |17/17 entries, all build/run exits0 |
| New objective restart group |37/37;0 assertions;0 unexpected |
| Host35455659845 / artifact10588291999 |build exit0;3344 warnings;0 errors; tests_run=false |
| Focused quest-log35455659850 / artifact10588381917 |build/run0; groups27/27,16/16,5/5,28/28 |

Integrated SHA256 `32a1079bf26cdfe7b83e562112a0ed4d6c8830a5c891d8a6f7dac10ecb45cdbc`; host `093ad1cf52e86631c568afc8bc82292c9bcb8cfca58c078d42d6f0a1133ca825`; focused `8c99d2c6461062e4e7ec5420f24c5758c0e8ffc8e44b7644c9e79fbaa49f4e7f`.

Downloaded archives pass ZIP CRC. Both red and green integrated archives pass all183 inner-manifest hashes. **All139 normalized members are byte-identical.** Input index grows1820→1821 only for the new helper; the only other input changes are UseItemOn.cs and GossipEvent.cs. Every retained indexed fixture/workflow remains unchanged. Host/focused small archives have no inner hash manifest; no such verification is claimed. All runs are offline with game_attached=false.

## Remaining work and limits

This does not fix every handwritten profile, event without a normal counter, inventory observation, quest-history state, or travel preamble. W77's34/34 generated collection admission and9/9 Ret registration remain separate retained repairs. The new completion check is used inside the two named behaviours; it is not automatically inserted before every generated event/kill transport preamble.

Cursor work remains open: ReturnDisplacedCursorToSource accepts any different held entry if the old source slot is empty, which is not physical displaced-item proof. Also inspect whether ConfirmOwnedEquipPopup may run with pending state before the local equip submission flag is true; reproduce with actual method execution before changing it. Existing symbol names in Offsets335.txt are not ABI or GUID-return proof and cannot authorize a guessed native cursor call.

Gordunni Cobalt2987 still needs its complete sourced shovel/location/spawn/loot strategy. Shovel9466, cobalt9463/count12 and mound144064 records plus the pinned cobalt/junk spell distinction do not establish the action sequence. No new recipe, coordinate, native operation or all-quest capability claim was added. A large item-template fetch returned empty content; that is not evidence that the item/recipe is absent from the full database.

Continue remaining cross-owner review: source-bound strategy location authority, last-moment NPC/menu identity, isolation-plan actor/routine/context lifetime and actual navigated routes, prerequisites, PallyPower, reward choice, plugin reuse, merchant/gather and aquatic/native paths. Do not resume the withdrawn AuctionHouse/ProfessionBuddy experiment. Full post-cutoff acceptance, independent review and supervised original-client/server acceptance remain open.

Master's previous disclosed add/revert incident stays documented in W77. This continuation has not written master. Expected master isb2324913e2499ba30b239dd67224ca2c655c05cc with original approved tree552eeab1233c7c282897dca0ed9f4334c5e8ed43; verify the direct ref rather than relying on cached PR base_sha. Every future write requires an explicit nonempty approved branch, reviewed content/message and exact expected parent/blob.
