# Addon evidence for Wholesome — original WoW 3.3.5a

Reviewed 18 September 2026. Target original client 3.3.5a build 12340; TrinityCore 3.3.5 primary, AzerothCore WotLK secondary. Read with `QUEST_DATA_PROVENANCE_335.md` and both original-client/core compatibility policies.

## Exact installed source is still required

The requested folder is `D:\World of Warcraft 3.3.5a\Interface\AddOns`. This session has not read that Windows filesystem. GitHub access is not access to the user's local drive. The public references below are comparison sources, not proof of what is installed, enabled, or supplying the current minimap overlay. A directory named Carbonite and a TOC version declaration are useful identity clues, not complete runtime verification.

The new tool is an offline inventory and quarantine boundary, not an addon database decoder or runtime importer. No third-party Lua/database payload, account SavedVariables, credentials, installed game files, or addon settings were copied into this public repository.

## Source review and adoption decisions

| Reference | Verified evidence | Decision and limits |
|---|---|---|
| Carbonite legacy 3.34 reference | At commit `7607006fb7c4d20a4075220681a77a3f6159a736`, `Carbonite/Carbonite.toc` declares Interface 30300, version 3.34, and NxData/NxCombatOpts/NxMapOpts plus per-character NxCData. The project's user guide describes quest objective/giver displays, quest areas and a searchable database. | Plausible match for the user's description, not verified installation. Its bundled legacy licence contains restrictions on copying/modification/distribution absent authorization. No raw database extraction or redistribution is approved merely because a later Carbonite project uses a different licence. Exact installed licence and applicable permission need review. |
| Questie-335 reference | `Krigsgaldrnet/Questie-3.3.5` branch 335 at `61b30f09c1b6df36ed839bdd2fdae63105bad21c` targets client 12340 in its README. It contains Classic, TBC and Wotlk database directories, corrections and compatibility shims. Reviewed QuestieDB/Constants also contain later-expansion names and corrections. | Useful candidate for quest/entity/location relationships after selecting and validating the actual WotLK data plus corrections. A backport's target label does not make every bundled record original-WotLK evidence. Compatibility aliases are not stock original-client APIs. No licence or installed-copy permission was certified here. |
| GatherMate with 3.3.5a database | `stevemcqueenz/gathermate-and-database-3.3.5a` describes mining/herb/treasure data from an AzerothCore world DB, and separately identifies upstream fishing/gas data. Its generator describes WorldMapArea.dbc-based coordinate conversion. | Useful gathering-location candidate, not a full quest instruction source. The project's advertised server-match claim is not proof of this user's realm data. Preserve separate source origins and core/database revisions. No claim that all nodes are active or reachable. |
| Ascension-specific addons | `Xurkon/GatherMate2` explicitly describes Project Ascension custom gathering data. Its Carbonite variant also targets Ascension. | Do not silently mix these custom-realm datasets into stock TrinityCore/AzerothCore WotLK knowledge. Same 3.3.5a client does not establish same world data. |

These are first-party project descriptions and inspected versioned files. The review did not execute addon Lua, decode the legacy Carbonite runtime, verify every record in any data pack, or install a replacement addon. The scanner also flags QuestHelper/pfQuest, TomTom and Atlas names as review candidates; those name-based flags are not endorsements, proof of their installed contents, or proof of compatible data formats.

## What addon data can contribute

Treat an addon-derived objective area as a place to search, not permission to kill everything in the area. Entity spawn hints, possible item sources, giver/ender candidates and known gather locations may supplement missing or sparse planning knowledge after their identities and provenance are resolved. Preserve the distinction between a quest objective and an item's possible drop source. A creature entry used only for scripted credit must not be converted into an ordinary kill objective.

Map geometry has its own contract. Preserve the source map namespace, map/floor, unit scale and shape. Zone percentages are not client world X/Y. A rectangle is not one known spawn at its centre. Two records with the same numeric creature and GameObject ID are not the same entity. A 2D pin does not establish elevation, safe ground, absence of an obstacle, a navigable entrance, or the correct floor. Do not invent Z=0 or mark points reachable because an addon draws them.

A later runtime adapter needs an independently verified original-client map transformation, candidate elevation/navigation checks and current target observations. Hotspot selection should then use reachability, risk and observed availability alongside distance, with bounded no-progress recovery. Addon data cannot override current quest acceptance/progress, core-specific scripts, action eligibility or stronger direct observations. Correlated datasets copied from the same ancestor must not be counted as independent corroboration.

## Implemented offline tools

`Tools/EvidenceAudit/addon_evidence.py` has two explicit commands and uses Python 3.10+ standard library only.

### Read-only inventory

From a directory outside AddOns, with the standalone tool downloaded or this repository checked out:

```powershell
py -3 .\addon_evidence.py scan --addons "D:\World of Warcraft 3.3.5a\Interface\AddOns" --out ".\addon-inventory.json"
```

From the repository root, use `py -3 .\Tools\EvidenceAudit\addon_evidence.py ...` with the same remaining arguments. No elevated permissions or execution-policy change is needed by this Python tool. It does not install Python or addons.

