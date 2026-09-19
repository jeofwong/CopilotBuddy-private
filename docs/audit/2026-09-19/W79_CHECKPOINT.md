# W79 — submission-gated equip confirmation, verified offline

Repository `jeofwong/CopilotBuddy-private` (1367174964), draft/unmerged PR51, branch `audit/next-55-equipment-observation-20260917`. This follows W78 and retains W77's corrected quest/navigation/Singular scope. Original WoW3.3.5a/build12340 only; TrinityCore3.3.5 primary, AzerothCore WotLK secondary. No new Lua API, mouse automation, auction expansion, merge, force push, deployment or installed-file change.

**Verified code/test head: `96d1259f1d3c7035b85b1fa4bf00d05fe52d4ee6`.**
**Verified tree: `bed704f539f0e21697cec2862906096d30139f03`.**
Documentation after this head does not constitute another tested production revision. Reconcile live refs before proceeding.

## Demonstrated defect and minimal repair

Both EquipItem and AutoEquip2 set pending GUID/entry/slot intent before attempting the actual equip submission. If pickup succeeded but SubmitOwnedCursorEquip returned false, subsequent TickPendingEquip called ConfirmOwnedEquipPopup before retrying submission. That helper previously checked pending intent and an explicit slot, but not the existing `_pendingEquipSubmitted` flag. Consequently it could emit a bind-confirmation request despite having no locally successful equip submission. The existing Lua only filters popup type and slot; another same-slot popup could satisfy those predicates.

Test-only `ae418d26cc3b980d6b3212a37ca7860125a7a8d2`, treefe8ef37476321ba4acf6040dd84bcc78e7cb867f, adds `EquipPopupSubmissionRegressionTests.cs`. It compiles verbatim tracked ConfirmOwnedEquipPopup and TickPendingEquip methods with controlled fields, submission results and a recording Lua boundary. It does not rewrite their statements, execute Lua, perform native equipment operations or attach a game. Complete-owner compilation also remains in the retained suite.

The18 cases cover both owners: absent pending state; pending-but-unsubmitted state; unknown slot; valid submitted explicit slots1 and16; failed retries; successful retry without premature confirmation; permitted confirmation on the subsequent tick; and repeated refusals. Existing emitted popup type/slot predicates are checked on admitted requests.

Clean red: integrated35456262122/art10587819087, SHA256 `968c98374605146952faf49e2d73d3290cec192a801bbed497b267e25611c9fb`: **8/18,10 intended assertions,0 unexpected**. Every build exits0; all16 other integrated entries pass. ZIP CRC and184 inner-manifest hashes verified.

Production `96d1259f1d3c7035b85b1fa4bf00d05fe52d4ee6` adds only `|| !_pendingEquipSubmitted` to the confirmation helper's early return in:
- `runtime-snapshot/Quest Behaviors/EquipItem.cs`
- `runtime-snapshot/Plugins/AutoEquip2/AutoEquip.cs`

The exact diff was inspected before publishing. Apart from those two guards it removes the final newline of EquipItem.cs. No Lua string, popup type/slot predicate, submission/retry logic, timeout, item return, valuation, ammo, loot policy, test, workflow, quest or combat code changed. Pending intent is not successful local submission; successful local submission is still not server acceptance.

## Exact-head green and retained checks

| Evidence | Actual result |
|---|---|
| Integrated35456734949 / artifact10587779649 |17/17 entries; all build/run exits0 |
| New popup submission group |18/18;0 assertions;0 unexpected |
| Host35456734963 / artifact10588378360 |build exit0;3344 warnings;0 errors; tests_run=false |
| Retained W78 normal-objective restart |37/37 |
| Retained W77 generated collection admission |34/34 |
| Retained Ret registration |9/9 |
| Retained acknowledged-equip timeout |10/10 |
| Python analyzers |89 tests;OK |

