# W55 — verified master merge and equipment safety continuation

17 September 2026. Repository: `jeofwong/CopilotBuddy-private` (ID 1367174964). Target: original WoW 3.3.5a build 12340. Read `docs/audit/WOTLK_335A_RESEARCH_POLICY.md` before mechanics or API changes.

## Merge and live state

PR47 is already merged, not awaiting another merge. The native merge commit is **f462a9bb4eb18acac9069f495177df35672286d5**, tree `552eeab1233c7c282897dca0ed9f4334c5e8ed43`, with parents master `518baec5` and PR47 head `5ae24752`. It preserves the 108-commit W47-W54 history. The merge was present when this continuation resumed; it was not repeated or misreported as a new write.

The README blob at both the prior master and merge is `8b137891791fe96927ad78e64b0aad7bded08bdc`. Three backup refs were freshly read: pre-approved-merge at `c43c50d8d5d6775055f19bf018b52930a264d4a4`, pre-W42-merge at `8382a7ec05a64212ea0a237159dca427a0767425`, and pre-W54-merge at `518baec545cedc8fe219afc0861c0e8cb9475a8f`. Preserve them. Exclude PR25/43/45 and do not remerge historical PR44.

The original post-merge integrated run **35211182024**, artifact **10491763404**, was recovered and independently checked: all **17/17 entries pass**, with **1761 input hashes, 92 normalized members and 80 aggregate group outcome inventories identical** to verified premerge code `06ab57a3`. This is actual execution on master, not premerge results relabelled as postmerge.

Continue on the already-created **draft PR51**, branch `audit/next-55-equipment-observation-20260917`. Latest verified code is **8e63d5158c7baec8f48f81f01e71089bf4aeb963**, tree **7f61ab8468b839468174bdf5cf87bec9fc41f0c7**. The follow-up changes remain unmerged. No deployment, installed-binary/mesh replacement, force push or new master write occurred in this continuation.

## Recovered work: do not recreate it

PR51 already contained an equipment-observation guard and shared physical-slot alias fix before this continuation. Explicitly missing player/inventory/equipment/list/item metadata is distinct from a genuinely empty slot. Chest/Robe and Ranged/Thrown/RangedRight/Relic comparisons share the host's physical slot identities without granting extra item/class eligibility. Final runs retain the **65-case observation group** and **71-case slot group**. The earlier clean red runs for those original repairs were not newly recovered here; this report does not invent their execution.

## New repair 1 — unknown equipment must not authorize Disenchant

The previous observation guard prevented false Need/equip decisions but returned an unknown score into the existing nonmatching fallback. That fallback could still select Disenchant when enabled. Unknown is not evidence of a confirmed non-upgrade.

The already-committed test-only revision **b99b2f9f** records **44/65 passing, 21 intended assertion failures and zero unexpected errors**. New repair **b138c7fb60a32c902b914de89fc42e0c5af824ce** changes only `SmartLootRoller.cs`: preserve a known-comparison flag, and deny Disenchant when the equipped comparison is non-finite. The user's configured Greed/Pass fallback and existing client permission checks remain. Confirmed non-upgrades retain the configured Disenchant policy. The unchanged tests pass **65/65**; all 17 integrated entries pass.

This is equipment-observation admission, not proof that dropped-item stat parsing is complete or that every score is valid. All numerical weights, saved enum values and class eligibility remain unchanged.

## New repair 2 — all replacement checks must consider the displaced hands

`GetMinEquippedScore` already counted both displaced hands for a two-hander, but `IsSlotEmpty` checked only the main hand. Thus a missing main-hand weapon could authorize equipping a weak two-hander over a valuable off-hand. Item-level tie-breaking also examined an empty off-hand instead of the two-hander it would displace.

New **EquipmentHandReplacementRegressionTests** compiles the four actual SmartLoot owners and reuses the unchanged external fixture. It executes the real scorer and auto-equip consumer. The 38 cases cover off-hand-only layouts, two-hander replacement, known empty/unknown metadata, score/level ties, one-hand controls and retained legitimate upgrades.

