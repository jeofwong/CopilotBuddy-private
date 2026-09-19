# Quest data provenance and execution — original 3.3.5a

Reviewed 18 September 2026 at `ee31cde9985f233fb7c085160a50c05c54650ea0`.
Primary target: TrinityCore **3.3.5 branch**, original WoW **3.3.5a build 12340**. Secondary target: AzerothCore WotLK. Read with `WOTLK_335A_RESEARCH_POLICY.md` and `TRINITYCORE_335_COMPATIBILITY.md`.

## What the current code actually reads

| Layer | Actual source and purpose | What it does not establish |
|---|---|---|
| Wholesome planning dataset | `DataLoader` locates a local `quest_data/quest_data.json`, reads its bytes, deserializes `QuestDatabase`, validates/publishes dependencies and fingerprints the exact snapshot. | It does not connect to the realm's SQL database, download its scripts or identify the source core. A content fingerprint is not a provenance certificate. |
| Current player observations | `QuestLog` and `QuestLogSnapshot` read the original client's accepted quest slots, metadata and progress observations. Inventory and world-object observations supply current items and recipients. | A local plan does not prove current acceptance, item possession, an active target or successful completion. Missing observations are not empty state. |
| Client Lua and completion history | The original UI uses `GetQuestLogTitle`, `GetQuestLogQuestText`, `GetNumQuestLeaderBoards`, `GetQuestLogLeaderBoard` and related functions. The host uses `QueryQuestsCompleted()`, waits for `QUEST_QUERY_COMPLETE`, then reads the returned history through memory or a Lua fallback. | These are exposed client observations and defined client/server requests, not a remote SQL interface or a download of server AI/event scripts. Completed history is not a complete quest walkthrough. |
| Server-side knowledge | Versioned core DB/source, vetted original-version profiles and explicitly curated quest strategies can supply facts absent from the client. | The name “TrinityCore” or “AzerothCore” alone does not identify a realm's database revision, custom conditions or script changes. |

The current data model has ordinary objective kinds (`KillMob`, `CollectItem`, `CollectFromGameObject`, `TurnInOnly`) and fields such as StartItem and SpecialFlags. It has no complete ordered special-item/event recipe or consumed core-revision provenance manifest. This review does not establish where the user's installed JSON was originally exported from. Do not silently label it TrinityCore-derived.

This is not simply “read everything from the WoW files.” Client data/cache and the server-supplied state are distinct from the locally shipped Wholesome planning JSON. Neither a client build number nor a cached quest description proves the server's complete implementation.

## What client Lua cannot reconstruct

TrinityCore's **335** quest-addon documentation and AzerothCore's corresponding documentation explicitly identify SpecialFlags as server-side data **not sent to the client**. Both document cast credit separately from actual kills. Therefore ordinary client observations cannot be assumed to reconstruct every server-only condition or its complete event sequence.

The original 3.3.5 UI source demonstrates useful read APIs for descriptions and objective completion. It does not expose an ordinary arbitrary-server-database query. A server-admin export or a deliberately installed server-side addon/protocol would be a separate integration with its own authorization and schema. No such integration is implemented or inferred here; do not embed server credentials in the bot or repository.

Local Lua execution remains local client execution. Some defined API calls request server state, but running a Lua string does not turn it into server-side Lua, SQL, C++ or SmartAI execution. In particular, do not claim an available server-core/revision detector without a documented, tested protocol.

## Required architecture for source selection (not yet an implemented importer)

Retain the current behavior-tree executor. Add provenance and strategy selection at the data boundary, rather than scattering core-name checks through movement and casting.

