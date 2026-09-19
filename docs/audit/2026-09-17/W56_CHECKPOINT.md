# W56 — combat line-of-sight dispatch and movement recovery

17 September 2026. Repository `jeofwong/CopilotBuddy-private` (ID 1367174964). Target: **original WoW 3.3.5a build 12340**. Read `docs/audit/WOTLK_335A_RESEARCH_POLICY.md` first.

## Current repository state

The requested W47-W54 integration is already complete: **PR47 merged as `f462a9bb4eb18acac9069f495177df35672286d5`** on 17 September 2026 at 10:34:18 UTC. The merge was present at resumption and was not repeated. It retains the 108-commit history and the separate master README edit. The original post-merge Windows integrated run 35211182024 / artifact 10491763404 was recovered and freshly verified: **17/17 entries pass** on the actual master commit. This is recovered execution, not a newly launched master run.

Continue on existing **draft PR51**, branch `audit/next-55-equipment-observation-20260917`. Latest verified code is **`6e9049c1297058bf7128114e88a87bb7fcf2eafc`**, tree **`9b8189c8a43b9014954dc18b5a63f70830ebf37c`**. Its follow-up equipment and combat fixes remain unmerged. No new master merge, deployment, native offset/mask change, installed-binary/mesh replacement or force push occurred in W56.

Preserve backups: pre-approved merge `c43c50d8d5d6775055f19bf018b52930a264d4a4`; pre-W42 merge `8382a7ec05a64212ea0a237159dca427a0767425`; pre-W54 merge `518baec545cedc8fe219afc0861c0e8cb9475a8f`. Exclude PR25/43/45 and do not remerge historical PR44. Re-read live refs before future writes.

## Existing obstruction handling and the reproduced gap

This code already has a geometric spell-sight path: `WoWUnit.InLineOfSpellSight` obtains player/target trace positions and calls `GameWorld.IsInLineOfSpellSight`, which negates a native collision result. It is not merely a facing or distance test. Missing executor and caught trace errors conservatively report a hit/blocked result. These are inspected source contracts, not a new native geometry execution.

The shared Singular Cast path checks sight and range before setup, but its terminal backend deliberately does not repeat `CanCast`. Dismounting, targeting setup or logging could therefore invalidate the earlier permission before submission. The new test-only revision **e62dbb72** executes the exact contiguous tracked Cast/Buff region and real TreeSharp, controlling only observations, setup and backend effects. It produced **24/53 passing, 29 intended assertion failures, zero unexpected errors**. The other 16 integrated entries passed.

Repair **eaeafecb** factors the existing named-spell admission into one helper, retaining its existing self/melee/ranged distinctions. It checks the same selected target again after setup and logging, including sight, range, availability, caller requirements and combat safety. The ID overload repeats its existing host `CanCast` policy. Rejected casts do not execute success bookkeeping or obtain buff retry state. All **53/53** unchanged cases and all **17/17 integrated entries pass**.

This does not redefine which individual spells require LOS. The existing named melee/self-range exceptions remain; their universal correctness is not established. Ground-targeted casts and direct backend callers remain separate paths. No claim is made that every class rotation or every physical collider was exercised.

## Movement recovery and casting-stop coordination

The original `CreateMoveToLosBehavior` could repeatedly evaluate its selector, move while casting, accept invalid owners or coordinates, and report failed navigation as handled. `CreateEnsureMovementStoppedWithinRange` could stop an approach merely because the target was close, even behind an obstruction.

The new movement tests compile **the complete tracked Movement.cs** with real TreeSharp and WoWPoint. World/settings observations and navigation effects are controlled. The test observer explicitly rejects swallowed TreeSharp diagnostics, so a null dereference returning Failure cannot accidentally satisfy a no-movement assertion.

A caller review found important counterevidence: Druid instance combat buffs use this helper to approach a **dead friendly Rebirth target**. Five positive resurrection/healing controls were added before production repair. A blanket dead-target veto would break that existing caller.

The final test-only revision **3773c67f** produced **33/74 passing, 41 intended assertions, zero unexpected errors**, with all 16 other integrated entries passing. The earlier observer-strengthened revision 4277abfd produced 28/69 with the same 41 intended assertions; it is retained separately. The initial 16e2bfd run was not downloaded/reverified and is not counted as additional evidence.

