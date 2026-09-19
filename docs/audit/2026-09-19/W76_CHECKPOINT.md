# W76 checkpoint — owned EquipItem/AutoEquip cursor transactions + slot-bound bind confirmation

Date: 19 September 2026
Repo: `jeofwong/CopilotBuddy-private`
Draft PR: #51
Branch: `audit/next-55-equipment-observation-20260917`

## Verified head

Verified code/test head: **698068344bbc51f79d81f76e7d3b91453f29de0c**
Verified tree: **77dd5c926b45a82f2961f89f95e058111d7cc0d1**

Exact-head validation:
- integrated Windows x86 **35441935790**, artifact **10583728386**, SHA256 `364bac78c8b67097056056bd6db4d8bd78eb4a4046821b013cfe51456822fe6e`, success, **17/17**
- host Windows validation **35441935725**, artifact **10583953031**, SHA256 `e73bb3821ea5d3a3630fdde16d705da7d443e954b8bfebef34044e65b162a4a6`, success
- Equip cursor ownership **19/19**, assertions=0, unexpected=0
- W75 container-slot identity **20/20** retained
- W75 MrItemRemover delete **14/14** retained

Do not merge PR51 without explicit user approval.

## Test-first evidence

Primary clean test-only red: **6622f7828b5460730752b5be8731603e0487ca58**.

Integrated red:
- run **35437261168**
- artifact **10583270460**
- SHA256 `91bf3dc1fdf621ed87b8ec4d14aee8b204abc41e6d5191b4474eafbf8e36d175`
- Equip cursor ownership **4/17**
- **13 intended assertions**
- **0 unexpected**
- both tracked owners compiled through the production SourceCompiler

Host validation at the red head was green (**35437261170**).

The first production sequence was:
- **1315fecf087395207a57f0d7a2560218890ba0ee** — expose validated pickup source location from `WoWItem.TryPickUp`
- **fb11f02d4cf8b37a05f10a238e2e4f8fe336f6bd** — own the explicit-slot `EquipItem` transaction
- **6bf484f20f48c44603f3ae5f966da93770ea4e86** — own the `AutoEquip2` transaction

At **ba08602ae3f5ccb54eba08f8dfceff5418826ebf**, 16/17 ownership cases passed. The one failure was a test-scope defect: it rejected every `PickupContainerItem` occurrence in the whole EquipItem file, including the guarded displaced-item return path. Test-only **2e8691d9d421085e0a8b937fb23f632093e430f2** narrowed that assertion to the actual initial equip owner; no production behavior was weakened.

## Original 3.3.5 bind-popup boundary

Pinned original-interface evidence remains **wowgaming/3.3.5-interface-files d0339b17b0221db76e6acd2dc2915d224a5b62ca**.

Its `StaticPopup.lua` establishes:
- `EQUIP_BIND` and `AUTOEQUIP_BIND` both call `EquipPendingItem(slot)`,
- `StaticPopup_Show(..., data)` stores that value as `dialog.data`,
- `StaticPopup_FindVisible` identifies the dialog type but does not filter `data` for these non-multiple dialogs.

That exposed a residual same-type-popup ownership gap in the first repair. Test-only **86333b357df0bab88925075a4e592c96adc2d541** added two slot-ownership cases:
- integrated **35441689706 / art10583628215**, SHA256 `3f194756ab6a44ed250d2cd9d54232f1d2d2b7904fd2778a2e261ce72119684d`
- Equip cursor ownership **17/19**
- **2 intended assertions**
- **0 unexpected**
- host **35441689712 / art10583752980** was green

Production **698068344bbc51f79d81f76e7d3b91453f29de0c** then made both popup-confirmation helpers fail closed unless:
1. there is a pending equip,
2. the transaction has an explicit equipment slot,
3. the visible dialog is exactly `EQUIP_BIND` or `AUTOEQUIP_BIND`, and
4. `tonumber(p.data)` equals the owned equipment slot.

Unknown-slot `EquipItemByName` transactions no longer auto-click a bind popup they cannot identify.

## Implemented equip ownership boundary

The W76 repair retains one explicit pending transaction with:
- exact item GUID and entry,
- intended equipment slot,
- validated source bag/slot returned by `TryPickUp`,
- submission state and a ten-second bounded lifetime.

Explicit-slot equip now:
- resolves/revalidates the physical item through the shared container identity path,
- refuses pickup if cursor or slot identity cannot be safely established,
- validates exact cursor item entry before `EquipCursorItem`,
- never uses `ClearCursor`,
- waits for the exact intended equipment slot to contain the pending GUID before acknowledging success,
- returns a displaced cursor item only when the cursor contains a different item and the remembered source slot is empty,
- attempts timeout restoration only for the owned item/source boundary.

`AutoEquip2` additionally gates `Pulse` and `DoCheck` while one equip is pending, so it does not start overlapping managed equip transactions. Disposal revokes only plugin-managed state and does not clear a foreign cursor.

The slice preserves the pre-existing ammo path, item scoring, weapon-style decisions, loot-roll policy and bag-selection logic.

## Scope limits

This is controlled source/Windows x86 verification with **no game attached**. It is not supervised original-client/server acceptance.

Still open:
- same-entry ABA between observation and mutation,
- a foreign concurrent equip targeting the same equipment slot,
- native popup/event timing and secure-action behavior,
- unknown-slot `EquipItemByName` bind prompts are intentionally not auto-confirmed,
- cross-plugin/global cursor arbitration,
- live displaced-item behavior and server acknowledgement,
- gear valuation/class proficiency/loadout policy outside this cursor slice.

## Next slice

Audit **AuctionHouse core cursor/post transactions**, test first, without folding ProfessionBuddy into the same repair.

Current source risks already traced:
- `Styx/WoWInternals/Misc/AuctionHouse.PostAuction` performs `item.PickUp()` → `ClickAuctionSellItemButton()` → raw `ClearCursor()` → sleep → `StartAuction`,
- `Styx/Logic/Inventory/Frames/AuctionHouse/AuctionHouse.PostAuction` accepts caller bag/slot coordinates and directly calls `PickupContainerItem` before posting,
- neither path currently proves stable item identity, owned cursor transfer, auction sell-slot acknowledgement, or posting context immediately before `StartAuction`.

Require a bounded, fail-closed owner around stable GUID/entry/container identity, cursor admission, AuctionFrame context, sell-slot transfer and posting acknowledgement. Preserve browse/search/bid/buyout/cancel behavior.

After AuctionHouse core is separately verified, audit **ProfessionBuddy** cursor transactions, beginning with its independent `SellItemOnAhAction` Lua path. Do not merge those two ownership domains into one test-first repair.

Retain all W71-W76 open requirements: prerequisite/core/data provenance, complex dependency semantics, authoritative special-item/gossip/event/escort recipes, PallyPower integration, permitted addon/map/terrain evidence, stronger/exclusive buff policy, gear/loadout/caps/reward evaluation, underwater recovery, GatherBuddy/rest/remount ownership, native UI/slot/cursor/LOS, independent review and supervised original-client/server acceptance.
