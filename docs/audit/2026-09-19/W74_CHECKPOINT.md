# W74 checkpoint — owned DeleteItems cursor/confirmation lifecycle

Date: 19 September 2026
Repo: `jeofwong/CopilotBuddy-private`
Draft PR: #51
Branch: `audit/next-55-equipment-observation-20260917`

## Verified head

Verified code/test head: **67411dbbd765a6d781809b7c18d4ca8e1a20d24a**
Verified tree: **2878b68b99bacf93079f48c1a99efacff803aa61**

Exact-head validation:
- integrated Windows x86 **35435502277**, artifact **10582149126**, SHA256 `5ce7c69ba18e50ee509f7d5d4fb77ecfe7773eb67ac3439f89cdd2a62e84f40d`, success, **17/17**
- host Windows validation **35435502301**, artifact **10581398494**, SHA256 `29e96e44f62740e61ed7607490eb4ea9deb824c975f2acb497bc27be2f224cf7`, success
- DeleteItems lifecycle **12/12**, **0 assertions**, **0 unexpected**

Do not merge PR51 without explicit user approval.

## Intervening post-W73 cursor acknowledgement

W73 documented the first `TryPickUp()` boundary. A later test/repair strengthened the pickup acknowledgement so a successful pickup must report the **expected cursor item entry**, not merely `CursorHasItem()`.

Production head **fb746ecdb07d08d10a343c694d2042e59ce387e2** was exact-head green:
- integrated **35433303671**, artifact **10582155861**, SHA256 `427ff22a561486e0968e2b79ae7943920a7615e16e810aba4d6d45f350279ea0`
- host **35433303565**, artifact **10581741166**, SHA256 `b2a3a126c45a2ed5a1176a7a1d2a95ee36dfcc84efba4d88d2133347ea0b8904`

Retain that stronger cursor identity contract.

## DeleteItems test-first evidence

The final clean behavioral red is **fd7d6f20050bcd7d37b7dd1fb88898c12c8a2ffc**. Earlier commits in the same test-only chain were fixture/compiler corrections and are not behavioral evidence.

Clean red:
- integrated **35435022827**, artifact **10581607785**, SHA256 `ec4538e330a9565056344b65b27c2bbf3bfad51778b7f6f77c9395021b474848`
- DeleteItems **2/12**, **10 intended assertions**, **0 unexpected**
- actual tracked `runtime-snapshot/Quest Behaviors/DeleteItems.cs` compiled successfully through the production quest-behavior compiler
- all failures were ownership/lifecycle assertions, not fixture or compiler errors
- host **35435022646**, artifact **10581378060**, SHA256 `cc835c556ff72e87d770a689293ea05c0058dd7f820bb4fb479f4c6f1e45bb8d`, success

Production **153128f726d9f1c47117dfe3adc974db163b36cd** changes only `runtime-snapshot/Quest Behaviors/DeleteItems.cs`.

The first post-production integrated run **35435314165 / art10582158833** reached **9/12**. The remaining three failures were test locator errors: the source helper region search matched earlier call sites once the helper names existed. No production defect was inferred. Test-only **67411dbb** anchors those unchanged assertions to the exact helper declarations.

## Implemented DeleteItems contract

Original-client evidence remains pinned to the 3.3.5 UI archive **d0339b17b0221db76e6acd2dc2915d224a5b62ca**.

The repaired behavior:
- no longer deletes or marks itself done inside `OnStart()`,
- uses `WoWItem.TryPickUp()` as an explicit success gate,
- never clears a foreign cursor,
- retains exact pending item GUID + entry identity,
- revalidates exact cursor item ownership before delete mutation,
- only handles exact `DELETE_ITEM` / `DELETE_GOOD_ITEM` popups via `StaticPopup_FindVisible`,
- never clicks generic `StaticPopup1Button1`,
- uses `DELETE_ITEM_CONFIRM_STRING` with the exact good-item edit box before confirming quality-3+ deletion,
- requires cursor release plus physical GUID disappearance from both bag observations and ObjectManager before acknowledging deletion,
- treats an item that returns to inventory as not deleted and reacquires from a fresh slot,
- bounds pickup-refusal and pending confirmation lifetimes,
- fails closed and stops the bot on unresolved destructive ambiguity rather than continuing the profile,
- leaves pending cursor ownership untouched on disposal instead of stealing it with `ClearCursor()`.

One `DeleteCursorItem()` invocation is still **not** considered deletion acknowledgement.

## Scope limits

This is offline/source-verified behavior, not a live deletion acceptance certificate. The harness compiles the real behavior and verifies ownership/lifecycle wiring, but it does not execute a live original-client Lua popup/inventory transaction.

Still open:
- same-entry ABA/native timing between final observations and client mutation,
- supervised original-client delete/popup acceptance,
- coexistence with other cursor owners,
- plugin-global confirmation races.

In particular, `MrItemRemover2` remains independent and currently still has:
- multiple `ClearCursor -> PickUp -> DeleteCursorItem` paths,
- additional unguarded delete paths,
- a global `DELETE_ITEM_CONFIRM` handler that clicks generic `StaticPopup1Button1` based on current-target presence rather than pending item ownership,
- `IsQuestItem()` deriving `BagIndex + 1` and `BagSlot + 1` independently before a destructive eligibility decision.

## Next slice

Audit **MrItemRemover2 destructive deletion only**, test first. Keep selling/opening/combining behavior separate unless necessary to preserve compatibility.

Required first-slice contract:
- one pending deletion transaction at a time,
- no `ClearCursor()`,
- explicit `TryPickUp()` success,
- exact GUID/entry ownership,
- exact popup identity and high-quality confirmation contract,
- no generic popup button click,
- bounded pending lifetime and safe reset/disable semantics,
- cursor release + physical item absence acknowledgement,
- stable container identity for quest-item protection.

After MrItemRemover, continue EquipItem/AutoEquip, AuctionHouse and ProfessionBuddy cursor transactions separately.

Retain all W73/W72/W71 open requirements: positive ExclusiveGroup cooldown/repeatable semantics, class/skill/reputation/breadcrumb planning inputs, buffs, gear/loadout/caps, addon terrain, underwater, GatherBuddy, remaining native LOS/ABI, Escort/event chains and supervised live acceptance.
