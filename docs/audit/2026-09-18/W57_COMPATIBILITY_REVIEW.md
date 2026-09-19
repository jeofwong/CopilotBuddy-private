# W57 — TrinityCore compatibility review and remaining execution contracts

18 September 2026. Implementation reviewed at44ff289ae1afd8fcef625948b10b5af8819deb26. This is source review plus controlled Windows execution, not testing against a running server.

## Pinned primary sources

- **TrinityCore 3.3.5:** `8fda442f6c30ca21a622638063ab8b28376f1b25`.
- **AzerothCore WotLK:** `8337a378ac325e62a6a91e00c6a5e944205e8536`.
- TC QuestDef.h: https://github.com/TrinityCore/TrinityCore/blob/8fda442f6c30ca21a622638063ab8b28376f1b25/src/server/game/Quests/QuestDef.h
- AC QuestDef.h: https://github.com/azerothcore/azerothcore-wotlk/blob/8337a378ac325e62a6a91e00c6a5e944205e8536/src/server/game/Quests/QuestDef.h
- TC Unit.cpp: https://github.com/TrinityCore/TrinityCore/blob/8fda442f6c30ca21a622638063ab8b28376f1b25/src/server/game/Entities/Unit/Unit.cpp
- TC spell_paladin.cpp: https://github.com/TrinityCore/TrinityCore/blob/8fda442f6c30ca21a622638063ab8b28376f1b25/src/server/scripts/Spells/spell_paladin.cpp

Native GitHub reads were used for the exact source. The TC documentation page did not expose parsed text on the final web read; that empty rendering is not evidence. The pinned header supplies the needed claim directly. AC documentation separately confirms the cast-credit flag: https://www.azerothcore.org/wiki/quest_template_addon . Avoid using any evolving documentation as proof of this realm's exact database.

## 1. Actual shared contract and actual difference

Both QuestDef.h files define CAST as0x20 in server SpecialFlags. Client Flags is a separate enum. The new scheduler predicate uses only the shared bit.

The internal flags are **not interchangeable**: TC DELIVER/SPEAKTO/KILL/TIMED are0x80/0x100/0x200/0x400; AC uses0x200/0x400/0x800/0x1000. AC reserves additional lower bits for other server policies. A generic import of every internal flag value from one core would therefore be wrong. Keep core/revision provenance if exporting those values, or translate named semantics explicitly.

The current Wholesome model has StartItem, SpecialFlags and ordinary objective rows but no complete ordered item/event strategy. A cast-credit flag can reject a false kill interpretation, not reconstruct an item ID, target namespace, live state, gossip choice, cursor action, ordered prerequisites, repeat policy or completion signal. Server scripts, vetted original-version profiles or explicit curated mappings are still needed for those facts.

## 2. HoJ, target clearing and combat ownership

TC Unit::SetStunned applies SetTarget(ObjectGuid::Empty), stun/root flags, StopMoving and CastStop; removing the stun restores the victim target where appropriate. This confirms the server-side displayed-target transition described by the quoted developer. It does not show that threat is erased, that our own player's combat ends, or that this fork takes the same downstream path as that reporter.

Current GroupCombatSafety.IsEngagedWithGroup requires valid living actors and enemy combat plus an observed group link. Positive threat can provide the link when the displayed target is empty. The 38-case suite covers this observation pattern, not a native HoJ or a running TrinityCore/AzerothCore server. The new observer-admission repair was recovered at1e67d956; the positive-threat controls already passed before it.

Important scope: GroupCombatSafety.IsRestricted is specific to Combat Bot in dungeons. It is not a blanket GatherBuddy control. Actual GatherbuddyBot.CreateRootBehavior delegates to LevelBot.CreateCombatBehavior behind _combatSuppressed. LevelBot's active combat branch also requires Targeting.FirstUnit. Root fall-through if target selection temporarily returns nothing remains a separate source-level concern. Do not label the full reported flee/switch symptom solved merely because group safety passes.

Next actual-owner cases: stun clears enemy displayed target while combat/threat remain; one pulse with no selected FirstUnit; existing target versus unrelated nearby mob; pet-only combat; evade/death/despawn; target change during cleanup; mounted travel suppression versus grounded node defense; safe return to gathering and subsequent mount once recovery is genuinely finished. No new failure reproduction for this complete chain was executed in W57.

## 3. Sanctuary, Greater buffs and complementary auras

