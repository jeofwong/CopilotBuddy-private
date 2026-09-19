# W63 — recovered prerequisite and vendor retry continuation

18 September 2026. Repository `jeofwong/CopilotBuddy-private`, ID 1367174964. Draft PR51, branch `audit/next-55-equipment-observation-20260917`.

## 1. Reconcile the interrupted work before making changes

The live branch was **847e9c3aba9c44a1f92d5c810afccdf372465fee**, tree **77203dccc6667610f39a3dd7b010f53ffacba021**, sixteen commits beyond the W62 documentation head **ad4a1eb14377d1d727bbcc36eb10bdb2c41fdb45**. The old root prompt and PR description still described W62. Those descriptions were stale; the newer implementation and tests were already committed.

This continuation recovered and verified that work, inspected the actual scheduling, pickup, stepped/bulk sale and session callers, and completed the missing durable checkpoint. It did not recreate the sixteen commits, author another production repair, or launch a new C# run. Its new GitHub publication is documentation and an implementing-assistant review, not independent approval.

The recovered changes modify three existing production files, add one production gate and add seven test groups. The final execution passes **165 cases in those seven groups**, **99 aggregate groups**, **74 analyzer tests**, and all **17 integrated build/run entries**. Counts overlap with other suites and are not numbers of independent bugs.

Master remains the approved PR47 merge **f462a9bb4eb18acac9069f495177df35672286d5**, with the separate README edit preserved. PR51 is not merged or deployed. Preserve the c43c50d8 / 8382a7ec / 518baec5 backup refs and exclusions PR25/43/45; do not remerge historical PR44/47.

## 2. Quest prerequisites: what was already present and what was repaired

The ordinary positive-predecessor check already existed: a positive `PrevQuestID` and positive IDs in `PreviousQuestsIds` require the authoritative rewarded-history set. A quest merely ready to turn in is not substituted for a rewarded predecessor. The actual runtime scan reads accepted quests from its quest-log snapshot and obtains the separate historical set through `TryGetAuthoritativeCompletedQuests`; it does not populate that set by simply collecting ready quests.

The missing case was **negative `PrevQuestID`**. Under the inspected original-3.3.5 TrinityCore/AzerothCore contract, that means the parent must be active/accepted, not merely historically rewarded. Repair **520958f6e4ddb73ec07154804c86a8c597ac45bc** adds this admission check before scheduling a new pickup and rejects `int.MinValue` without negation overflow.

The original test-only revision **ba5308928743842e3e2ca45ab92a0765ce0e2cd0** reports **21/32**, eleven intended assertions and zero unexpected errors. The unchanged group is **32/32** in the final recovered run. Controls cover absent/wrong parents, ready-but-unturned parents, repeated accepted parents with old history, parent removal/restoration, additional rewarded prerequisites, unknown history, incomplete logs, unrelated pickup work and ready-parent turn-in priority. Unmet parent conditions do not generate a child data-failure record.

The actual pickup executor is a second boundary. `ForcedQuestPickUp.SelectAvailableQuest` checks the observed gossip/native offer lists, uses a uniquely resolved exact title where needed, and distinguishes a positively observed empty list from an unavailable observation. Missing offers feed the existing dialog/mismatch recovery rather than being treated as successful acceptance. These paths were source-reviewed here; the new 32 cases exercise the actual public scheduler with controlled snapshots, not a live NPC.

**Limits:** a complete prerequisite model is not established for every realm. The reviewed helper does not itself expand `ExclusiveGroup`; it checks a flat explicit prerequisite list. Whether an importer correctly expanded a particular grouped dependency must be verified against its source, rather than guessed. Negative/alternative groups, repeatable reset rules, class/reputation conditions, custom scripts and live offer changes require distinct evidence. Missing live offers must not automatically be diagnosed as a specific missing prerequisite.

Primary reference documents read for this review:
- https://trinitycore.info/database/335/world/quest_template_addon
- https://www.azerothcore.org/wiki/quest_template_addon

These support the shared signed-field distinction, not a blanket certificate for the installed dataset or a custom realm. Retain original WoW 3.3.5a build12340, TrinityCore3.3.5 primary and AzerothCore WotLK secondary.

## 3. Rejected items: bounded retry rather than a repeated sale loop

The recovered implementation adds `MerchantSaleAttemptGate` and connects the existing stepped and bulk quality sellers to it. A gate belongs to a MerchantFrame instance; this is **not a global shared transaction service across every seller instance or plugin**.

The recorded key binds the observed player GUID, merchant GUID, bag, slot, count and item link. An unchanged submitted stack is excluded for **120 seconds**, while other eligible stacks remain selectable. This is a retry deadline, not an instruction to stand at the merchant for two minutes. At most **256 keys** are retained; active exclusions are not evicted to make a failing item immediately eligible again. Generated exclusion-source size is also bounded.

Known zero-vendor-value items are skipped by both quality sellers. Unknown price, malformed metadata, missing context and locked observations retain their separate pending policies; they are not silently equated with zero value or successful sale. Existing protected-item, food/drink and client quest-item exclusions remain.

The initial stepped repair is **db617bcf2567d934d0fc52e9a53ac45b22c205a4**. A later real caller review found that the bulk API still bypassed the retry cache. Repair **7bc03d4528ed0357bd517b23482760e7670d4477** addresses that path too: both methods on the same instance share receipts, capacity and in-flight exclusion. Bulk processing validates the complete returned receipt batch before publication and preserves earlier submitted keys when a later item observation is pending, the window closes, or the Lua loop errors. The bulk loop rechecks player/merchant context before each request.

