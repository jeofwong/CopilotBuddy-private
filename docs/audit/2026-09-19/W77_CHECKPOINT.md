# W77 — verified Ret and collection-restart repairs; partial review

Repository `jeofwong/CopilotBuddy-private` (1367174964), draft PR51, branch `audit/next-55-equipment-observation-20260917`. Reviewed19 September2026. Reconcile live refs before continuing.

## Scope and exact state

Owner's cutoff is **18 September2026 21:58 Malaysia /13:58 UTC**. Cutoff ancestor: `4c6b1b2e75af4dd0eaffde11d38ebbc84a21f4c0`. First subsequent commit: `5b7ee98d9560d14221875632beabbc50d4f79c0d`. The initial W77 entry bf1cd682 was125 commits ahead; this continuation entered atdcf95177 (129 ahead). Verified code below is132 ahead. These are ancestry counts, NOT counts of individually accepted commits. Model authorship is user-supplied, not independently proven by Git metadata.

**Verified code/test head: `f0a8b316c42498b85798fe067d5cbdf35a410d7f`.**
**Verified tree: `4d368e01b4172b641d329c66da5053e451ff70f7`.**
Documentation after this is not another tested production revision.

Focus **Wholesome questing, navigation and Singular combat**. Original WoW3.3.5a/build12340 only; TrinityCore3.3.5 primary, AzerothCore WotLK secondary. Read `docs/audit/WOTLK_335A_RESEARCH_POLICY.md`, `TRINITYCORE_335_COMPATIBILITY.md`, `QUEST_DATA_PROVENANCE_335.md`, `ADDON_EVIDENCE_335.md`, and `W77_REVIEW_SCOPE.md`. Earlier requirements remain. Do not resume AuctionHouse/ProfessionBuddy from stale W76 prose.

PR51 is draft/unmerged. No game was attached, no deployment or installed addon/binary/mesh change occurred, and no force push was used. **Master's commit changed during a publishing mistake, but its exact tree was restored; see section7. Do not describe master as untouched.**

## 1. Exact-head verification

| Evidence | Actual result |
|---|---|
| Integrated Windows x86 run35452490981 / artifact10586823183 |17/17 entries |
| Host run35452490898 / artifact10587595640 |exit0;3344 warnings;0 errors; compile only |
| Generated collection admission |34/34;0 assertions;0 unexpected |
| Compiled Ret registration |9/9;0 assertions;0 unexpected |
| Retained acknowledged-equip timeout |10/10, actual tracked control flow |
| Retained equip ownership checks |19/19, compilation/source contract |
| Retained container identity checks |20/20, pure/source contract |
| Retained MrItemRemover deletion checks |14/14, compilation/source contract |
| Python analyzers |89 tests;OK |

Integrated SHA256 `ef8a1e08a1bb568806b589078af6608635ae4b76a84b2e0c9afb23ecafdcaa11`; host SHA256 `3cd85512a063a0054018888d5d4fa6b34c992db54c27d11643b5280e6a3ead88`. The downloaded integrated ZIP passes CRC and all182 inner-manifest hashes, and has1819 indexed source/configuration inputs and138 normalized members. The host ZIP has four files and no inner manifest. All results are bound to the exact source identity and `game_attached=false`.

## 2. Fixed: Ret's normal rotation registration

Historical `dde3c7544cb02ee44f4a134fa715943245a50926` inserted the optional `CreateRetributionPaladinIsolationPull` between five registration attributes and the full normal rotation. The attributes consequently belonged to the Exorcism-only helper, not `CreateRetributionPaladinNormalPullAndCombat`. Its historical source and the current pre-fix compiled CLR attributes both demonstrate this.

Clean red **dcf95177088de3b3fc47ac69929e8b642ac6a9d9**: integrated35450462957/art10587085138, SHA256 `552bc22740f1c0c19ada7bf1e5ddc1bb25508f0a75556acad2d6126b32b553d9`: **5/9,4 intended assertions,0 unexpected**, other16 integrated entries passing.

Repair **0673382279e11463531056d507a65c220be51911** moves only the five Class/Spec/Pull/Combat/Normal attributes back to the full factory in `runtime-snapshot/Routines/Singular wotlk/ClassSpecific/Paladin/Retribution.cs`. Rotation bodies, spells, coefficients, optional opener, tests and workflows are unchanged; an incidental final-newline removal is visible.

