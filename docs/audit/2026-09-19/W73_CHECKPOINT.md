# W73 checkpoint — validated single-item cursor pickup

Date: 19 September 2026
Repo: `jeofwong/CopilotBuddy-private`
Draft PR: #51
Branch: `audit/next-55-equipment-observation-20260917`

## Verified head

Verified code/test head: **3c76cddb12dbaef94ae7e6f121bb2678387f3471**
Verified tree: **154ce386ab94df0065b27a87c66c1416caa01847**

Exact-head validation:
- integrated Windows x86 **35432153183**, artifact **10580649556**, SHA256 `70ceb1c8ae0f031e25844cd461e46cf8d0279167e22fa20b959a62fdfeb8d055`, success
- host Windows validation **35432153030**, artifact **10580249866**, SHA256 `6ba4a24e2e4c4702459d3966171442019571ec4a8b845f77e4089bb3db300306`, **0 errors / 3342 warnings**

Selected retained groups:
- container slot identity + single-item pickup **16/16**
- active parent prerequisites **39/39**
- dependent previous alternatives **14/14**
- negative exclusive dependencies **10/10**

Do not merge PR51 without explicit user approval.

## Test-first W73

Test-only **9875ab34fb3de0dec245c803c64a635d1c0ede87** changed only `ContainerItemSlotIdentityRegressionTests.cs`.

Actual Windows red:
- integrated **35431946631**, artifact **10580863879**, SHA256 `be96c4fc86a8b08d39cfb165e628bd838deb5da7144800d79d6ce9719ecab686`
- container group **9/16**, **7 intended assertions**, **0 unexpected**
- all nine existing safe-container-use controls remained green
- host **35431946628**, artifact **10580664206**, SHA256 `d0d24ef33a469264a1b1925d9656e07ccb6807c5fc21071ffce9063aa86a0b9b`, compile success

Production **3c76cddb** changes only `Styx/WoWInternals/WoWObjects/WoWItem.cs`: **+64/-1**.

## Implemented single-item pickup contract

Original client evidence is pinned to the 3.3.5 UI archive **d0339b17b0221db76e6acd2dc2915d224a5b62ca**. Relevant original APIs are `GetCursorInfo`, `GetContainerItemLink`, `PickupContainerItem` and `CursorHasItem`.

`BuildValidatedContainerPickupLua` now:
- rejects invalid bag/slot/entry arguments in managed code,
- calls `GetCursorInfo()` before mutation and refuses every truthy pre-existing cursor payload,
- never calls `ClearCursor()`,
- verifies `GetContainerItemLink(bag,slot)` still contains the expected item entry,
- only then calls `PickupContainerItem`,
- returns success only if `CursorHasItem()` is true afterward,
- never reuses `UseContainerItem` mutation semantics.

`TryPickUp()` now:
- captures item GUID + entry,
- snapshots backpack/bag GUIDs,
- resolves one unambiguous Lua bag/one-based slot through the existing GUID resolver,
- revalidates that same GUID in the chosen slot immediately before Lua,
- executes the validated pickup script and returns its boolean acknowledgement,
- fails closed on observation/Lua errors.

Public `PickUp()` is retained for compatibility but delegates to `TryPickUp()` and logs a refusal instead of deriving `BagIndex + 1` and `BagSlot + 1` independently.

This is **single-item pickup only**. It does not establish cursor transaction ownership for a caller that intentionally carries an item across a second action. Same-entry ABA between final managed slot revalidation and the Lua entry check remains an explicit native limitation.

## Destructive deletion evidence for next slice

`runtime-snapshot/Quest Behaviors/DeleteItems.cs`:
- is excluded from the main host build,
- performs all mutations inside `OnStart()`,
- for every matching item calls `item.PickUp(); DeleteCursorItem();`,
- immediately sets `_isBehaviorDone = true`,
- has no pickup success gate, confirmation ownership, retry or inventory-absence acknowledgement.

`runtime-snapshot/Plugins/MrItemRemover2/Methods.cs` has multiple independent delete call sites. Some explicitly call `ClearCursor()` before pickup and some do not. Its plugin attaches `DELETE_ITEM_CONFIRM`; current handler clicks generic `StaticPopup1Button1` whenever the player has a current target, without proving popup identity or item ownership.

Original 3.3.5 UI:
- `DELETE_ITEM_CONFIRM` opens `DELETE_ITEM` below quality 3 and `DELETE_GOOD_ITEM` for quality 3+,
- both popup accept handlers call `DeleteCursorItem()` again,
- cancel clears the cursor,
- `DELETE_GOOD_ITEM` starts with button1 disabled and requires its edit box text to equal `DELETE_ITEM_CONFIRM_STRING`,
- `StaticPopup_FindVisible(which)` exists and popup frames store `dialog.which`.

Therefore one `DeleteCursorItem()` invocation is **not deletion acknowledgement**, and generic `StaticPopup1Button1` clicking is not an acceptable ownership contract.

## Next slice

Audit standalone `DeleteItems` first with a dedicated compiled-behavior harness. Do not migrate MrItemRemover in the same repair.

Required contract to test before production:
- no delete request unless `TryPickUp()` succeeded,
- caller never steals or clears a pre-existing foreign cursor,
- keep exact item GUID/entry ownership through the pending delete lifecycle,
- distinguish item disappearance from popup-pending state,
- only interact with exact `DELETE_ITEM` or `DELETE_GOOD_ITEM` popup owned by this deletion attempt,
- if high-quality confirmation is supported, use that exact dialog/edit-box contract rather than generic buttons,
- require actual bag absence / owned-cursor release before advancing,
- do not mark behavior done merely because DeleteCursorItem was invoked,
- bound retry/pending lifetime and preserve explicit failure diagnostics.

Then review MrItemRemover separately, followed by EquipItem/AutoEquip, AuctionHouse and ProfessionBuddy cursor transfers.

Retain all W72/W71 open requirements: positive ExclusiveGroup cooldown/repeatable semantics, planning class/skill/reputation/breadcrumb inputs, buffs, gear/loadout/caps, addon terrain, underwater, GatherBuddy, remaining native LOS/ABI, Escort/event chains and supervised live acceptance.
