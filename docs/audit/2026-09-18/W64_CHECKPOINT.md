# W64 — published stepped-sale context repair; vendor-visit audit remains open

Evidence reviewed 18 September 2026. Repository `jeofwong/CopilotBuddy-private` (1367174964), draft PR51, branch `audit/next-55-equipment-observation-20260917`.

## Reconciliation and actual publication

The supplied W63 recovery was local-only, but live PR51 had already advanced to documentation commit `518d1fcdb3bce7248094544b7ffd20ead9c5b3c6`. That commit updated the root pointers and PR body to W63. It changed no production/test inputs relative to `847e9c3aba9c44a1f92d5c810afccdf372465fee`. Its W63 documents and all intervening implementations are preserved, not recreated.

Native publishing worked in this continuation. New test-only commit **e274b7a9c9270f37e593e0d576bf24adc5fca12b** adds `Tools/WholesomeQuestRecoveryRegressionTests/MerchantStepContextRegressionTests.cs`. New repair **4320f619e0ee6a58dd848ce9d993dcc93489c9cf**, tree **4f677d888a737f14d5b5bfb202417786efe1accb**, adds exactly two lines to `MerchantFrame.cs`. It was published by a non-force branch update after actual Windows assertion-red evidence.

The complete original MerchantFrame file was fetched and reconstructed, and its Git blob matched `cf9b57ddb793f817cb7f14c1247007e3e91539f4` before patching. The repaired native blob is `0276a8825e2d8ac8da00f53492b523b3f9480f37`. Native compare confirms +2/-0 in one production path, with no changed existing tests or workflows. The supplied W63 excerpt is no longer the sole patch-application evidence.

## What changed

The stepped seller now rechecks merchant-window visibility and the captured player/NPC GUIDs after candidate metadata/quest/stack observations and retry exclusion, immediately before `UseContainerItem`. These are the same two checks already used by the bulk query. Bulk code is unchanged. The repair does not alter quality/protection rules, retry duration, receipt bookkeeping, item deletion, cursor handling, interface ownership or core detection. A returned receipt still records a submitted request, not merchant acceptance.

## Actual executions and retained counterevidence

| Phase | Commit | Windows outcome |
|---|---|---|
| Test-only | e274b7a9 | New structural group 3/6, three intended assertions, zero unexpected errors; 99 prior groups and other 16 entries pass |
| Repair, attempt 1 | 4320f619 | Structural group 6/6 and all 100 groups pass; separate analyzer test fails, so integrated result is NOT all-green |
| Same repair, attempt 2 | 4320f619 | 17/17 entries, 100/100 aggregate groups, 74/74 analyzer tests pass |
| Host build | 4320f619 | Exit 0, 3,304 warnings, zero errors; compile only |

The first repair run failed `test_addon_evidence.InventoryTests.test_excludes_binary_assets` with `0 != 1`: the report contained no text-file entry where the fixture expected one. The report's issue details and individual file identities were not logged. An unchanged rerun passed. All 1,789 source/configuration input hashes and all 112 normalized members are identical between attempts. **The cause remains unproven; the rerun is not an analyzer repair.** Do not infer Windows metadata, antivirus, filesystem timing, OAuth, billing or throttling as an established cause. No test, scanner safety check, workflow condition or assertion was weakened. Both original archives are retained.

Across the merchant assertion-red/green pair, only MerchantFrame changes among 1,789 indexed inputs: 1,788 are identical. All 112 normalized members are byte-identical, including the new fixture. From prior 847 to the test-only commit, the only input addition is that fixture; the aggregate driver/manifest legitimately change to register it. Do not call those generated registration files unchanged across test addition.

The actual repair Lua exported by Windows CI was run through the same controlled Lua5.4 probe. Prior exported script: **18/30, 12 intended failures, zero unexpected**. Repaired exported script: **30/30, zero unexpected**. The repaired script is byte-identical to the supplied narrow candidate; bulk export is unchanged. This is **system Lua5.4 via ctypes, not original Lua5.1 or WoW**. The probe shim never entered production. The C# group tests generated-source ordering, not native Lua dispatch. No addon/client/server was attached and no in-game sale was executed.

### Exact current artifacts

| Phase | Run / attempt | Artifact | SHA256 |
|---|---|---|---|
| Merchant structural red | 35337737185 / 1 | 10543786713 | de44f4f09992fbfc9cce41fab18d52cf9e42df2ab0e466eaecf8231a008655b7 |
| Merchant green, analyzer red | 35338415023 / 1 | 10543912635 | 58e515208d9f089e5ab3d43dfdb325082b30a94b204a2ac87456f79a4ac0536b |
| Integrated green rerun | 35338415023 / 2 | 10544426266 | 3d70a4b0f1ead2ee71145012bf66128f90df521d2d41450514488d510cb8f77f |
| Host | 35338415009 / 1 | 10543603614 | dc96bb0b5f20074b01151fada8ac03a3d76182e0ae7582b9ef090eb31064b74f |

The green archive covers 156 inner-manifest members, 1,789 source/configuration index entries and 112 normalized members. All archive SHA256 values, CRCs and complete inner manifests were verified. The host has no inner manifest, and none is claimed. Local evidence-verifier controls pass 12/12; those are utility tests, not additional bot/gameplay cases.

## Preserved prerequisite and refusal controls