Green integrated35451612047/art10587396934, SHA256 `592c8a3ed7464a50d66cc925a18a9e4680dcb9091f5bfb0ae7251be036cf66d9`: **9/9 and17/17**. Host35451612053/art10585919686, SHA256 `cbcb3ba61a9bb264e3315f3113aedc76254bbfa733855a36d15d226223b7a4e8`: exit0,3344 warnings,0 errors.

Both integrated archives pass CRC and181 inner hashes. Only Retribution.cs differs among1818 indexed inputs; all137 normalized members are byte-identical. This is factory-registration verification, not a complete rotation tick, maximum-DPS proof, or acceptance of the whole isolation coordinator.

## 3. Fixed: fulfilled collection steps could re-enter travel

Generated ordinary and source-bound custom objective blocks were enclosed only by `HasQuest(q)`, with transport preambles before the inner action. Already owning the required collection items did not prevent travel re-entry after regeneration/restart.

Test-only **0298c2d25fe46cecc74198d535e48138b6aa7763** adds `QuestCollectionReplayRegressionTests.cs`. It invokes the real ProfileBuilder, compiles the actual emitted conditions with the production SourceCompiler, and executes controlled HasQuest/GetItemCount observations. The34 cases include ordinary/gameobject collection and bound UseItemOn/GossipEvent collection goals, below/exact/above target counts, abandonment/reacceptance, item loss after restart and enclosing transport/action order. Kill and zero/unknown-count contracts remain controls; the tool ID differs from the collection ID.

Clean red35452173759/art10587147423, SHA256 `6340442bbf4b080b7bbc6e8029e7025cb55c407fab8d258b03b68e0c189acef6`: **18/34,16 intended assertions,0 unexpected**; other16 entries pass.

Repair **f0a8b316c42498b85798fe067d5cbdf35a410d7f** changes only ProfileBuilder.cs: one shared condition builder and three call sites. Positive, known collection goals emit `HasQuest(q) && GetItemCount(collectedItem) < requiredCount` before transport and ordinary/custom actions. The existing C# profile helper is used; no new Lua API is assumed. The shovel/tool item is not substituted for the objective item.

Green is34/34 and17/17 at the exact head in section1. Only ProfileBuilder.cs differs among1819 indexed inputs, and all138 normalized members are byte-identical red-to-green. No test, assertion, pickup, turn-in, kill guard or behavior body was modified. An incidental final-newline removal is visible.

**Limits:** this repairs generated-profile collection admission, not every event-only or handwritten profile. UseItemOn/GossipEvent still capture a new ObjectiveProgress baseline on start and need a separate already-completed individual-objective observer. Inventory-observation completeness, changes during an action and live Stop/Start acceptance are not newly certified. No universal once-ever cache was added; abandonment, reacceptance and later item loss remain possible.

## 4. Cursor evidence and remaining defect

'Cursor' here is the original WoW held-item/targeting state, not the desktop mouse. Lua `PickupContainerItem`, `GetCursorInfo` and `EquipCursorItem` operate on that client state; `p.button1:Click()` invokes a UI callback without mouse coordinates. Client-side Lua still depends on shared client UI state and is not server-side execution.

Historical archives were opened again:6622f7824/17;ba08602a16/17;86333b3517/19;6980683419/19. The counts are accurate, but the output labels them tracked-owner compilation/source contracts. The full6622-to6980 chain also contains test-locator/scope corrections and additions; it is not one unchanged-fixture behavioral pair.

Earlier W77 already repaired the acknowledged-but-blocked cursor-return branch bypassing timeout; the10/10 actual-control-flow cases remain passing. Do not recreate it.

**Remaining:** `ReturnDisplacedCursorToSource` accepts any held item different from the pending equipped item's entry when the remembered source slot is empty. That does not identify the actual displaced item. Same-entry ABA, same-slot foreign bind prompts, result loss and cross-plugin arbitration remain open. Do not certify the entire cursor lifecycle or blindly discard the valid safety checks in favor of raw pickup/ClearCursor.

## 5. Gordunni Cobalt capability boundary

The inspected shipped Feralas record, blob `99e02c27e879962c18dd20312116d39f2f26246b`, declares quest2987, StartItem9466, collected item9463/count12 and GameObject144064 ('Gordunni Dirt Mound'). ProfileBuilder can emit collection work, but these fields alone do not describe digging at a valid trigger, observing a spawned mound and looting it.