The report includes addon-folder names, bounded TOC metadata, relative text-file paths, sizes and SHA256 hashes. TOC file references are not followed as commands or paths. Names of SavedVariables declarations may appear, but their account files are not opened. It does not inspect the sibling WTF tree, follow symlinks/junctions, upload anything, execute Lua, or determine whether an addon is enabled in the running client. Binary art is intentionally excluded. Name-based candidate categories only aid triage.

Budgets are 50,000 entries, 256 MiB total bytes, 32 MiB per text file and depth 16. Oversized/unreadable/changed files, excluded links and exhausted budgets report a partial result rather than a false complete inventory. Inputs are read-only. The output must be a new file outside the input tree; an existing output is not overwritten. Exit 0 means a complete scoped inventory, 1 means a saved partial inventory, 2 means an error. Review the JSON before sharing it; unusual addon authors can put arbitrary text in TOC metadata. Run on a quiescent directory: this is not an adversarial-filesystem security sandbox or proof against every concurrent directory substitution.

The scanner has not been run on the user's drive. Synthetic temporary-directory tests and CI are labelled separately from installed-source verification. An explicitly connected filesystem session or the returned inventory is the next source-acquisition step; do not request the entire WTF/account directory.

### Quarantined neutral hints

```powershell
py -3 .\addon_evidence.py stage --input ".\reviewed-hints.json" --out ".\quarantined-hints.json"
```

This command accepts our new neutral JSON format only. It does not accept raw Carbonite/Questie Lua, scrape a minimap, infer the source's data layout, or extract hidden server scripts. A separately reviewed, lawful source adapter must supply this envelope. The source hash and core are declarations, not automatically verified evidence.

Required schema is `addon-hints-335-v1` with exact client build 12340. The source contains provider, version, SHA256, core, revision and licence strings. Supported core declarations are unknown, trinitycore-3.3.5, and azerothcore-wotlk. A hint contains quest_id, nullable zero-based objective_index in this neutral schema, entity_type/entity_id, explicit map namespace/map_id, nullable floor, and geometry. Allowed map namespaces are world-map-area-335, carbonite-zone and questie-area. The names preserve source namespaces; they do not convert between them. Known entity namespaces are creature, gameobject and item; unknown has a null ID. A named item source is still not proof of an action.

Geometry is a point or ordered box with explicit zone-percent or zone-fraction units. Unknown fields, extra action/Lua fields, duplicate JSON keys, nonfinite/boolean/out-of-range coordinates, contradictory IDs, wrong builds and oversized inputs are rejected. Polygons and other unreviewed forms are not flattened into points. Exact duplicate records are deduplicated; distinct entity namespaces remain separate. No input object is modified.

Every accepted result is still `quarantined`, with `runtime_enabled=false`, `source_verified=false`, `licence_verified=false`, `authority=search-hint-only` and `world_xyz=null`. There is no option to override those flags. A producer's licence string does not grant redistribution rights; a core string does not identify the user's actual realm. The current Wholesome scheduler does not consume this output.

## Next integration gates

1. Identify the exact installed addon, version, selected dataset/corrections and licence. Keep private observed data outside the public repository unless the user explicitly approves a reviewed export.
2. Implement a format-specific read-only parser/exporter only against real, permission-reviewed fixtures. Do not execute untrusted Lua; do not assume later-version schemas. Keep original evidence and transformations traceable.
3. Correlate quest and entity IDs against the current planning model and live objective observations. Keep uncertain objective mappings as unknown. Do not convert location coverage into special-action support.
4. Validate original-client map/floor transformations and terrain-aware candidate selection before any runtime use. Preserve existing ownership, cancellation, unreachable-route and combat/rest guards. Keep imported hints optional and independently disableable.
5. Implement special-item/gossip/event/escort strategies separately, with ordered prerequisites and authoritative progress acknowledgement. Location hints do not supply missing action sequences. Continue the remaining W59 audit requirements and live-server acceptance.

## Sources

- Legacy Carbonite reference: https://github.com/DevScarabyte/Carbonite-3.3.5a-Remastered/tree/7607006fb7c4d20a4075220681a77a3f6159a736
- Inspected TOC blob: `33567e8b3de12b3a09712dfb6d9201060197850b`; legacy licence blob: `f48cfb84a2cd3802d8ded5aaa26cc7d64470fe7f`.
- Carbonite user guide, live reference read18Sep2026: https://github.com/DevScarabyte/Carbonite-3.3.5a-Remastered/blob/master/CarboniteReadMe.txt (not proof that the user's copy is identical).
- Questie-335: https://github.com/Krigsgaldrnet/Questie-3.3.5/tree/61b30f09c1b6df36ed839bdd2fdae63105bad21c
- Inspected QuestieDB blob: `5784cb65160175e1dce30fe20e8baada0af7e480`; Constants blob: `31433fae38d11569d31a15c810ade34db1643bf8`.
- GatherMate project/source description read18Sep2026: https://github.com/stevemcqueenz/gathermate-and-database-3.3.5a
- Custom-realm comparison read18Sep2026: https://github.com/Xurkon/GatherMate2 and https://github.com/Xurkon/Carbonite

No licence conclusion for an unseen installed version, server-revision detection, raw addon importer, active hotspot improvement, new gameplay repair, or all-quest completion claim is made by this document.
