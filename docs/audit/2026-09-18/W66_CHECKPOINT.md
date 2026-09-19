# W66 — reconciled intervening work and bounded GatherBuddy sale-visit re-entry

18 September 2026. Repository `jeofwong/CopilotBuddy-private` (1367174964), draft PR51, branch `audit/next-55-equipment-observation-20260917`.

## Reconciliation before new work

W65 documentation head `2f4bf14e4bd2838e083096fa74de065cf3f4063a` was stale when this continuation began. Live head was already `ee815560dfaba3b69d7c5936cbbfa9c9a1398cd3`, eight commits later. Those commits were reviewed before adding new code and were not recreated.

The intervening work forms three coherent test/fix sequences:

- GatherBuddy repair visit: `c1efc628` test -> `d51a6521` repair. `CreateRepairBehavior` now uses the same arrival-then-service Sequence contract previously verified for selling.
- Quest dependency protection: `4c6b1b2e` test, `5b7ee98d` first repair, `1607a7f8` fixture/contract correction, `4c89949b` final repair. The loader publishes absolute nonzero signed PrevQuestID edges without rewriting raw quest metadata, and published coverage fails closed for an unknown quest ID.
- Windows addon-evidence identity: `83dcafd3` reproducer -> `ee815560` fix. Windows snapshot identity no longer relies on creation-time semantics that can disagree between path-stat and handle-stat; POSIX keeps ctime. Link/reparse, size, mtime, regular-file and before/during-read guards remain.

The final pre-W66 head `ee815560` had a passing integrated run. The analyzer now has 75 tests because the Windows fresh-file identity probe was added. These source/CI observations are narrower than live-client acceptance.

## New W66 finding: per-stack retry did not prevent whole-visit churn

W63/W64 already bound repeated sale attempts for an unchanged player/merchant/bag/slot/count/link key for 120 seconds. That controls transaction resubmission, not the outer GatherBuddy vendor visit.

At `ee815560`, a terminal GatherBuddy sale pass could close the merchant and return to the root while bags remained full. `NeedsBagsEmptied` had no sale cooldown, so the bot could immediately select the same vendor again even while MerchantSaleAttemptGate was still excluding the unchanged stack. The separate two-minute mail cooldown did not apply to selling.

This is distinct from repeated result -1/2 inside one active sale session. Persistent unknown/locked observations can still keep SellAllItemsStep Running and remain an open follow-up.

## Test-first repair

Test-only `b0c4b7c93ec14b6a4c7683df6897f545bfbfd498` added `GatherbuddySaleVisitBackoffRegressionTests.cs`.

Actual Windows integrated run **35358935120**, artifact **10553796622**, SHA256 **8ab413c7a9d58a1a48accd6ae4d38530e85110259b57cb4601e437168e0a86f0** reproduced **1/9 passing, 8 intended assertion failures, 0 unexpected errors**. The independent mail-cooldown control passed.

Production repair began at `63fa9693`; `42f38223` restored the file's pre-existing mixed line-ending pattern. The resulting production delta from the clean test-only commit is one file, `Bots/Gatherbuddy/GatherbuddyBot.cs`, **+20/-1**. It adds:

- a finite two-minute completed sale-visit backoff aligned with the existing per-stack retry interval,
- reset of that backoff at GatherBuddy Start,
- admission suppression in NeedsBagsEmptied while the interval is active,
- timestamp publication only after SellAllItemsStep reports a terminal result while MerchantFrame is still visible.

The first full post-repair integrated attempt at `42f38223` reached **8/9** new cases; all 75 analyzers passed. Its sole failure was a fixture bug requiring one exact whitespace form for the Start reset. Production behavior was unchanged. Test-only correction `37511a5f0a4db7b8fe91efd1475f8d2700cad4f6` made that source check whitespace-agnostic without weakening the required reset.

## Verified W66 head

Latest verified code **37511a5f0a4db7b8fe91efd1475f8d2700cad4f6**, tree **71de8d71865929a8b8938ccdd8180b8e76c66ecc**.

Final integrated run **35360041922**, artifact **10553948558**, SHA256 **fd536ab64c075d43f9f967d929b61574ac30116d3681bb3dfc82601e585c24ae**:
- 17/17 build/run entries pass.
- GatherBuddy sale-visit backoff 9/9, 0 assertions, 0 unexpected.
- 101 Wholesome aggregate groups plus the retained 3 QuestLog groups pass.
- 75/75 analyzer tests pass.
- 1,793 indexed source/configuration inputs.
- 116 normalized file members.
- 160 files recorded in the generated files-sha256 inventory; source identity names tree71de8d71.
- no game attached.

Host validation **35360041925**, artifact **10553963467**, SHA256 **6ca4ec4697f452da2434d799d57fd5a60c2a1e0e4b33e507f63f41fa0b008eee**: build succeeded, **3,304 warnings, 0 errors**, compile only.

No test or workflow failure condition was disabled. The W66 test correction is retained explicitly as a fixture correction after the 8/9 run, not relabelled as clean behavioral red/green evidence.

## Semantics and boundaries

The two-minute visit backoff is not a merchant acknowledgement and does not delete or permanently blacklist anything. A completed local pass can still contain refused or merely submitted requests. At the deadline, a full-bag GatherBuddy visit becomes eligible again. Clock reversal fails open rather than creating a permanent lockout.

This repair does not solve:
- repeated unknown/malformed or locked observations that keep one SellAllItemsStep session Running;
- post-submission result loss or stable physical item identity;
- LevelBot-specific full-bag/no-progress policy;
- public void bulk-sale outcome completeness;
- native cursor/UI race conditions or server acknowledgement.

Retain all W65/W64/W63 prerequisite, merchant retry, session ownership and final-context protections.

## Remaining wider audit

Still open: complex/grouped/alternative prerequisite provenance, special-item/gossip/event/escort strategies with authoritative server credit, PallyPower assignment bridging, permitted Carbonite geography/map conversion, full gathering/rest/remount and combat preemption, all-class stronger/exclusive buffs, equipment/loadout/caps/rewards, underwater recovery, native slot/cursor/LOS/UI acceptance, independent review and supervised original-client/server acceptance.

Original WoW 3.3.5a build12340; TrinityCore3.3.5 primary, AzerothCore WotLK secondary. No exhaustive-completion or live-compatibility certificate.