Pinned `src/server/scripts/Kalimdor/zone_feralas.cpp` sources were read separately: TrinityCore3.3.5 `8fda442f6c30ca21a622638063ab8b28376f1b25` (blob026104c70e4e9c5d524217049a3d065b1982001f), and AzerothCore `8337a378ac325e62a6a91e00c6a5e944205e8536` (blob9b1e8d9b3cd33e35c54f65ce85b8ecb47a7f19d2). Both distinguish cobalt-mound11756 and junk19394 outcomes in trap19395. Invoking the shovel is not proof of collection success. Neither core revision is represented as the user's detected realm.

No new Gordunni recipe or guessed coordinates were shipped. An explicit sourced strategy still needs current quest/collection need, shovel possession, correct dig location/trigger, safe movement, original-client item invocation, spawned-object discrimination, loot and item/quest progress acknowledgement. The new collection guard can skip generated travel when enough cobalt is already present; it does not invent the missing dig/spawn/loot sequence.

## 6. Scope withdrawal and incomplete review

**d8b6476ac75f7422d52b26674f00c83433d848d9** already withdrew the unfinished auction experiment, restoring its two existing files to pre-experiment blobs and removing only its new transaction and feature-specific tests. Comparison with pre-auction ac03b01e showed only the scope/handoff documentation differs. This is not certification of legacy auction safety. Do not resume AuctionHouse or ProfessionBuddy.

The net comparison inventories29 non-test production/tool paths. This continuation read the relevant ProfileBuilder, UseItemOn, GossipEvent, DataLoader, PullIsolationCoordinator and Ret bodies, relevant quest/profile-helper/provider/settings/equip sections, verified both new red/green archive pairs, and rechecked historical cursor counts. **It did not individually accept every intervening commit or finish the full cross-owner review.**

Remaining priorities: non-collection individual-objective completion and direct legacy profiles; displaced-item/cursor/popup lifetimes; dense-pull actor/routine/context changes and actual traversed-route safety rather than just path existence/straight-line envelopes; strategy target/location authority; final gossip quest/NPC/menu revalidation; remaining prerequisite, PallyPower, reward, plugin refresh, merchant/gather, native and aquatic reviews. DataLoader already rejects duplicate quest/objective owners across kinds. Kind parsing lacks the Enum.IsDefined check used by its other enum parser; reproduce the parser behavior before changing it.

Retain all earlier source/core provenance, permitted addon/terrain hints, stronger/exclusive buffs, gear/loadout/caps, escort/event recipes, underwater recovery, GatherBuddy/rest/remount, native UI/slot/LOS/ABI, independent-review and supervised original-client/server acceptance requirements. Green offline suites do not satisfy those gates. This batch is not approved wholesale and does not establish a model-ranking benchmark.

## 7. Publishing mistake and exact recovery

While preparing this checkpoint, the assistant sent an erroneous `update_file` call with an empty branch/content/message/SHA. GitHub's default-branch behavior created one empty `docs/audit/2026-09-19/W77_CHECKPOINT.md` on master in **69296e337b9b40682e4d425e7e96156db3c9fcb2**. This was unintended and contrary to the intended branch-only workflow.

The assistant immediately deleted only that empty file using its exact empty-blob SHA and an explicit master branch, producing **b2324913e2499ba30b239dd67224ca2c655c05cc**. Native comparison with original master **f462a9bb4eb18acac9069f495177df35672286d5** has **zero changed files**. Both commits' tree SHA is exactly **552eeab1233c7c282897dca0ed9f4334c5e8ed43**. No production/test/workflow content changed, no force push occurred, and PR51 was not merged. The two documentation add/revert commits remain in master history; do not hide them or claim its SHA stayed unchanged.

Current master is **b2324913e2499ba30b239dd67224ca2c655c05cc**, with the original approved tree. Prepared unreferenced documentation commit857722c7 was superseded before branch publication because its current-master statement predates this incident; do not resume from it.

Future writes require a nonempty explicit approved branch, expected source/blob or parent identity, meaningful message and reviewed nonempty content unless a deliberate deletion is authorized. Discover the exact action schema rather than invoking another write action as a placeholder. These are recorded operating requirements, not a claim that an automated enforcement mechanism was implemented.
