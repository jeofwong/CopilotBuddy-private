# CopilotBuddy audit — W59 verified quest-item and combat safety

Continue draft PR51 on `audit/next-55-equipment-observation-20260917` in `jeofwong/CopilotBuddy-private` (ID1367174964). Re-read live refs before writing. Read `docs/audit/2026-09-18/W59_CHECKPOINT.md`, `W59_EVIDENCE.json`, `docs/audit/QUEST_DATA_PROVENANCE_335.md`, both original-client/TrinityCore policies, then NEXT_CHAT_PROMPT.md.

Latest verified code **ee31cde9985f233fb7c085160a50c05c54650ea0**, treecbe514baa03078eadc4be4a3300e556f2cb4d6b5. Native publishing works. Two new unchanged-test Windows repairs: merchant admission7/20->20/20 (13 intended assertions); combat target gaps11/26->26/26 (15 intended assertions), zero unexpected errors. Existing33 progress-lifetime cases remain. Final integrated35303475446/art10531141312 passes17/17 entries and91 aggregate groups; host35303475426/art10531046186 has0errors3292warnings. Tests/workflows/103 normalized members stay identical within each repair;1777 input hashes and147 internal manifest entries verified at the documented scope.

The a11 combat test normalization failure and subsequent Mount ambiguity were fixture errors, not behavioral red. Explicit raw-string namespace comments and a fixture Mount alias fixed those without changing26 assertions or weakening the normalizer. The W58 local merchant proposal is now committed and repaired; do not add it again. No unexecuted test remains at the verified revision.

Quest planning currently reads local JSON. Live quest acceptance/progress and supported Lua queries are separate observations, not full server DB/script access. Provenance/import and automatic special-item/event/escort recipes remain requirements, not implemented features. TrinityCore3.3.5/original client12340 primary; AzerothCore secondary; no universal live compatibility certificate.

Masterf462a9bb already includes approved PR47. Preserve README, backupsc43c50d8/8382a7ec/518baec5, exclude25/43/45, do not remerge44/47. PR51 remains unmerged; no force push/deployment/installed-binary or mesh replacement. Old W57 pointers are archived byte-for-byte under `docs/audit/2026-09-18/pre-w59/`.

Keep the wider quest, cursor/LOS/acknowledgement, full GatherBuddy/Targeting/rest/mount, buff rank/exclusivity, equipment/reward and underwater requirements open. Integrated green is not independent approval, maximum-DPS proof or original-client acceptance. See W59 for the separate neutral CodeQL configuration warning.