The first test commit **eac0fd77** failed dynamic fixture compilation before cases executed: an exception-helper type needed qualification inside the generated source. **81add3ad4feb50d24648699b7bb16e96af822c1d** corrects only that qualification and produces genuine **19/38 passing, 19 intended assertion failures, zero unexpected errors**. That earlier compilation failure is retained, not counted as behavioral red.

Repair **8e63d515** changes only `PawnScorer.cs`: a two-hand replacement is empty only if both hands are empty; when only the off-hand is displaced its level is retained; replacing a two-hander with an off-hand compares the displaced main-hand level. The existing main-hand tie convention when both hands contain items, other slot policies, item eligibility and all weights remain. The same **38/38 cases pass**. This is not a complete two-item loadout optimizer, actual class proficiency test or live equip result.

Native readback caught one nonfunctional payload difference: the published PawnScorer lacks the draft's terminal LF. The actual blob **4d94405ab26a7f0f846e7cd429217aab9246a08a**, not the draft blob named in the commit message, is retained and matches final CI. No additional code mutation was hidden in a documentation commit.

## Final validation and evidence scope

At **8e63d515**:

| Execution | Run / artifact | Result |
|---|---|---|
| Integrated Windows x86 | 35218450081 / 10496071675 | **17/17 build/run entries pass** |
| Host compilation | 35218450071 / 10495688972 | **0 errors, 3296 warnings**; compile only |

Final integrated evidence contains **83 passing aggregate groups**, **1764 source/config hashes**, **95 normalized members**, and **139 verified internal-manifest entries**. Retained selected results include equipment observations65/65, physical slots71/71, hand replacement38/38 and SmartLoot decisions79/79. These are overlapping regression inventories, not counts of independent defects or live gameplay coverage.

Each new clean red/green pair has byte-identical original tests, generated fixtures and workflow inputs. Only its one intended production path changes. Other named aggregate outcome inventories match: **81** for the Disenchant pair, **82** for the hand pair. Complete archive SHA256/CRC and inner-manifest coverage are checked. The host archive has no internal manifest; none is claimed. Included before/after sources and tests are bound to the corresponding CI input hashes.

The historical focused comparison workflow explicitly lists older W42/W47 branches and does not trigger on the new W55 branch. No separate current focused run is claimed. Its four projects are nevertheless included in the passing 17-entry integrated execution. Public integrated/host CI is authorized and operational; no workflow or permission change was made here, and no historical guard was bypassed.

Eight original archives are retained in the downloadable package, including the fixture compilation failure and pre/postmerge evidence. The verifier was corrected for stdout/stderr interleaving: a named aggregate failure arriving after the next group's BEGIN belongs to its explicit owner, not the next group. It also rejects unmatched/duplicate outcomes and unmanifested members. **16 utility tests pass**; those are not additional C# gameplay cases. Raw failing logs remain intact.

## Further source findings and acceptance order

The auto-equip consumer separately requests score, emptiness and sometimes item level for each candidate; each request copies the equipment list. A shared, explicitly owned snapshot per decision could reduce repeated reads and allocations while avoiding mixed observations. It needs mutation/owner-change tests and measured observation/allocation counts; no performance improvement is claimed by these repairs.

Stat parsing still needs explicit unknown/partial/malformed/non-finite contracts and native-key alias tests. Auto-equip logs, dispatches an item and clicks a generic EQUIP_BIND popup without a complete captured-item/confirmation ownership contract. The separate SmartLoot and AutoEquip2 scales, cap-aware loadout comparison and quest-reward path are not unified by these fixes. Do not invent optimal Strength/Agility coefficients.

Automatic Wholesome escort/event classification and start/follow/defend/reacquire/completion ownership remain distinct from the retained legacy Escort tests. All-class stronger/equivalent/singleton buff policies need legitimate transition and no-oscillation tests. Safe underwater air/obstacle/shoreline selection remains distinct from water-rest admission. Preserve the existing behavior-tree executor and the original-client research policy while closing those boundaries with actual-owner tests.

The checkpoint is ready to continue from a verified code state with no pending unexecuted test-only addition. **Exhaustive completion, independent approval, measured maximum DPS and original-client acceptance remain unestablished.** PR51 is not merged merely because the earlier PR47 integration was authorized.
