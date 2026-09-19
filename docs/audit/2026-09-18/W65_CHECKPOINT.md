# W65 — verified GatherBuddy sale phase ordering; wider audit remains open

18 September 2026. Repository `jeofwong/CopilotBuddy-private`, ID1367174964. Draft PR51 on `audit/next-55-equipment-observation-20260917`. Original WoW3.3.5a/build12340; TrinityCore3.3.5 primary, AzerothCore WotLK secondary.

## Reconciliation: W64 was already published and verified

The initial visible history ended at W64's failed first integrated attempt. Fresh native reads found newer W64 documentation head `310a19722abd88f7302be412a099f7907e442654` and a successful unchanged second attempt for tested code `4320f619e0ee6a58dd848ce9d993dcc93489c9cf`. The initial incomplete-status message was corrected after that live reconciliation. W64's two-line stepped merchant visibility/player/NPC guard was not recreated. W63's sixteen recovered commits, W62/W61 and all prior requirements remain preserved.

W65 actually published a new test-only commit and a separate production repair, and executed new Windows runs. Later documentation updates do not constitute another C# execution. Before the documentation update, the native branch remained `67be2b9eb430d31fb52d5e12c00a94d0e2eb9907`; master remained `f462a9bb4eb18acac9069f495177df35672286d5`.

## Narrow production repair and root cause

`GatherbuddyBot.CreateSellBehavior` constructed a two-child `PrioritySelector`: an arrival Action followed by the existing service Sequence. Arrival reports Success after caching an in-range vendor. The actual selector terminates on a successful child, so arrival success skipped service; an arrival Failure instead allowed fallback into service. Running approaches retained the first phase. This composition contradicted the method's required arrival-then-service ordering.

Test-only commit **`a25c46601e2f17575842f0b1941e81f47033c42c`**, tree `2c98e035f4c71348f999f41419afa2cb0b0a7305`, adds only `Tools/WholesomeQuestRecoveryRegressionTests/GatherbuddySellPhaseRegressionTests.cs`.

Repair **`67be2b9eb430d31fb52d5e12c00a94d0e2eb9907`**, tree **`d60c801853f857ec50037b7d42ad61eb309e5709`**, changes only that method's outer `PrioritySelector` to `Sequence`. Successful arrival now admits the existing service sequence; failed arrival cannot fall through. Every native leaf, existing service action, retry/protection rule and other behavior remains unchanged. The repaired file has exactly one word replaced, preserving its mixed CRLF/LF line endings.

Full original GatherBuddy blob: **`c7ee45154129563a09072db9c62298a88d61fd85`**. Repaired native blob: **`791e8511312a215854574cbc9a46c04832c8df83`**. Native comparison confirms one production path, +1/-1. This is the W64 source candidate, now actually tested and published; do not reapply it from an older handoff. W64's unexecuted14-case proposal was not falsely counted: W65 uses a new20-case fixture.

## Test scope: actual compiled builder and composites, controlled native leaves

The fixture invokes the compiled private `CreateSellBehavior` on an uninitialized host object, avoiding constructor file loading. It verifies the expected two-phase structure, retains the actual outer group and inner service Sequence, and substitutes the external arrival Action and ten native service leaves with controlled TreeSharp actions. It ticks the actual compiled composites rather than a copied selector implementation.

Twenty scenarios cover immediate/delayed arrival success and failure, running approaches, pending sale continuation without repeated arrival/interaction, service failure, explicit Stop/restart, a second visit, context identity, exact cancellation/interruption propagation at either phase, independent constructed tree lifetimes, and recovery after failed arrival.

This is **not a live vendor visit**. Native movement, sleeps, gossip, Lua dispatch, inventory changes, repair transactions and merchant acknowledgment were not executed. Explicit Stop controls do not prove that the full GatherBuddy root notices combat/death/profile changes while a child is Running. Controlled independent trees do not prove native static cached-vendor isolation.

## Actual new Windows results, including the failed first repair attempt

| Phase | Run / attempt | Artifact | Verified result |
|---|---|---|---|
| Test-only red, a25c4660 |35344230695 /1|10546657555|4/20;16 intended assertions;0 unexpected. All100 prior groups and other16 entries pass.|
| Repair67be2b9e, first attempt |35345417008 /1|10547260118|20/20;all101 aggregate groups pass, but one analyzer ERROR means integrated failure.|
| Same repair, unchanged rerun |35345417008 /2|10547520570|17/17 integrated entries;101/101 aggregate groups;74/74 analyzer tests pass.|
| Host67be2b9e |35345417019 /1|10545824241|Build exit0;3304 warnings;0 errors. Compile only;tests_run=false,game_attached=false.|

W65 attempt1 failed `test_addon_evidence.InventoryTests.test_no_wtf_read_or_toc_path_following` with `IndexError: list index out of range` when accessing the expected TOC metadata. This is distinct from W64 attempt1's `test_excludes_binary_assets` assertion `0 != 1`. The scanner's detailed issue list and file/stat identities were not logged in either failed test. Both original failed archives are retained. A single unchanged W65 rerun was launched and passed; **the cause remains unproven and no analyzer repair is claimed**. Do not infer filesystem timing, antivirus, CPython, OAuth, billing or throttling as the established cause.

The reviewed scanner rejects changed file identities before/during its bounded read and excludes links/reparse points. A missing accepted TOC snapshot can therefore produce an empty TOC list, but the CI error alone does not identify which rejection path occurred. The next analyzer investigation needs failure-context diagnostics and deterministic tests before changing behavior. No assertion, snapshot safety guard, account-data boundary or workflow was weakened to obtain this passing run.