The current run retains QuestActiveParent 32/32, MerchantSaleRetry 27/27, MerchantBulkRetry 27/27, VendorSaleResult 12/12, VendorSaleContinuationOwnership 31/31, VendorBulkContinuationOwnership 16/16 and VendorSaleEntryOwnership 20/20. Their scopes differ; their sum is not a live-sale or all-quest certificate.

The supplied original prerequisite red/green archives were revalidated: ba530892 -> 520958f6, 21/32 -> 32/32, 11 intended assertions and zero unexpected; only QuestScheduler changes and all 105 normalized members match. Positive prerequisites require rewarded history; a negative parent must currently be accepted, with the int.MinValue guard retained. Missing/unknown observations, active-parent acceptance/removal, extra prerequisites, ready-parent turn-in and unrelated work stay distinct. Unmet prerequisites do not themselves add a failure or permanent blacklist. The loaded-NPC mismatch tracker still distinguishes unknown from confirmed absence and requires three distinct confirmed interaction cycles. It does not identify every hidden script condition.

The 120,000ms guard remains per observed player/merchant/bag/slot/link/count key, bounded to 256 keys. Zero known price is skipped; unknown price is pending. Expiry, moved/changed stacks, different merchants and lost results remain separate. The W63 remote evidence additionally records historical stepped/bulk red archives; those records were read and preserved, not relabelled as newly downloaded/verified here. The uploaded five-archive package remains intact.

## Source-confirmed remaining vendor boundaries

1. **GatherBuddy arrival/service composition.** Current `CreateVendorBehavior` reaches `CreateSellBehavior` for full bags. That method returns a PrioritySelector containing an arrival action followed by a service Sequence. Arrival returns Success when in range. The actual PrioritySelector implementation terminates on a successful child, so that success bypasses the later service phase; an arrival Failure instead permits fallback into it. This is a source/control-flow finding, not a newly executed native reproduction. A one-word Sequence candidate and a 14-case actual-builder/TreeSharp phase-composition fixture are retained, but **neither is compiled, executed or published as a production/test change**. The fixture substitutes external arrival/service phases: it would not validate real movement, gossip or sale acknowledgment. Verify newer refs, then obtain Windows test-only results before the repair. Original full GatherBuddy blob: `c7ee45154129563a09072db9c62298a88d61fd85`; candidate blob: `791e8511312a215854574cbc9a46c04832c8df83`. Preserve its mixed line endings and every other behavior.
2. **Pending visit versus completed pass.** Vendors.ContinueSellSession returns pending for repeated unknown/locked results without a whole-visit progress deadline. LevelBot maps that to Running; its existing full-bag stop is reached only after the step terminates. GatherBuddy has no equivalent post-sale full-bag stop/cooldown in the reviewed sequence; its separate mail cooldown is not a sale cooldown. Do not collapse these callers into one universal completed-visit loop diagnosis.
3. **Bulk outcome loss and independent owners.** Vendors.SellAllItems is a public void facade and clears its stable session/ForceSell after the void merchant callback; callback ownership correctness does not establish sale completion. Wholesome.SellByQuality uses MerchantFrame.Instance with additional live quest-log/item protection, while Vendors owns a separate MerchantFrame instance. The retry dictionary is per instance, so shared step/bulk logic is not a process-wide receipt service. Templar also calls MerchantFrame.Instance directly. Preserve public void compatibility until caller/result migration is explicitly designed and tested.
4. **Unresolved native boundaries.** Result loss after a possible submission, slot/link freshness, cursor state, permanently unknown/locked metadata, repeated no-progress visits, and original-client behavior remain open. Never fabricate acknowledgments, permanently blacklist unknown items or close arbitrary interfaces to conceal these gaps.

A historical canonical source export was recovered and all 4,026 exported Git blobs/sizes/digests checked. Of the 847 input index, 1,539 downloaded source/config files match exactly; 249 were absent or different. Current sources were separately fetched where necessary. This is not a full current checkout and not a claim that all matching files were reviewed. No source-export workflow was enabled and no meshes, installed addons or native binaries were changed.

## Protection and next actions

Master remains the approved PR47/README checkpoint `f462a9bb4eb18acac9069f495177df35672286d5`; retain backup refs c43c50d8/8382a7ec/518baec5. PR51 stays draft and unmerged. Exclude25/43/45, do not remerge44/47, and do not infer PR51 approval from older merges. No force push or deployment occurred.

Original WoW3.3.5a/build12340; TrinityCore3.3.5 primary and AzerothCore WotLK secondary. Retain WOTLK_335A_RESEARCH_POLICY, TRINITYCORE_335_COMPATIBILITY and QUEST_DATA_PROVENANCE_335. Realm names, addon names and client versions do not identify backend schema or hidden scripts. Preserve W61/W62/W63, including broader special-quest, grouped dependency/provenance, PallyPower assignment, permitted Carbonite hint/map conversion, all-class stronger/exclusive buffs, equipment/reward/loadout, underwater, full GatherBuddy/rest/remount, LOS, independent review and supervised original-client/server acceptance requirements. None is completed by the two-line merchant guard.

Implementing-assistant source review and evidence verification were performed; independent review and exhaustive completion remain open. Native writes demonstrably worked; do not restart reconnect loops or treat historical publisher absence as the current state. Re-read live refs at continuation and before publishing.
