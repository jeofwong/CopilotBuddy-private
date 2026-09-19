# W77 review scope correction — 19 September 2026

This is an in-progress review checkpoint, not an acceptance certificate.

## Owner's current instructions

Review every change after 18 September 2026 21:58 Asia/Kuala_Lumpur (13:58 UTC), with priority on Wholesome questing, navigation and Singular combat. Investigate custom-behavior replay after Stop/Start, current inventory/objective completion, and Gordunni Cobalt's shovel/location/loot sequence. Original WoW 3.3.5a build 12340 only; TrinityCore 3.3.5 primary and AzerothCore WotLK secondary. Do not assume a modern or Classic API is compatible. Do not use desktop mouse automation. Do not merge PR51, deploy, replace installed binaries/addons/meshes, or continue unrelated auction/ProfessionBuddy expansion.

## Exact review window

The last ancestor at/before the cutoff is 4c6b1b2e75af4dd0eaffde11d38ebbc84a21f4c0 (18 September 13:35:48 UTC). The first following commit is 5b7ee98d9560d14221875632beabbc50d4f79c0d (13:59:35 UTC). Entry head bf1cd682c229a7f0143d5f3e54a6949c53ad8c44 is 125 commits ahead, with no divergent ancestry. A model name is not encoded reliably in these commits; the user's attribution is not independently verified authorship evidence.

## Auction scope rollback

The five commits after W76 documentation ac03b01e7051036922c4d8a9f36aed498aa00673 change exactly four paths:

- Styx/WoWInternals/Misc/AuctionHouse.cs
- Styx/Logic/Inventory/Frames/AuctionHouse/AuctionHouse.cs
- Styx/WoWInternals/Misc/AuctionPostTransaction.cs
- Tools/WholesomeQuestRecoveryRegressionTests/AuctionPostOwnershipRegressionTests.cs

Restore the two existing AuctionHouse files to their exact pre-experiment blobs (70d3c6ab3738e08ed5eddbc55f45c20328d47d3f and f4188188da1d792db36cd91df69625e7656ed6ab) and remove only the new transaction and its feature-specific tests from the active tree. This is withdrawal of an unfinished, out-of-scope feature, NOT weakening retained quest/combat assertions to make a claimed auction repair green. Every experimental commit remains in Git history.

At bf1cd682, original integrated run35443189746/art10584271731 actually has 16/17 passing entries. Auction source/pure contract group is17/25 with8 assertions and0 unexpected errors. The archive SHA256 is4b13b6b56bbd4f40e938fe1ee3dc9fbfb023dbb7c129b830aedceee83dbb37c9; its CRC and all180 inner-manifest hashes were verified locally. Missing protections include an occupied sell slot, negative-to-unsigned stack conversion, carried-bag bounds, price/quantity admission and unverified CancelSell cleanup. Source inspection also shows that matching name/texture/quality is not unique physical-item proof and returning true after StartAuction is not server acceptance.

Restoring the legacy auction files does NOT certify their old behavior as safe. They are outside the current questing scope and must not be enabled or advertised as newly audited functionality. No quest, navigation, Singular, vendor, addon-evidence or prior test file is part of this rollback. No workflow is changed.

## Review findings still being investigated

1. New UseItemOn/GossipEvent ObjectiveProgress behavior captures a new initial count on each start and recognizes only a subsequent increase. It lacks an explicit completed-objective admission check. Generated custom-behavior guards currently check HasQuest only, including before transport preambles. This is relevant to the reported repeat-visit symptom, but is not a reproduction of an unidentified installed profile.
2. W76 equip/delete passing counts are primarily tracked-owner compilation and source/pure-helper contracts. They do not execute the complete cursor/UI lifecycle. W76's displaced-item return accepts any different entry and its acknowledgement branch can bypass the timeout while return fails. These require behavioral review, not another source-token assertion.
3. The shipped Feralas data contains Gordunni Cobalt and a dirt-mound collection objective. That alone is not a shovel-use/spawn/loot strategy. Verify the exact original-client/core recipe before enabling one.

## Continuation

Read this correction before W76's old NEXT instructions. Do not resume AuctionHouse or ProfessionBuddy. Reconcile live refs; verify the scoped rollback; then investigate and test the quest restart and cursor-owner defects using actual execution of tracked code wherever possible. Retain all original-client/core/provenance policies and earlier requirements. Unknown observations must not become completion, permission to act, or an invented persistent 'done' flag. Prefer fresh authoritative quest/inventory state over a universal once-ever cache, which would break repeatable quests and recoverable steps.

The full 125-commit review, runtime regressions and supervised original-client/server acceptance are not yet complete at this scope checkpoint.
