# TrinityCore 3.3.5 compatibility requirement

Owner requirement reaffirmed 18 September 2026.

## Supported target versus verified evidence

The primary compatibility target is **TrinityCore's 3.3.5 branch with original WoW 3.3.5a build 12340**. AzerothCore WotLK is a secondary compatibility target. This is a mandatory design and review requirement, not a declaration that every server, custom realm, database revision or gameplay path has passed acceptance testing.

Read this document with `docs/audit/WOTLK_335A_RESEARCH_POLICY.md` and the newest checkpoint. Do not use TrinityCore's modern master branch as evidence for original 3.3.5 mechanics. Do not substitute Wrath Classic 3.4.x guides or import their later balance changes without original-version corroboration.

## Review rules

For a server-sensitive change, record the exact TrinityCore 3.3.5 revision and relevant source/data path, then compare the corresponding AzerothCore implementation where support is claimed. Classify the outcome as source-correlated, tested against controlled client observations, tested against an actual server, or unresolved. A passing offline C# test is not a server compatibility certificate.

Keep client contracts separate from server logic. The original client ABI, spellbook, inventory APIs, aura observations and navigation interfaces are client-facing. Quest scripts, credit conditions, database schemas, spell stacking, threat and exceptional spell rules may differ by core revision or realm customization. Code must act on current valid client observations and an explicit supported strategy, not infer a core from a realm name or assume every database exports the same fields.

Prefer shared behavioral contracts over core-name switches. A stunned enemy's displayed target becoming empty is not sufficient evidence that combat or threat has ended. Likewise, loss of data is not an empty inventory, completed quest, missing buff or permission to act. Preserve the existing ownership, bounded-retry and last-moment dispatch checks.

## Quest-item and event safety

A supplied item ID, required NPC entry, objective text, or server cast-credit flag alone is not a complete recipe for using an item. A supported strategy must identify the quest/objective, item or interaction, recipient type/identity, required state, range/line of sight, start conditions, retry limits and success evidence. Reuse reviewed profile/API primitives without assuming that their presence makes Wholesome automatically support every special quest.

TrinityCore 3.3.5 `QuestDef.h` at `8fda442f6c30ca21a622638063ab8b28376f1b25` defines `QUEST_SPECIAL_FLAGS_CAST = 0x020` as cast credit rather than an actual kill. The TrinityCore 3.3.5 and AzerothCore documentation agree on this bit. Client `Flags` and server `SpecialFlags` are different namespaces. Unknown or unsupported cast-credit work must not silently become a KillMob instruction. Existing completed-quest turn-in and separately valid collection work must remain usable.

Do not equate player arrival, item submission, NPC disappearance or a zero target GUID with server success. Observe the correct quest progress or explicit strategy acknowledgement. Do not change imported evidence to conceal a missing strategy.

## Buffs, auras and combat

Separate same-effect coverage, effective strength, single-active families, per-caster contributions and intentional transitions. Do not cancel another Paladin's aura, keep retrying a weaker conflicting blessing, or globally forbid legitimate aspect/stance/seal changes. Unknown strength must not be presented as a proven upgrade. Greater blessings must retain recipient/class-wide effect, reagent, known-spell, range and current coverage checks. A setting is not evidence that the character has learned a talent spell.

A tactical stun is not a damage spell, but disabling all stuns is not a general repair for a botbase losing combat ownership. Trace the routine, target selection, combat state, threat, gathering/rest and mounting owners separately. Retain defenses and heals appropriate to the actual specialization; do not fix delayed remounting by bypassing necessary recovery or issuing unsafe movement.

## Research references

- TrinityCore 3.3.5 source: https://github.com/TrinityCore/TrinityCore/blob/8fda442f6c30ca21a622638063ab8b28376f1b25/src/server/game/Quests/QuestDef.h
- TrinityCore 3.3.5 quest addon documentation: https://trinitycore.info/database/335/world/quest_template_addon
- TrinityCore 3.3.5 objective documentation: https://trinitycore.info/database/335/world/quest_template
- AzerothCore quest addon documentation: https://www.azerothcore.org/wiki/quest_template_addon

References reviewed on 18 September 2026. Earlier AzerothCore-derived claims require explicit TrinityCore cross-checking; they are not retroactively certified by this policy. Record unresolved differences and retain original-client/server acceptance as an open gate until executed.
