# Resume at W66 — verified GatherBuddy sale-visit backoff

Repository `jeofwong/CopilotBuddy-private`, draft PR51, branch `audit/next-55-equipment-observation-20260917`. Reconcile live head/master before writing.

Latest verified code: **37511a5f0a4db7b8fe91efd1475f8d2700cad4f6**, tree **71de8d71865929a8b8938ccdd8180b8e76c66ecc**. Read `docs/audit/2026-09-18/W66_CHECKPOINT.md` and `W66_EVIDENCE.json` first. W65/W64/W63 remain preserved evidence and requirements.

W65 docs were stale by eight commits at the start of W66. Those commits were reviewed, not recreated: GatherBuddy repair Sequence; signed negative-parent protection publication plus fail-closed unknown coverage; and Windows file-identity stabilization with retained snapshot safety checks.

W66 test-only `b0c4b7c9` reproduced the missing outer sale-visit backoff at 1/9 with8 intended assertions,0unexpected. Final production is the GatherBuddy-only +20/-1 delta through `42f38223`; `37511a5f` corrects a whitespace-brittle test assertion only. Final integrated35360041922/art10553948558 passes17/17 entries,101 Wholesome+3 QuestLog groups and75 analyzers. Host35360041925/art10553963467:0errors3304warnings,compile only.

The new policy suppresses immediate full-bag GatherBuddy re-entry for two minutes after a terminal still-open sale pass, matching the existing per-stack retry interval. It is not sale acknowledgement, deletion or permanent blacklist. Persistent result -1/2 inside one active SellAllItemsStep session remains open, as do post-submission result loss and native slot/cursor freshness.

Next priority: bounded no-progress handling for persistent unknown/locked sale observations, preserving exact session ownership and existing result assertions. Do not make unknown=empty/success, do not delete items, and do not use the two-minute stack retry as an instruction to idle at a merchant.

Original3.3.5a/build12340; TC3.3.5 primary, ACWotLK secondary. Masterf462a9bb unchanged; preserve backups/exclusions, PR51 draft/unmerged, no force push/deployment/installed addon/binary/mesh changes. Broader quest/buff/gear/underwater/LOS/full GatherBuddy/native acceptance scope remains open.
