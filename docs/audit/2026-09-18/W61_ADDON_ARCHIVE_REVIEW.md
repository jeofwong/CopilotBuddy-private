# W61 — actual uploaded AddOns archive review

18 September 2026. Repository `jeofwong/CopilotBuddy-private`, draft PR51, branch `audit/next-55-equipment-observation-20260917`.

## Scope and source identity

This review inspected the user's uploaded **AddOns.zip**, not a connection to `D:\World of Warcraft 3.3.5a\Interface\AddOns`. The archive is 110,590,947 bytes; SHA256 **75055256969fd6d8baa218113fe49e08773965b09efd4bd35530c106c861eb3c**. It contains 18,251 members, including 16,481 non-directory members; their total expanded size is 276,380,110 bytes. All ZIP CRCs passed. Member names were unique, paths were checked against traversal/absolute-path forms, and no symlink entries were found before selective reading. No addon Lua was executed.

Excluding Apple metadata sidecars leaves 11,651 file members, 138 top-level folders containing files and 376 TOCs. There are 111 matching `AddOns/<name>/<name>.toc` entrypoints and 265 nested TOCs. These counts are not counts of enabled addons; nested libraries and bundled duplicates are included. The archive has no WTF or SavedVariables directory component. TOC declarations/defaults do not reveal actual enabled state, current character configuration, imported gathering history, Pawn selections or PallyPower assignments. No drive, account folder or live client was inspected. No input, installed addon or game file was changed.

The local inventory contains hashes for 6,835 selectively read text/source members and complete TOC metadata/reference inventories. This is metadata and source review, not a security certification of every member or a claim that all scripts were understood. Text manifest-reference existence does not prove successful loading. The downloadable report excludes third-party Lua/database payloads, art, fonts and account information. Public repository publication is limited to this original analysis and resumption documentation.

W60's need for exact source acquisition is satisfied **for this uploaded snapshot**. Its earlier public Carbonite/Questie references must not substitute for the contents below. Runtime enablement and realm provenance remain unknown.

## Actual relevant addons

| Uploaded entrypoint | Declared version | What is present and useful | Adoption boundary |
|---|---|---|---|
| Carbonite/Carbonite.toc | 3.34; Interface30300 | Main script contains the packed `Nx.Que1` quest table. The bundled guide documents objective areas, quest database and map targeting. | Relevant to the user's hotspot description; this archive alone cannot prove which active addon painted the particular minimap. Geographic evidence only, not an automatic event/item/escort strategy. |
| CarboniteNodes/CarboniteNodes.toc | 1.00; Interface30300; load-on-demand | Static herbalism/mining locations, with an explicit import procedure described by the addon. | Gathering hints. Presence of this file is not evidence the user imported it or that a node currently exists. |
| CarboniteItems/CarboniteItems.toc | 1.00; Interface30300; load-on-demand | Separate static item information. | Possible reference data, not current inventory, learned equipment proficiency, server item customization or a complete scoring model. |
| CarboniteTransfer/CarboniteTransfer.toc | 1.01; Interface30300 | Warehouse transfer helper and a SavedVariables declaration. | Not a quest database. Do not collect cross-account Warehouse data as an incidental import. |
| PallyPower/PallyPower.toc | v3.2.21; Interface30300 | Class/recipient blessing assignments, normal exceptions and aura assignments; flavor-specific tables. | High-value future read-only assignment bridge. Validate active Wrath flavor and map to spell identity; never blindly copy integer selections. |
| Pawn/Pawn.toc | 1.3.8; Interface30300 | Bundled Wowhead stat scales and equipment-comparison code. | Useful to reconcile existing scoring and user-selected scales, not proof of universally optimal DPS. Actual saved selection is absent. |
| CLCRet/CLCRet.toc | Packaged labels r133 and v.1.3.03.025; Interface30300 | Configurable priority recommendations and proc/resource checks. | Historical reference and test ideas, not a replacement combat executor or a live benchmark. Preserve both metadata labels. |
| TurnIn/TurnIn.toc | 2.1; Interface30300 | Automated accept/reward/gossip actions when enabled. | Potential competing action owner, not a trustworthy special-quest recipe database. |
| Mapster; AtlasLoot; ClassLoot; DBM; GTFO; _NPCScan | Mixed versions | Map presentation, loot/class references, encounter warnings and sightings. | Purpose-specific references only. None is a general quest strategy or navigation/LOS oracle. |

No TOCs named for Questie, QuestHelper, pfQuest, GatherMate, Gatherer or TomTom were found anywhere in this archive. This is not a claim about another installation or files outside the upload.

## Carbonite: useful geography, not complete quest instructions

The exact main TOC names version3.34; all three files it directly lists are present. `Carbonite.lua` contains a large packed quest table at LF-based line344. Some data is non-UTF8/byte-oriented. Do not run the addon or casually UTF8-normalize packed strings to extract it. This review did not decode/export that database.