1. **Explicit source identity.** Bind a dataset/strategy pack to client build, source core and branch, core commit/database revision, exporter/schema version, content hashes and declared realm overrides. Let the operator choose or supply a verified pack. Unknown provenance remains unknown; it must not silently become AzerothCore or TrinityCore.
2. **Core-specific import, shared semantics.** Normalize each proven source into named client-execution semantics. Preserve original values and provenance for audit. W57 already established that some internal TC/AC flag values differ, so copying numeric internal enums across cores is not a valid importer. The shared CAST0x20 rule is narrower than complete flag compatibility.
3. **Explicit strategy contracts.** A supported special quest needs quest/objective identity, item/interaction, recipient namespace and identity, required life/health/aura state, start/gossip/cursor protocol, ordered prerequisites, range/LOS conditions, safe movement and retry deadlines. A StartItem or required creature entry is only one input, not a full recipe.
4. **Fresh admission and acknowledgement.** Recheck the captured player, accepted quest, item and target after setup and immediately before dispatch. Afterward, wait for the appropriate quest progress or explicit event acknowledgement. An invocation, local repetition count, player arrival or NPC disappearance is not server credit.
5. **Conflict handling.** If live observations contradict the recipe, defer that work with a diagnostic and bounded recovery. Keep independent safe work available. Do not repeatedly use random items, kill a credit-only NPC, fabricate missing steps or rewrite imported evidence to conceal uncertainty.

An illustrative recipe might require a particular creature to be below a health threshold before a supplied item is used. The threshold, recipient and success condition must come from that quest's actual source/profile, not from this example. Another quest may require a corpse, transport, location, gossip transition or ground cursor instead; a universal “use item on nearest mob” rule is insufficient.

If the user operates the realm, a reviewed read-only export of the actual DB plus custom script/override provenance can improve the match. If the user is only a player, rely on explicitly sourced compatible recipes and current client observations; do not pretend the inaccessible server implementation is known. Even an accurate server-side export is knowledge input, not automatically executable client navigation and interaction code.

## Current progress and remaining limits

- Existing quest-aware CAST support prevents unsupported cast-credit work being treated as ordinary kills. It does not implement a missing recipe.
- Existing explicit `UseItemOn` selection, captured actor/item/recipient and quest-lifetime checks remain. W59 additionally prevents its generic container request when a merchant is visibly open.
- W59's combat repair keeps the shared ground-combat subtree active through a temporary target-selection gap, including living-pet-only combat, while retaining healing. It does not replace the target-selection algorithm or prove the full native HoJ/gather/mount chain.
- Automatic item/event/escort strategy discovery, GameObject/ground-cursor dispatch, complete native/UI freshness, server progress acknowledgement, nonblocking waits and live acceptance remain separate work.
- No automatic core detector, provenance importer, complete recipe engine or every-quest support was added in W59. This document establishes the required boundary and records the current implementation honestly.

## Source references

Current repository, pinned to `ee31cde9985f233fb7c085160a50c05c54650ea0`:
- `runtime-snapshot/Bots/WholesomeAutoQuest-master/DataLoader.cs` (blob `efad5c629cc7aed3265993ef65ae1c4a1089669c`).
- `runtime-snapshot/Bots/WholesomeAutoQuest-master/DataModels.cs` (blob `df977df23ea2b2e269f76ebde3771f564e7df8ad`).
- `Styx/Logic/Questing/QuestLog.cs` (blob `5450c7d5c27ddb2d595a508fdc28135f9d2be040`), especially `TryRefreshCompletedQuestCache` and completion-state distinctions.
- `runtime-snapshot/Quest Behaviors/UseItemOn.cs` and `Bots/Grind/LevelBot.cs`; see W59 evidence for the actual execution scope.

External primary materials reviewed on 18 September 2026:
- TrinityCore **335** quest addon documentation: https://trinitycore.info/database/335/world/quest_template_addon
- AzerothCore quest addon documentation: https://www.azerothcore.org/wiki/quest_template_addon
- Original 3.3.5 UI archive: https://github.com/wowgaming/3.3.5-interface-files/blob/d0339b17b0221db76e6acd2dc2915d224a5b62ca/QuestInfo.lua (`QuestInfo_ShowDescriptionText`, `QuestInfo_ShowObjectives`).
- W57's pinned server comparison remains TC335 `8fda442f6c30ca21a622638063ab8b28376f1b25` and AC `8337a378ac325e62a6a91e00c6a5e944205e8536`; those revisions are not represented as this user's exact realm build.

Modern TrinityCore master, Retail and Classic3.4.x are not substitutes for this target. A controlled Windows test is not live certification on either server.