Integrated ZIP SHA256 `8dd192f459654e2e28420fd3e9b1bb08a6d7c09fe1c638b418ab6b9b12cfdb66`; host ZIP SHA256 `fcb527df8e4d566c29f4d380a7282801908b360d5d6641b26353b47868d0acfd`.

Downloaded ZIP CRCs verified; red and green integrated archives each pass184 inner hashes. Their1822 input-index sets are identical and differ only in the two production files above. **All140 normalized fixture members are byte-identical.** No assertion was weakened or altered to make the repair pass. The host archive has no inner manifest; none is claimed. All validation has game_attached=false.

W78 remains durable at documentationc041780c9fd03c81f569a4ed0cf70c04b1160181, verified code44cfdca70a59f3cd660364748687782f23a2793d. Its independent full-owner restart test pair23/37->37/37,139 unchanged normalized members and focused quest-log validation are recorded in W78_CHECKPOINT/EVIDENCE. Do not recreate either W78 or W77 repairs.

## Evidence boundaries and remaining scope

This proves the selected C# confirmation/tick admission paths and emitted requests under controlled external results. It is not native Lua execution, physical item GUID ownership, complete plugin lifecycle, actual popup/server acknowledgement, or supervised gameplay acceptance.

**The displaced-item defect remains:** ReturnDisplacedCursorToSource permits any held entry different from the pending equipped item at an empty remembered source slot. W79 deliberately does not disguise this as solved by a submission flag. Same-entry ABA, same-slot foreign popups after legitimate submission, player/routine/lifetime changes, reentrant events and cross-plugin arbitration remain separate questions. Offsets335 symbol names such as GetCursorItem do not establish a native ABI/signature or GUID return type; no guessed call was added.

W78's new helper handles explicitly selected normal NPC/GameObject raw-counter slots0..3 inside UseItemOn/GossipEvent ObjectiveProgress mode. Caller/source mapping must actually identify that raw slot; a displayed/compressed quest-log row or collected-item index is not interchangeable. It is not a global objective admission policy, a fix for all handwritten profiles, or a guard before every generated transport preamble. Existing delta acknowledgement and invocation/whole-quest modes remain unchanged. Further review must not mistake positive-only helper evidence for proof that every unknown state is safe to act on.

Gordunni Cobalt2987 still lacks the complete verified shovel9466/location/trigger/spawn/loot strategy. The shipped cobalt9463count12 and mound144064 data plus pinned cobalt/junk outcomes do not authorize invented coordinates or an invocation-as-completion assumption. No new recipe was shipped. The latest web search surfaced TBC/Vanilla/Retail references, which were not used as 3.3.5a implementation authority; the earlier large pinned item-template fetch returned empty content, not proof of absence.

Full post-cutoff review after18 September2026 21:58 Malaysia/13:58 UTC (ancestor4c6b1b2e75af4dd0eaffde11d38ebbc84a21f4c0) is still incomplete. Remaining work includes generated/direct/event objective admission, complete cursor ownership, source/recipe/location authority, final gossip NPC/menu identity, isolation-plan ownership and actual navigated routes, prerequisite publication/scheduler, PallyPower/buffs, rewards/gear/loadout, plugin compilation reuse, merchant/gather and aquatic/native paths. Keep all earlier independent-review and supervised original-client/server acceptance gates. Do not resume AuctionHouse or ProfessionBuddy; their unfinished experiment was withdrawn atd8b6476a, not certified.

## Repository integrity

Direct master ref was re-read and remains **b2324913e2499ba30b239dd67224ca2c655c05cc**. W77 records the prior empty-document add/revert and the restored approved tree552eeab1233c7c282897dca0ed9f4334c5e8ed43. No master write occurred in this continuation. PR51 remains draft/unmerged. All branch publications use explicit approved branch, exact reviewed parent/tree and force=false. Preserve every backup/exclusion rule; never send blank placeholder writes or rely on the cached PR base_sha as the current master ref.
