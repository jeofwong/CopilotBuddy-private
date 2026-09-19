# W59 — quest-item merchant admission and combat target gaps

18 September 2026. Repository `jeofwong/CopilotBuddy-private`, ID1367174964. Draft PR51; branch `audit/next-55-equipment-observation-20260917`.

## Published and executed

Latest verified code **ee31cde9985f233fb7c085160a50c05c54650ea0**, tree **cbe514baa03078eadc4be4a3300e556f2cb4d6b5**. This continuation successfully published tests, fixture corrections and two production repairs through native GitHub actions. It is not a read-only recovery report.

Master remains the approved PR47 merge `f462a9bb4eb18acac9069f495177df35672286d5`. PR51 remains draft/unmerged. Preserve README and backups c43c50d8/8382a7ec/518baec5; exclude25/43/45 and do not remerge historical44/47. No force push, deployment or installed binary/mesh change.

## 1. Pending combat test: honest separation of fixture errors from behavior

The starting a11c3514 test had never executed: the existing normalizer matched namespace declarations inside its raw-string fixture. Commit f8e47ea5 prefixes only those seven embedded declarations with an explanatory C# comment. The initializer remains in the global namespace and the normalizer/workflow guard is unchanged. All26 assertions are retained.

That reached dynamic compilation and exposed CS0104: host `Styx.Logic.Mount` conflicted with controlled `Styx.Logic.Pathing.Mount`. Commit cac8a01d adds an explicit fixture alias in both generated units, without altering the extracted LevelBot region or any assertion. The initial failures are preserved as fixture failures, not claimed as successful behavioral reproduction.

At cac8 the actual combat region and POI decorator, using real TreeSharp and controlled routine/world leaves, report **11/26,15 intended assertion failures,0unexpected**.

## 2. Combat repair

Commit ee31cde9 changes only the combat region of `Bots/Grind/LevelBot.cs`. Rest and combat now share the player-or-living-pet combat predicate. Ground combat retains self-healing and ownership even when FirstUnit is temporarily absent. Existing target-dependent CombatBuff/Combat leaves retain a target gate; the subtree does not force a new target or authorize patrol merely because selection is temporarily empty.

The **same26/26 cases pass**. Controls retain explicit dismount priority, mounted-travel handling, idle/dead-pet behavior, normal rest after combat ends, valid-target routine priority, KillPOI cleanup and normal/blocked-LOS pulls. The full file's preexisting mixed line endings remain byte-for-byte outside the changed region; before86462/blobfcc02655 ->after86815/blobe91f274c is bound to CI input hashes.

This is the actual LevelBot subtree GatherBuddy calls, not a full GatherBuddy/root/Targeting/native HoJ test. It does not ensure a new target will always be found, clear every stuck combat flag, guarantee immediate remount, or establish every actor-replacement/running-child transition. No timer-based guessed combat end or blanket stun ban was added.

## 3. Previously local merchant proposal is now committed and repaired

Commit0268828a publishes the retained W58 20-case proposal, preserving all scenarios and eight controlled fixture substitutions. Only the pending-status comment and embedded namespace formatting differ from the proposal. Actual result: **7/20,13 intended assertions,0unexpected**.

Commit a89c10ba checks visible merchant state at the end of `UseItemOn`'s existing pre-dispatch admission and then rechecks actor/quest ownership. Existing setup boundaries already invoke that admission. A denied generic container request does not increment the count, blacklist the recipient, close another owner's interface or fabricate quest progress. It remains retryable after the window closes. A window opening after an already-submitted invocation does not erase the retained local repetition count or manufacture a second use.

At a89 **20/20 merchant cases pass** while the still-unrepaired combat group remains11/26. At final ee both are green. Original33 quest-progress,44 selection and72 dispatch controls remain. Only UseItemOn changes across its clean pair; the native diff additionally records removal of its terminal newline, with no semantic effect. Do not describe that diff as perfectly preserving every formatting byte.

