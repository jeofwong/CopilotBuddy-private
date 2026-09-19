# CopilotBuddy audit — W57 TrinityCore-aware quest and Paladin continuation

Continue draft PR51 on `audit/next-55-equipment-observation-20260917` in `jeofwong/CopilotBuddy-private` (ID1367174964). Read `docs/audit/WOTLK_335A_RESEARCH_POLICY.md`, `docs/audit/TRINITYCORE_335_COMPATIBILITY.md`, `docs/audit/2026-09-18/W57_CHECKPOINT.md`, `W57_EVIDENCE.json`, `W57_COMPATIBILITY_REVIEW.md`, then NEXT_CHAT_PROMPT.md. Original3.3.5a build12340; TrinityCore3.3.5 primary, AzerothCore WotLK secondary. Compatibility target is not universal live acceptance.

Master remains the approved PR47 merge f462a9bb4eb18acac9069f495177df35672286d5. PR51 is separate and unmerged. Preserve README, backups c43c50d8/8382a7ec/518baec5 and exclusions25/43/45. Do not remerge47 or historical44, force push, deploy or replace installed binary/mesh files without authorization.

Latest verified code **44ff289ae1afd8fcef625948b10b5af8819deb26**, treef25c23875730aa8c737da0c3011901713f2335d7. Pending cast-credit red7854c3f5 was10/20,10 intended assertions,0unexpected. The narrow quest-aware support repair gives20/20 with unchanged tests; it prevents server SpecialFlags0x20 from becoming an ordinary KillMob/pickup plan and preserves turn-in/satisfied counters/valid collection. It does NOT invent an item recipe.

Final integrated35294707613/art10527486517:17/17 entries,88 aggregate groups,1774 input hashes,100 normalized members,144 manifest entries. Red/green differ only in QuestScheduler.cs among inputs,87 other group outcomes identical. Host35294707643/art10528400041:0 errors,3296 warnings,compile only. Public CI and native publishing worked; no workflow change. No unexecuted test remains at verified code.

Sixteen commits recovered since W56 include explicit UseItemOn selection44/dispatch72, Greater blessings87, aura70/Lowbie10, Protection recovery53 and engagement38 passing in the final run. Do not recreate those fixes. Sanctuary and UseGreaterBlessings are present; Greater defaultsfalse. Their original separate red histories were not newly recovered here.

Automatic quest-item/event recipes, actual GatherBuddy stun/target/rest/mount transitions, all-spec rank/singleton decisions, native/cursor/LOS ownership, unified equipment, underwater escape, independent review and original-client acceptance remain. Read the compatibility review before claiming earlier AC research proves TC behavior; internal quest flag values differ. The old W56 root pointers are archived under `docs/audit/2026-09-18/pre-w57/`.