TC spell_paladin.cpp explicitly identifies20911 and25899 as normal/Greater Sanctuary and routes both through its Sanctuary aura script. The script removes its auxiliary buff with caster identity. This supports original-version spell presence and the importance of per-caster semantics. It does not establish this character's learned talents or every stacking magnitude.

Our PaladinBlessings enum now appends Sanctuary after the existing values, and the common dispatcher lists all four normal and Greater blessings. UseGreaterBlessings defaultsfalse; enabling it requires learned spell metadata, nonzero identities, OOC state, known reagents and compatible visible same-class group coverage. Unknown or conflicting group evidence falls back to the normal action instead of spending reagents on an unproven mass replacement. Greater87 and support44 cases pass in the final run.

The recovered aura owner spans precombat/combat and Lowbie paths. It preserves useful owned coverage and uses stable caster GUID ordering for duplicate known providers, reducing coordinated flip-flopping. Aura70 and Lowbie10 cases pass. Ret's separate forced Retribution Aura call is removed. This is contribution coordination, not a complete rank/talent/encounter optimizer. Explicit Fire/Frost resistance choices are not present in the reviewed enum; the legacy Resistance option maps to Shadow. Do not silently advertise complete resistance coverage.

Battle Shout suppression of Might remains conservative: active equivalent coverage prevents repeated attempts even in manual mode, but effective magnitude is not compared. The policy is not a proof that every Shout is stronger than every Might. Earlier all-class shared-buff revalidation controls are retained; all possible family contradictions are not thereby exhausted.

## 4. Protection recovery and remounting

Current Protection registers an OOC Heal behavior and calls it before default rest. It attempts Holy Light/Flash of Light under configured thresholds, excludes active combat and unsafe travel/casting states, and reserves OOC Plea for healthy low-mana recovery. Unconditional rotation HoJ is removed; the shared tactical interrupt helper is retained. Protection53 cases pass using the complete linked routine with controlled dispatch/default rest.

This addresses a missing recovery path, not every cause of delayed remounting. Do not call flight while combat, gathering, needed healing or an owned rest sequence legitimately remains active. Actual Heal/default-rest/mount ownership, no-food/no-mana failure handling and finite progress deadlines still require combined tests. No source change bypassing recovery was made in W57.

## 5. Earlier changes: compatibility review is not blanket recertification

| Previous area | Current evidence boundary |
|---|---|
| Client spell metadata, native item/UI layouts and offsets | Original-client contracts and earlier controlled tests remain; a matching server brand cannot prove native addresses, cache freshness or ABI acceptance. |
| Shared buff coverage, effect strength and exclusivity | Coverage/caster/setup controls pass; rank/talent coefficients and every single-active family still need original-version evidence and decision tests. |
| LOS and movement | Existing native traces and managed admission remain; spell exceptions, masks, real obstacles and server rejection must be compared explicitly. Do not globally make every cast require the same mask. |
| Loot/equipping | Saved weights and permission/unknown-slot safeguards remain; neither core pin proves universal Strength/Agility weights or cap-aware optimization. |
| Quest data, items and events | New shared cast-bit guard is cross-correlated; core-specific internal flags and custom scripts remain distinct. Explicit UseItemOn72/selection44 is not automatic recipe discovery. |
| Gathering and underwater recovery | OOC recovery and wet-consumable guards remain; they do not prove safe air/shoreline paths or no premature flight after every stun. |

Earlier AC-derived claims must retain their exact evidence and be rechecked where server-sensitive. There is no evidence here for a blanket statement that every previous commit has been live-tested on both cores. Prefer a shared observation/strategy contract rather than a realm-name or server-name switch.

## 6. Next implementation sequence

Keep the current behavior-tree executor. Introduce an explicit, versioned quest-strategy contract only after locating source-backed recipes. Match quest/objective/item and target namespace, verify start/state/LOS/range/inventory conditions, dispatch using reviewed primitives, and wait for authoritative progress with bounded retries. Test missing or contradictory recipe evidence, counters that change independently, consumed items, actor replacement and interrupted event lifetimes. Preserve independent safe work without fabricating completion.

For existing UseItemOn, finish NPC-versus-GameObject targeting/cursor ownership, LOS, blocking waits and final success acknowledgement before allowing generic automatic dispatch. Then test the actual GatherBuddy/LevelBot/Targeting chain above and strengthen buff rank/singleton coordination without disabling legitimate transitions. Keep pending source findings separate from reproduced bugs, and obtain independent review plus supervised original-client/server acceptance before broad release claims.