**A receipt means the local request was submitted, not that the server bought the item.** The logs now say submitted requests rather than claiming acknowledged sales. Items are not destroyed or permanently blacklisted. A changed slot/count/link/context is a distinct observation, and an expired deadline permits reconsideration.

The recovered clean stepped test revision **3a8e72b8d1d316ba5bb817289ab9f05618e6c1ae** reports **15/27 merchant cases** and **8/12 result cases**, with twelve and four intended assertions respectively, zero unexpected errors. The final run reports **27/27** and **12/12**. Bulk test revision **47de7d73bc5a8c7c9daa2ce01ab6e7da4c069e3b** reports **7/27**, twenty intended assertions, zero unexpected errors; final bulk cases are **27/27**. The four original/generated test payloads are unchanged from these clean failures to the final run.

## 4. Sale-session replacement and cleanup were also repaired

Three subsequent slices prevent an old or aborted sale operation from clearing newer work:

| Repair | Contract | Current cases |
|---|---|---:|
| 301d6003238c0bf7ae9ccd269aa4fb46749a80f2 | Stepped result and diagnostic callbacks recheck the captured session identity before counts or cleanup | 31/31 |
| 6a012632f6c2237030eb66316d8a8760a373ac90 | Public bulk wrapper preserves a session replaced/reset by the merchant callback | 16/16 |
| 847e9c3aba9c44a1f92d5c810afccdf372465fee | Aborted non-null entry/admission stays pending rather than telling its caller the service finished | 20/20 |

The last group uses the actual host entry/admission and XML-parsed no-sale profiles; it does not issue native merchant requests. Earlier extracted continuation groups substitute the external merchant/log/admission boundaries. Their exact scope is retained in each test and log.

The separate historical red counts for these three slices are recorded by their commit messages (14/31, 10/16 and 7/20). This recovery verifies their final passing executions but does **not** claim that those three individual red archives were re-downloaded. The initial raw-string normalizer failures and the read-only Profile-property fixture error were corrected before the respective behavioral reds; they are not gameplay failure evidence.

## 5. Source-reviewed boundaries still open

The narrow retry fix is useful but must not be inflated into a complete merchant transaction guarantee:

- A slot/link/count token is not a stable physical item GUID. Moved/replaced-but-identical inventory, alternate seller instances, late or malformed Lua results and native acknowledgement need stronger transaction evidence.
- The stepped generated Lua does not yet mirror every final merchant/player recheck present in the bulk loop, and neither path proves atomic bag-slot/UI ownership. This is a reviewed contract gap, not a new live loss reproduction.
- The public bulk API remains void; its legacy caller cannot distinguish every pending result from a finished pass. The stepped continuation also lacks a separately verified full-visit deadline for persistent unknown/locked observations. Whole-visit re-entry with full bags is not covered merely by a per-stack cooldown.
- Grouped prerequisite expansion and complete automatic item/gossip/event/escort strategies remain separate from ordinary predecessor checks and legacy helper tests.

Next tests should exercise those actual callers and observations before further production changes. Do not replace missing acknowledgement with an English error-string guess, clear all retry protection after one good sale, delete refused items, globally blacklist an item entry, or hold combat/services indefinitely.

## 6. Exact recovered evidence

Final integrated run **35332335480**, artifact **10541791831**, SHA256 **1f8f7b1b0cccdbf7ec7a41f90eb7242542d94e5b323aa27e4e0dd5f1443e6515**: all17 entries pass,96 Wholesome plus3 QuestLog aggregate groups pass,74 analyzer tests pass. Its source identity names code847e9c3a and tree77203dcc. All **155 internal-manifest entries** and complete ZIP CRC/SHA256 were checked.

Final host run **35332335473**, artifact **10541402192**, SHA256 **62e2176b79fabfc29b2729e00a5cad73156689cadb3a73f8cae09effb1c35bef**: compile exit0, **3304 warnings,0 errors**, tests_run=false,game_attached=false. There is no host internal manifest.

The package retains six original archives: W62 integrated baseline, three clean primary-red integrated archives, the final integrated archive and final host archive. Every retained archive is integrity-checked. The final source/configuration index has **1788 entries**. Compared with W62, only three old inputs change and eight are added (one gate plus seven tests). The final generated bundle has **111 members**:102 old members remain byte-identical, while the aggregate driver and manifest change to discover the added tests. Those two metadata changes must not be represented as altered behavioral oracles.

The original and normalized forms of all four primary-red test groups retain their recorded identities through final847. These are narrow archives and source-index comparisons, not a claim that1788 full source files were separately downloaded. No new Windows run was launched in this recovery. The thirteen retained archive-helper unit tests and the portable verification entrypoint pass; those utility checks are not extra bot cases.

## 7. Broader requirements are not silently closed

Retain W62 TurnIn conflict24, W59 merchant-context20/combat-gap26, quest progress33, item selection44/dispatch72, known-master flight12, Greater/Sanctuary87, aura70/Lowbie10, Protection53, engagement38 and earlier equipment/buff/LOS/legacyEscort controls. The uploaded AddOns.zip and W61 inventory are already available; do not ask for them again or infer runtime settings from bundled defaults.

PallyPower assignment bridging, permitted Carbonite hint import with map/floor/terrain validation, full special-quest strategies and authoritative credit, complete GatherBuddy/rest/remount behavior, all-class stronger/exclusive buffs, cap/loadout equipment/rewards and safe underwater recovery remain open. Independent review and supervised original-client/server acceptance remain open. The audit is **not exhaustively complete**, despite this recovered checkpoint being green.
