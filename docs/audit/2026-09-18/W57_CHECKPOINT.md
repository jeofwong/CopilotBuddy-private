# W57 — TrinityCore-aware quest safety and recovered Paladin fixes

18 September 2026. Repository `jeofwong/CopilotBuddy-private`, ID 1367174964. Continue draft PR51 on `audit/next-55-equipment-observation-20260917`.

## Current state and evidence

Latest verified code is **44ff289ae1afd8fcef625948b10b5af8819deb26**, tree **f25c23875730aa8c737da0c3011901713f2335d7**. This continuation recovered 16 commits beyond the old W56 documentation checkpoint, finished the pending cast-credit repair, and committed the TrinityCore compatibility requirement. Do not recreate the recovered quest-item, Paladin aura, Greater blessing, Protection recovery or engagement repairs.

Master remains the approved PR47 merge **f462a9bb4eb18acac9069f495177df35672286d5**. PR51 is not merged or deployed. Preserve the README and backups c43c50d8/8382a7ec/518baec5; exclude PR25/43/45 and do not remerge historical PR44. Re-read live refs before writes. Public integrated/host CI is authorized and operational; no workflow or permission change was made here.

Read `docs/audit/WOTLK_335A_RESEARCH_POLICY.md` and **`docs/audit/TRINITYCORE_335_COMPATIBILITY.md`** first. The primary target is TrinityCore's **3.3.5 branch / original client build 12340**; AzerothCore WotLK is secondary. This is a required compatibility target, not a blanket server-acceptance certificate. Modern TrinityCore master and Wrath Classic 3.4.x are not substitutes.

## New repair: cast credit is not a kill instruction

The pending test-only head **7854c3f577f389945fc393239f8b6f21b5c0f1c0** exercised the actual scheduler and profile writer. Its integrated run returned **10/20 passing, 10 intended assertion failures and zero unexpected errors**. All builds and the other 16 integrated entries passed.

Both pinned cores define server `SpecialFlags & 0x20` as cast credit, explicitly not an actual NPC kill. Wholesome was treating an imported KillMob row as executable ordinary killing without consulting that flag. Repair **44ff289a** adds a quest-aware support predicate at objective scheduling and pickup eligibility. The same 20 tests now pass. Only `QuestScheduler.cs` changes in production; the second changed path is the compatibility policy. Every test, normalized fixture and workflow input is unchanged.

Completed turn-ins, already-satisfied counters, valid collection work, independent quests and the distinction between client `Flags` and server `SpecialFlags` remain. The input row is not silently rewritten. Unsupported action work is reported through its existing objective owner before navigation.

**This prevents an incorrect action; it does not implement a missing item recipe.** StartItem, a required NPC entry or cast-credit bit alone cannot establish which item to use, on which live object, under what conditions, or what proves success. Unknown recipes must stay explicit. The current database model still has only KillMob/CollectItem/CollectFromGameObject/TurnInOnly objective kinds.

## Recovered capabilities re-executed in the new passing run

| Area | Current cases | What is established |
|---|---:|---|
| Explicit UseItemOn target selection | 44/44 | Validity, namespace/state/aura filtering, BelowHp versus corpse, collection distance and inventory observation controls |
| Explicit UseItemOn dispatch | 72/72 | Captured actor/item/recipient and revalidation across setup callbacks; no substitution of a different same-entry object |
| Paladin aura coordination | 70/70 | Shared aura owner, preserved complementary contributions and stable caster-order tie breaking |
| Lowbie aura coordination | 10/10 | Lowbie path uses the same coordination policy |
| Greater blessings | 87/87 | Sanctuary setting, normal/Greater options, known-spell/reagent/roster/coverage controls |
| Protection recovery | 53/53 | Registered OOC healing before default rest and guarded mana recovery |
| Core engagement observations | 38/38 | Valid living observers and retained positive threat after the displayed enemy target clears |

These are passing executions of previously committed work, not seven newly authored repairs in this continuation. Separate original red archives for each recovered feature were not downloaded here. The current run also retains the earlier shared-buff, blessing contribution, loot/equipment, LOS and legacy Escort cases.

Greater blessings default to **disabled** through `UseGreaterBlessings`; when enabled they are preferred only out of combat with known resources and compatible same-class coverage. Normal buffs remain the fallback. Sanctuary selection does not grant an unlearned talent spell. The aura policy does not calculate every talent/rank advantage or guarantee optimal encounter resistance assignments.

## Actual final Windows validation

| Execution | Run / artifact | Result |
|---|---|---|
| Integrated x86 | 35294707613 / 10527486517 | **17/17 build/run entries and all 88 aggregate groups pass** |
| Host compilation | 35294707643 / 10528400041 | **0 errors, 3296 warnings**, compilation only |

The original red artifact is run35292526779/artifact10527232042. All three complete ZIP digests and CRCs were verified. Each integrated archive has **144 verified inner-manifest entries** with full file coverage. Red and green have **1774 input hashes**, differing only at QuestScheduler.cs, **100 byte-identical normalized members**, and **87 unchanged other aggregate outcome inventories**. Both supplied scheduler source snapshots match their respective CI input hashes. The host archive has no internal manifest; none is claimed.

The existing portable evidence verifier and its 15 utility tests are retained and rerun in the handoff. These are artifact checks, not gameplay scenarios. No local C# execution or separate focused workflow is claimed; the four focused projects are included in the integrated run. `game_attached` is false. A successful host build is not native/server acceptance, an all-green security review, or a warning-free build.

## Boundaries still open

The quoted developer report is not this fork's attached live log. TrinityCore source confirms that SetStunned clears the displayed target; it does not alone prove the rest of the reported GatherBuddy failure chain. Our group-engagement predicate preserves positive self/group threat but is not the entire GatherBuddy root or target selector. Source review also found GatherBuddy delegates to LevelBot and the latter requires FirstUnit for its active combat branch. That combination needs end-to-end target-loss/stun/recovery tests before promising no premature patrol.

Protection now attempts learned self-healing before falling into missing-food recovery and no longer has unconditional rotation HoJ. This is not permission to ignore legitimate recovery or force immediate flight. Tactical shared interrupts remain. The actual post-combat gathering/rest/mount sequence still needs verification.

Explicit UseItemOn script safety is not automatic Wholesome item/event support. Its GameObject cursor/target protocol, LOS, blocking waits, success acknowledgement and lifecycle boundaries remain. Automatic escort/event recipes, stronger-rank/singleton buff policies for every class, cap/loadout-aware equipment, underwater escape, remaining native/merchant observations, independent review and original-client acceptance are not closed by this checkpoint.

See W57_COMPATIBILITY_REVIEW.md for the pinned evidence and concrete next cases. There is no pending unexecuted test-only addition at the verified code head. Exhaustive completion and measured maximum DPS are not established.
