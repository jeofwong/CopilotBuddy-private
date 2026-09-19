# CopilotBuddy audit — W63 recovered prerequisite and vendor fixes

Continue draft PR51 on `audit/next-55-equipment-observation-20260917` in `jeofwong/CopilotBuddy-private`. Read live refs first, then `docs/audit/2026-09-18/W63_CHECKPOINT.md`, `W63_EVIDENCE.json`, NEXT_CHAT_PROMPT and the original3.3.5a/TrinityCore/provenance policies.

Latest recovered and verified code is **847e9c3aba9c44a1f92d5c810afccdf372465fee**, tree **77203dccc6667610f39a3dd7b010f53ffacba021**:16 commits beyond W62ad4a1eb1, not missing work to recreate. Existing W62 root pointers are archived under pre-w63. New W63 publication is documentation only; prior C# runs are recovered, not newly launched.

Verified final integrated35332335480/art10541791831:17/17 entries,99 aggregate groups,74 analyzer tests;155 inner-manifest members,1788 input hashes,111 normalized members. Host35332335473/art10541402192:0errors3304warnings,compile only. Seven new groups total165 passing cases: active-parent32,stepped retry27,bulk retry27,result12,stepped continuation31,bulk continuation16,entry20. Preserve the clean primary-red archives and all test identities; original fixture normalization/Profile-property errors were not behavioral reds.

Positive predecessors still require rewarded history; negative PrevQuestID now requires an accepted parent. Unknown history/log defers, unmet parent is not a pickup failure, and actual NPC offer lists remain a separate authority. Refused/unchanged stacks receive a bounded120s per-seller-instance retry exclusion keyed by player/merchant/bag/slot/count/link; known zero-value items are skipped. Bulk and stepped methods share that instance's gate, not every plugin's global transaction state. Submitted requests are never acknowledgement. Session replacement/aborted admission cannot complete an obsolete caller.

Open: grouped prerequisite provenance, final native bag-slot/UI/merchant context and receipt loss, whole-visit pending/full-bag re-entry, all special-quest strategies, full gathering/rest/remount, PallyPower/Carbonite integration, rank/exclusive buffs, loadout equipment, underwater recovery and original-client/server acceptance. No exhaustive-completion or live-compatibility claim. Read W63's exact remaining boundaries before implementing more tests.

Original WoW3.3.5a build12340; TrinityCore3.3.5 primary, AzerothCoreWotLK secondary. Masterf462a9bb already includes approvedPR47 and preserved README. Keep backupsc43c50d8/8382a7ec/518baec5, exclude25/43/45; no remerge44/47, PR51 merge, force push, deployment or installed addon/binary/mesh replacement.