Repair **6e9049c1** changes only Movement.cs:

- Captures the player and evaluates the selected subject once, then rechecks permission after sight and destination observations.
- Holds LOS repositioning during an active cast or channel; denies missing/invalid/dead actors and invalid/non-finite destinations.
- Retains living allies and dead friendly resurrection destinations; dead nonfriendly targets remain ineligible.
- Propagates Failed/PathGenerationFailed as Failure, allowing the next eligible combat or defensive leaf to run. Accepted movement results retain their handled status.
- Requires visible, in-range, still-current target evidence before stopping an approach.

The unchanged final **74/74 cases pass**. The explicit-selector control measures **four evaluations before versus one after**, a bounded observation reduction rather than an FPS or global performance benchmark. The repair does not introduce a new persistent movement owner, interrupt an in-flight cast, globally stop movement when sight clears, or claim a complete route around walls.

## Fresh final Windows results

| Execution | Run / artifact | Result |
|---|---|---|
| Integrated x86 | 35226403760 / 10499785441 | **17/17 build/run entries pass**, all 85 aggregate groups pass |
| Host compilation | 35226403801 / 10499216509 | **0 errors, 3,296 warnings**; compilation only |

Final integrated source identity is the exact 6e9049c1 commit/tree above. Verification covers the complete archive SHA256 and CRC, all **141 internal-manifest entries**, **1,766 source/configuration hashes** and **97 normalized members**. The host archive has no internal manifest; none is claimed. Both final production payloads and both new test payloads match the native Git blob identities and final CI input hashes.

Across each clean red/green pair, original tests, normalized fixtures and workflows remain identical. Only Spell.cs changes in the first pair, with **83 other group inventories unchanged**; only Movement.cs changes in the second, with **84 other inventories unchanged**. Cases overlap with other suites and are not counts of independent bugs or live gameplay scenarios.

No standalone focused run was launched on the W55 branch; that historical workflow's branch list does not include it. Its four projects are included in the 17-entry integrated execution. Public integrated/host CI remains authorized and operational; no workflow or permission change was required here. A separately surfaced CodeQL configuration warning is not a passing security review or a failure of these tests.

Seven original archives are retained in the package. The portable verifier checks full inner-manifest coverage as well as listed digests; **15 utility tests pass**. Utility checks are not additional bot C# tests. The local environment did not run C#; actual C# evidence comes from the recorded Windows jobs. The package includes the exact changed before/after sources and tests, not a complete current repository checkout.

Native readback found two harmless payload discrepancies from preparation: Spell.cs contains an XML returns punctuation change; Movement.cs omits an obsolete range-param XML comment on a no-range overload. The actual blobs, not draft hashes, are authoritative: Spell **e9acd6b7e550d72396dde2980d559d057407a254**, Movement **f46f937c46c8a442ae1902f2250a76151e392d02**. Both were reconciled and verified against CI; no additional executable change was hidden in a documentation commit.

## Retained W55 work and remaining audit

The prior equipment observations65/65, physical-slot aliases71/71, hand replacement38/38 and loot-decision79/79 groups remain in the passing run. Do not recreate those fixes or overwrite their unknown-data protections. Legacy Escort47, shared buffs54, Paladin support44 and blessing contribution63 also remain; their passing narrower tests do not establish an automatic escort strategy or universal buff optimization.

See W56_LOS_REVIEW.md for the next source-traced requirements: native observation freshness and spell-specific collision rules; safe alternative casting positions with progress/retry bounds; off-target facing; ground-cursor ownership; and remaining post-selection actor/aura revalidation. These are not closed by the new helper tests.

The larger audit still includes automatic Wholesome escort/event lifecycle, all-spec equivalent/stronger/singleton coordination, safe threat/utility ownership, unified loadout/cap-aware equipment and reward evaluation, underwater air/obstacle/shoreline recovery, remaining upstream/native/merchant boundaries, independent review and original-client acceptance. Preserve the behavior-tree executor and original-client research policy. **No exhaustive completion, independent approval, maximum-DPS improvement or complete live obstacle recovery is claimed.** There is no pending unexecuted test-only addition at the final verified code revision.