The bundled `CarboniteReadMe.txt` describes a searchable quest database at line249 and specifically identifies four-arrow square markers as **the closest point to reach a quest area** at line517. Therefore a displayed marker is not necessarily an exact creature spawn. Lines562-564 describe static node data being loaded/imported through Carbonite's options. Lines541 onward distinguish its static item information from cached client tooltips.

Any eventual location adapter must preserve quest/objective identity, source map namespace, units, floor uncertainty and area-versus-point shape. Do not treat an area center or approach icon as a verified spawn; do not invent elevation0, reachability or safe ground. Use hints to propose search candidates, then check terrain, current entities and live quest state. A hint cannot authorize killing a cast-credit NPC or establish an item, gossip, transport or escort sequence.

The main addon wraps AcceptQuest and GetQuestReward, but the reviewed wrappers call the retained original functions after addon bookkeeping (`Carbonite.lua:8178-8180,16223-16226`). That is not the same as TurnIn independently deciding to submit those actions.

### Redistribution boundary

The actual `CarboniteLicenseAgreement.txt:15-23` grants a limited personal-use licence and restricts reproduction, modification, distribution and derivative works without authorization. The uploaded Pawn source/readme also declares Attribution-NonCommercial-NoDerivs3.0 terms. These are observations of the supplied terms, not a legal determination of their enforceability or ownership of individual factual coordinates. A different modern project's GPL declaration does not establish permission for this historical copy. **No Carbonite/Pawn database, executable source, decoder or bulk extracted dataset is being published to the public repository.** Review permissions for the exact source and intended use before a raw-data adapter or redistribution. Publicly licensed alternative datasets still need core/version/schema verification; no alternative was silently substituted for this upload.

## PallyPower: flavor and assignment identity must be explicit

The actual TOC says v3.2.21 while its X-Curse-Packaged-Version still says v3.2.20. Preserve both observations; they do not identify a clean upstream release. The script initializes separate Vanilla, TBC and Wrath assignment tables (`PallyPower.lua:17-29`). Its mode selection at lines31-43 depends on an embedded date, expansion level and certain realm names. That addon-specific heuristic must not become CopilotBuddy's core detector. No server identity is inferred from the realm names appearing in source.

The concrete compatibility trap is in `PallyPowerValues.lua:267-296`: normal assignment4 maps to Salvation1038 in the Vanilla/TBC table, but Sanctuary20911 in the Wrath table. Greater assignment4 likewise differs. A raw integer imported without the active flavor would describe the wrong buff. The script exposes `PallyPower.IsWrath` and stores normal/Greater exceptions and aura assignments separately.

Recommended bridge contract: optional, read-only, inspect actual loaded version/flavor, capture roster/provider/recipient and assignment together, translate to named spell families, respect manual/raid assignments and last-moment learned-spell/reagent/coverage checks. Do not silently replace a declared role's buff, upgrade a source's permissions, write PallyPower's tables or activate unknown-flavor assignments. Revalidate on roster, specialization, assignment and aura changes. This is a proposed bridge, not implemented W61 behavior; the existing tested Singular policy remains unchanged.

## Pawn confirms existing provenance, not independent validation

`Pawn/Wowhead.lua:77-86` contains a static Retribution scale. Its Strength80 and Agility32 exactly correspond, divided by100, to the current AutoEquip XML's Strength0.8 and Agility0.32. The other overlapping named stat coefficients also follow that normalization. The current XML explicitly says it is normalized from the bundled Pawn/Wowhead scale:

`runtime-snapshot/Data/Weight Sets/Paladin-Retribution.xml`, inspected at3a0820da, blob **0b7a0d07f73387d4c184a00a2b05163f45bfccff**.

This establishes a common data lineage, not two independent sources validating optimal numbers. It does not establish that every socket/bonus rule is equivalent, that the user selected that scale, or that hit/expertise caps, set bonuses, weapon interactions and encounter effects are modeled. The separate SmartLoot preset discrepancy remains the previously documented issue; no weights are changed here. An explicit selected-scale adapter must preserve source, normalization and user intent rather than copying whichever scale is found first.

## CLCRet is a useful original-era comparator

The default FCFS list is Hammer of Wrath, Crusader Strike, Judgement, Divine Storm, Consecration and Exorcism, followed by empty slots (`clcret.lua:149-161`). These are default slots, not an unconditional action order: the reviewed decision code contains usability, Art of War and mana guards (`clcret.lua:1453-1480`). The file also contains Protection options, so copying every named ability into a Ret routine would be incorrect. Actual saved priority and gear/profile settings were not provided.

The historical CurseForge file v.1.3.03.025-nolib is dated15July2010 and labelled3.3.5. This corroborates the age of one packaged label, not a byte match or proof of the upload's complete origin. Reference: https://www.curseforge.com/wow/addons/clcret/files/439501 . Keep existing original-version research rules and verify individual mechanics. No recommendation-engine transplant or measured DPS superiority is claimed.

## TurnIn is an interaction-conflict candidate

