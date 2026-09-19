# Resume at W67 — verified provenance/dependency/addon/reward frontier

Repository `jeofwong/CopilotBuddy-private`, draft PR51, branch `audit/next-55-equipment-observation-20260917`. Reconcile live head/master before writing.

Latest verified code **0c918908d0b6372c1b8b20359982e037c87a635b**, tree **daf4476e9836c8aaa4d3199c12d6fb0de760fef2**. Read `docs/audit/2026-09-19/W67_CHECKPOINT.md` and `W67_EVIDENCE.json`, then WOTLK_335A_RESEARCH_POLICY, TRINITYCORE_335_COMPATIBILITY and QUEST_DATA_PROVENANCE_335. W63–W66 requirements/evidence remain retained.

New verified slices since W66: bounded merchant pending observations; optional strict dataset provenance while the current dataset remains Unknown; legacy fingerprint identity preservation; positive and negative ExclusiveGroup handling without rewriting signed negative PrevQuestID; opt-in read-only/version-pinned PallyPower v3.2.21 Wrath assignments; quarantined WorldMapArea candidate conversion with no Z/terrain/runtime authority; strict source-bound special-strategy pack validation; and fail-closed live quest reward identity.

Final integrated35412850647/art10575210442 SHA96e49a57538d6abada13c62ea8d3aedbd65d03654bdf0c95f4e8ba8b39b3684b passes on exact code head. Host35412850625/art10575030460 SHAafbed4763aa50e841bc6efbfbb69758763d0ff886942489b58c57cf598a0b23a passes compile-only.

NEXT execution slice: source-bound special recipes are validated but unused. Start with UseItemOn only. Do not let local invocation count establish success. A generated strategy must preserve quest/objective/item/target/state/range/LOS/max-attempt/source identity and terminate only from authoritative ObjectiveProgress/QuestComplete or bounded deferral. Gossip and Escort remain separate later slices.

Do not silently reinterpret PreviousQuestsIds as TC OR semantics until exporter/source provenance is established. Current JSON provenance is unknown. PallyPower is Paladin assignment integration, not all-class strength optimization. Carbonite is search hints only. Reward identity safety is not cap/loadout optimization. Aquatic observation/rest tests are not underwater escape. Full GatherBuddy/rest/remount, native UI/slot/cursor/LOS, independent review and original client/server acceptance remain open.

Masterf462a9bb unchanged. Preserve backups/exclusions; no PR51 merge, force push, deployment or installed addon/binary/mesh modification.