All **1790 source/configuration index entries** and all **113 normalized members** match between the failed and successful W65 attempts. Across the test-only/production repair pair, only GatherbuddyBot.cs changes:1789 indexed inputs and all113 normalized members are identical, including the complete new fixture. All100 prior named aggregate outcomes are retained. Across W64 to the test-only addition, only the new fixture is added among indexed inputs;110 of112 prior normalized members are identical, while the group driver and manifest legitimately change to register the new group. Do not say all112 were unchanged across test addition.

The final green archive contains **157 covered internal-manifest members**,113 normalized members and1790 indexed inputs. Six retained W64/W65 original archives have checked SHA256/CRC and complete internal manifests where present. The host has no internal manifest, and none is claimed. This is a narrow evidence package, not a full current checkout. The complete GatherBuddy file and new fixture were separately bound to the CI source index and native blobs.

### Archive SHA256 identities

- W65 test-only red: `0e55f8daa5ac1d2fe01ef1ca24a6ae9e596b9a458da8cac76c7093fe2c5a539a`.
- W65 repair attempt1, analyzer error: `14706d241bccf62584130bce6d3340e736b6a02704cc102a5630896aa10bab74`.
- W65 repair attempt2, integrated pass: `7f459a18c8b4e0d145762a112f3c1b9f358f78cf1ec9a8317c183d2fe1fda13b`.
- W65 host: `77126bfff4c53448d2a69e16007ac659758233752962ada933ad53d7f4ed5a01`.
- Retained W64 failed attempt1: `58e515208d9f089e5ab3d43dfdb325082b30a94b204a2ac87456f79a4ac0536b`.
- Retained W64 passing attempt2: `3d70a4b0f1ead2ee71145012bf66128f90df521d2d41450514488d510cb8f77f`.

`ARTIFACTS.json` and `VERIFICATION.json` in the downloadable package retain exact IDs, source identities, inventories and comparisons. `python tools/verify_w65.py --out /tmp/w65-verification.json` repeats archive/source validation, not C# or gameplay execution. Ten portable verifier control tests pass. The verifier normalizes decoded CRLF logs for matching and compares unique named case inventories rather than stdout/stderr interleaving order; original archive bytes and fixture hashes remain unchanged.

The supplied W63 five-archive recovery verifier also passed again. Its original prerequisite red21/32 ->green32/32 and old Lua5.4 probe18/30 ->candidate30/30 were revalidated as historical evidence, not new W65 C# or original-client execution. The supplied W63 candidate is already superseded by the published W64 merchant repair.

## Implementer review and concrete remaining work

The phase-order change is consistent with actual TreeSharp success/failure/running semantics. Its diff and test identities were checked, but no independent reviewer or original-client acceptance was obtained.

1. **Repair-side phase composition:** `CreateRepairBehavior` retains the same arrival-Action/service-Sequence PrioritySelector pattern. This is source-reviewed and remains untested/unrepaired by W65. `CreateMailBehavior` has a single Action and must not be mechanically included in the same diagnosis. Obtain a separate actual-builder red before altering repair behavior.
2. **Full-root preemption and session lifetime:** the full root's priority/cancellation behavior, static cached vendor, actor/profile/NPC change after a yield, repeated service visits and native cleanup ownership remain separate. W65's controlled explicit Stop tests do not certify all of them.
3. **Whole-visit progress:** persistent unknown/locked items can keep a stepped session Running; LevelBot's full-bag stop occurs only after a terminal step. GatherBuddy's mail cooldown is not a sale cooldown. Public void bulk facades and separate MerchantFrame instances still need explicit outcome/lifecycle review. A120-second stack-key retry gate is not complete visit-level liveness or permanent refusal memory.
4. **Native transaction boundaries and analyzer stability:** preserve lost-result ambiguity after submission, physical slot/link/count/cursor freshness and actual acknowledgment. Investigate the two retained analyzer failures with report/stat diagnostics rather than repeated blind reruns or weakened identity checks. No item deletion, invented acknowledgment or arbitrary interface closing is introduced.

Keep all existing prerequisite/quest/retry/ownership protections. Positive predecessor IDs require authoritative rewarded history; negative PrevQuestID requires an accepted parent with the int.MinValue guard. Unmet parents are not child data failures; unknown history and incomplete logs remain distinct. Loaded-NPC confirmed absence after distinct interactions is not a universal hidden-condition detector. Grouped/alternative dependencies and exact dataset/core/realm provenance remain open.

W65 does not complete special-item/gossip/event/escort strategies with authoritative credit, PallyPower assignment mapping, permitted Carbonite hints with map/floor/terrain validation, all-class stronger/exclusive buffs, equipment/loadout/caps/rewards, underwater recovery, full gathering/rest/remount, native LOS/UI/cursor acceptance or the exhaustive audit. Read preserved W64/W63/W62/W61 and WOTLK_335A_RESEARCH_POLICY, TRINITYCORE_335_COMPATIBILITY and QUEST_DATA_PROVENANCE_335. Realm/addon/client names do not identify hidden server schema/scripts.

## Publication safeguards

PR51 remains draft and unmerged. Master is the approved PR47/README checkpoint `f462a9bb4eb18acac9069f495177df35672286d5`. Preserve backup refs c43c50d8/8382a7ec/518baec5; exclude25/43/45; do not remerge44/47 or infer PR51 merge approval. No force push, deployment or installed addon/binary/mesh change. Native publication and the actual unchanged Windows rerun worked; historical missing-publisher claims do not describe this continuation.

The root W64 documents are archived using their exact blobs under `docs/audit/2026-09-18/pre-w65/` when publishing the W65 pointers. Re-read live head/master before the next write and at the end; never recreate either the W64 merchant fix or this W65 sale-phase repair from an older local candidate.