`TurnIn.lua:268-318` can respond to quest windows by calling GetQuestReward or AcceptQuest when its state/NPC policy permits. Later gossip code selects options. Its saved NPC rules are not a core-qualified quest/objective strategy, and the actual saved enabled state is absent. This is a **source-supported potential concurrency problem**, not an observed live regression.

Use one action owner while botting. For this exact addon, `/ti status` reports its state and `/ti off` disables its automatic event handlers (`TurnIn.lua:85-88,160-205`). Turning its automation off while Wholesome owns quest interaction is the immediate low-risk recommendation; nothing has been changed automatically. Do not blindly reward/accept/gossip through both systems. Carbonite's reviewed pass-through hooks should be distinguished from this independent automation. The bot should eventually detect/warn about conflicting automation or support an explicit coordination mode, tested across delayed UI events and reward selection; that is not implemented here.

## Archive layout and version ambiguity

The 265 nested TOCs include libraries and substantial bundled duplicate trees. Four top-level folders have TOCs only below another folder, not the normal matching root entrypoint: Cartographer, ChatFilter, PhoenixStyle and RaidRoll. Under the normal loader layout these would need correct placement to be independently discovered; explicit references from other scripts are a separate matter. The archive is not proof of enabled or broken runtime state. Do not auto-move/delete them.

Concrete duplicate versions show why recursive first-match imports would be unsafe: root AtlasLoot is v6.05.04 while AtlasLoot/AtlasLoot is v5.11.04. Root PallyPower declares v3.2.21 while AtlasLoot/PallyPower declares v3.2.20. Their TOC bytes differ; the nested CLCRet TOC matches the root TOC. This does not establish that the entire CLCRet source trees are identical. The scanner's recursive discovery must not label nested copies as additional active addons.

DBM-Core declares Interface30300 despite a10.1.13_alpha addon version and Warmane-related project metadata. An addon version is not the client version, and project branding is not the user's actual realm/core. Several files reference other-expansion or realm-specific behaviors; select actual original-WotLK evidence rather than treating the whole upload as homogeneous.

## Integration decision and next work

The upload is useful. Highest-value candidates are (a) Carbonite's geographic search evidence after source-permission and format review, (b) PallyPower's explicit live assignments for complementing buffs, and (c) reconciling the existing shared Pawn/AutoEquip lineage without inventing new weight accuracy. CarboniteNodes is separately useful for gathering, while TurnIn needs conflict avoidance before concurrent questing. None authorizes automated copying of all addon tables into quest_data.json.

Continue with coherent, tested slices: source identity/provenance and runtime observations first; permitted format-specific hint adapters second; independently validated map/floor/terrain conversion before runtime use; explicit item/gossip/event/escort strategies and authoritative credit separately. Retain current unknown-state, target, combat, vendor, ownership and cancellation controls. Continue the outstanding all-class buff, equipment/rewards, underwater and full GatherBuddy transition audit. Do not mark those requirements complete from this inventory.

## Evidence and publication status

All internal source line references use one-based LF-delimited lines in the exact uploaded bytes, not generated/normalized source. Selected original SHA256 identities:

| Relative to AddOns | SHA256 |
|---|---|
| Carbonite/Carbonite.toc |3aa9324688a6b02a7966e5d2f93e623773e85dd030f7c34f399b54e64619a032|
| Carbonite/Carbonite.lua |e1050ad789b2518d1c371025ff6f3a85b079d7bc72a6e10a8af37e9dc8cdb1e8|
| Carbonite/CarboniteReadMe.txt |dbef50f6ecd491844c1e8027fab62d626aec59add4ff68869758fe71ec715664|
| Carbonite/CarboniteLicenseAgreement.txt |c4748b1f7358c96f28a287294126632ba80f3f8f6295b898c541870a54ae73d6|
| CarboniteNodes/CarboniteNodes.lua |3412d0d7775951134ec5a6bc7bc41caf86dc0b11c38467e7d7c4124edcfbfb47|
| PallyPower/PallyPower.lua |fef996fa2fb1389c9ad9cd69467e7d479e88ac753ddb4841e6a3bfc0e4d79f71|
| PallyPower/PallyPowerValues.lua |c5b5adf3c3f72922420150c2a2bb795f4afc28322edd24a807bc477d379b1a24|
| Pawn/Wowhead.lua |160e2f5d15e2706f49383cb804592bb9e7a629a287243a7dbfb0486f71ad8da7|
| TurnIn/TurnIn.lua |4b2c76fdb849f0d404dbe61d10523627ee8cc695961cd46ec1346846a088a035|

Native repository read at start: PR51 head3a0820da, latest recorded tested code6cd4f9f9, masterf462a9bb. W60's integrated17/17,91 retained groups and74 analyzer tests are prior recorded results; W61 did not rerun those C# jobs or certify them afresh. This continuation is archive analysis and documentation only, not a gameplay repair, full parser, live bridge, runtime hint activation or independent review. No third-party addon payload is included in the public documentation. Keep PR51 draft/unmerged; preserve backups/exclusions, README and installed files. Re-read live heads before subsequent writes.