Tests control known-visible merchant state and record generic API requests; no native sale or loss of a real quest item occurred. Unknown/stale UI observations, bag-slot/cursor identity and atomic final native dispatch are separate boundaries. A local use/count is still not server quest credit.

## Actual CI and integrity

| Revision | Combat | Merchant | Integrated status |
|---|---:|---:|---|
| f8e47ea5 | Dynamic fixture compile error | Not yet present |16/17 run entries pass; all builds pass|
|0268828a | Same dynamic fixture error |7/20,13 assertions |16/17 run entries pass|
|cac8a01d |11/26,15 assertions |7/20,13 assertions |16/17 run entries pass|
|a89c10ba |11/26,15 assertions |20/20 |16/17 run entries pass|
|**ee31cde9** |**26/26** |**20/20** |**17/17 build/run entries pass**|

Every clean behavioral failure has0unexpected. Final integrated run **35303475446/art10531141312**, SHA256 **46e63f40c51f2c6c893f4abdd4d0e9c1418dcbcc19b0a8a1e54cb869f40e5fce**, passes **91 aggregate groups**. Host **35303475426/art10531046186** compiles with **0errors,3292warnings** (not live execution), SHA256 **7265d4ef6f8e06c117969f3b9bb55105ab7383fd3136979eeb35002ef75c9c80**.

All six original archives are retained with authenticated outer digests and CRC verification. Clean integrated archives each have147 fully covered internal-manifest entries,103 normalized members and1777 source/config hash-index entries. Within each production pair, every original/generated test and workflow input is unchanged; only UseItemOn, then only LevelBot, changes. Each pair retains90 other aggregate outcomes and the other named-case inventory. Two included LevelBot snapshots and both exact original test files match CI input hashes. The narrow artifacts do not contain all1777 source files for independent re-downloading/rehashing; the index comparison is stated separately from the included-source checks.

The verifier matches explicit aggregate identities rather than assuming stdout/stderr remain adjacent; the retained fixture-error log interleaves streams. It preserves the error and unique outcome instead of dropping failed groups. Thirteen retained verifier unit tests pass; they are package-utility checks, not additional gameplay cases. No local C# run or separate focused run is claimed; integrated includes the focused projects.

A separate CodeQL check reported neutral with a missing C# configuration comparison warning. Do not equate the integrated/host result with every security check passing or independent approval.

## Quest data answer and next implementation boundary

Read `docs/audit/QUEST_DATA_PROVENANCE_335.md`. Current Wholesome planning reads local JSON; accepted state/progress come through the client; defined Lua requests expose client/server observations but not complete hidden server DB/scripts. TC/AC-specific imported semantics and recipes need explicit provenance, not inferred core identity from realm name or build12340. A fingerprint detects content, not source compatibility.

The new document is a source-backed architecture requirement, **not an implemented core detector/importer/recipe engine**. The local dataset's original producer/revision remains unproven. Automatic special-item, interaction, event and escort support remains incomplete; current guarded `UseItemOn` is an explicit profile primitive. Live success must come from current quest/event evidence, not an attempted use or player arrival.

Prior Sanctuary/Greater/aura/Protection/MP5/equipment/buff/LOS fixes remain. Their existing suites run in final integrated green, but no new independent red histories or universal all-class/rank/DPS/server guarantees are asserted.

Next priorities: source-backed versioned quest strategies/provenance with actual data fixtures; remaining UseItemOn movement/LOS/GameObject/cursor/acknowledgement and blocking waits; full GatherBuddy/Targeting/rest/mount transitions; all-class exclusive/strength policies; cap/loadout equipment/reward unification; safe underwater air/shoreline recovery; native/merchant freshness and independent original-client acceptance.

No pending unexecuted test-only addition remains at final ee. No exhaustive completion, automatic all-quest support, measured DPS gain or live TrinityCore/AzerothCore certification is claimed.
