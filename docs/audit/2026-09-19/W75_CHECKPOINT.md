# W75 checkpoint — owned MrItemRemover deletion + validated quest-item slot identity

Date: 19 September 2026
Repo: `jeofwong/CopilotBuddy-private`
Draft PR: #51
Branch: `audit/next-55-equipment-observation-20260917`

## Verified head

Verified code/test head: **4e7c7699d577bbc6922bdbc12adea30e17c12450**
Verified tree: **ffd735430f64cdfdf91af3ce3d1d41d2f53f94db**

Exact-head validation:
- integrated Windows x86 **35436934243**, artifact **10582366699**, SHA256 `c71541514dd21642bc2570f91a0e7d637aaf88bedd7bffb8fb73a734687598ed`, success, **17/17**
- host Windows validation **35436934248**, artifact **10582840978**, SHA256 `4f8b1437a19cdc8a6f36e69792360cab1dac1d21c2c23dfdec18d2ad3ddcab8d`, success
- container slot identity **20/20**, assertions=0, unexpected=0
- MrItemRemover delete **14/14**, assertions=0, unexpected=0

Do not merge PR51 without explicit user approval.

## Test-first red

Final clean test-only red head before production: **f0b2cba9479a11d7538c8a01a17346f42e13c43d**.

Integrated red:
- run **35436213745**
- artifact **10582725289**
- SHA256 `51eb916291c044910be6561d5f88da2df0c50a76cd2ae6ee3a56f0bf354a05d7`
- MrItemRemover delete **2/14**
- **12 intended assertions**
- **0 unexpected**
- tracked plugin source compiled through the production SourceCompiler

Host validation at the red head was green (**35436213791**).

No existing assertion was weakened to obtain green.

## Implemented container quest-info boundary

Production **7cb5dcbc719a3a73f8db0ecd1f0484104a4e24d8** extends `WoWItem` with a stable quest-item observation boundary:

- resolves one physical item GUID to one container location,
- revalidates that exact GUID at the chosen slot,
- validates the expected entry through `GetContainerItemLink`,
- then calls original-client `GetContainerItemQuestInfo`,
- preserves the original 3.3.5 return tuple `isQuestItem, questId, isActive`,
- fails closed when slot/GUID/entry identity cannot be retained.

Original UI evidence remains pinned to **wowgaming/3.3.5-interface-files d0339b17b0221db76e6acd2dc2915d224a5b62ca**; its ContainerFrame/BankFrame use exactly `isQuestItem, questId, isActive = GetContainerItemQuestInfo(...)`.

## Implemented MrItemRemover destructive owner

Production through **4e7c7699d577bbc6922bdbc12adea30e17c12450** changes only these three files relative to the final red:
- `Styx/WoWInternals/WoWObjects/WoWItem.cs`
- `runtime-snapshot/Plugins/MrItemRemover2/Methods.cs`
- `runtime-snapshot/Plugins/MrItemRemover2/MrItemRemover2.cs`

Destructive deletion now:
- owns one pending GUID + entry at a time,
- uses `TryPickUp()` and never `ClearCursor()`,
- stops the current item scan after one destructive candidate,
- revalidates exact cursor type + item entry before `DeleteCursorItem()`,
- rejects an already-open delete popup before submitting another delete,
- handles only exact `DELETE_ITEM` / `DELETE_GOOD_ITEM` popups,
- uses `DELETE_ITEM_CONFIRM_STRING` and the popup edit box for good-item confirmation,
- never clicks generic `StaticPopup1Button1`,
- services pending deletion from `Pulse()` independently of periodic bag-check enablement,
- requires cursor release plus physical GUID absence from BagItems/ObjectManager before acknowledging deletion,
- treats a returned item as not deleted and requires a fresh pickup,
- bounds pending delete lifetime,
- resets plugin-managed pending state on disable without clearing or stealing the cursor,
- preserves unknown/unstable quest-item query as protected rather than deletable.

Selling/opening/combining behavior was not refactored in this slice.

## Scope limits

This is source/controlled Windows verification, not supervised original-client deletion acceptance.

Still open:
- native same-entry ABA between final observation and mutation,
- live popup/event timing,
- cursor ownership coexistence across independent plugins/behaviors,
- recovery policy after timeout while an owned item may still remain on the cursor,
- original-client server acceptance.

## Next slice

Audit **EquipItem and AutoEquip2 equip cursor transactions**, test first and separately.

Current source risks already traced:
- `Quest Behaviors/EquipItem.cs` marks itself done immediately after a fire-and-forget equip request,
- explicit-slot EquipItem independently derives `BagIndex + 1` / `BagSlot + 1`,
- AutoEquip2 calls `ClearCursor()`,
- AutoEquip2 independently derives BagIndex/BagSlot before pickup,
- AutoEquip2 clicks generic `StaticPopup1Button1`,
- neither owner establishes exact item/cursor/equipment-slot acknowledgement before completion.

Do not combine AuctionHouse or ProfessionBuddy into the first equip repair. After EquipItem/AutoEquip, review AuctionHouse and ProfessionBuddy cursor transactions separately.

Retain all W74/W73/W72/W71 open requirements: prerequisite provenance/core differences, positive ExclusiveGroup repeatable/cooldown semantics, class/skill/reputation/breadcrumb planning inputs, buffs, gear/loadout/caps, addon terrain, underwater, GatherBuddy, remaining native LOS/ABI, Escort/event chains and supervised live acceptance.
