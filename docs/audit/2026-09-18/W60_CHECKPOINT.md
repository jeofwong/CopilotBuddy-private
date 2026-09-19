# W60 — addon inventory and quarantined quest-location evidence

18 September 2026. Repository `jeofwong/CopilotBuddy-private`, draft PR51, branch `audit/next-55-equipment-observation-20260917`.

## Published work

Test-only commit **82ac2a091bce94aaae28a4ae184ed604b6e8c39d** adds `Tools/EvidenceAudit/test_addon_evidence.py`. Implementation **6cd4f9f9a14fe03c1921f1e56824e6532d63318d**, tree `52d5010d490a0ad678f6e88690ae1db72dbf06ad`, adds the standalone `addon_evidence.py`. No existing runtime, test, workflow or quest dataset was changed. The documentation following this revision is not another gameplay repair.

The tool inventories bounded text-file hashes and TOC metadata below an explicitly selected AddOns root. It rejects/fences ordinary symlinks and Windows reparse points, excludes the WTF/account tree, never runs addon Lua, and writes only a new output outside inputs. Candidate categories are filename hints, not verified feature or enabled-state claims. Its separate strict neutral JSON validator preserves point/box shape, map/entity namespaces and unknown indices; output is always quarantined with no executable world coordinates or action authority.

**The user's actual `D:\World of Warcraft 3.3.5a\Interface\AddOns` folder was not accessed.** No local filesystem connection was available; a relevant connection option was surfaced. A read-only inventory command is provided, not a claimed installed-addon scan. No Carbonite data/source or account SavedVariables was imported into the public repository.

## Source review

Read `docs/audit/ADDON_EVIDENCE_335.md` for the candidate review, exact pins, command and format. The inspected Carbonite3.34 TOC declares Interface30300; its legacy licence has restrictive terms. No later project's different licence is assumed to authorize copying this archive. The inspected Questie335 fork includes expansion-specific datasets/corrections and compatibility layers; its name alone does not validate every record. GatherMate sources may be AzerothCore-derived or custom-realm-specific. None is silently selected as this user's installed version or realm's authoritative database.

Addon quest areas can be supplementary search hints. They are not complete special-item/gossip/escort recipes, proof of current spawns, safe paths, or server completion. Zone map coordinates require verified transformation and elevation/routing before use. Unknown Z is not zero. Current Wholesome does not consume the staged output; actual source-specific decoding and runtime enrichment remain unimplemented.

## Actual verification

- Local unchanged new-tool suite: **41/41**. The initial missing-module failure was preserved as scaffolding evidence, not an intended gameplay assertion reproduction.
- Windows integrated run **35306915012**, artifact **10532375902**: **17/17 build/run entries**, all **91 retained aggregate groups**, all **74 analyzer tests** (41 new,33 existing). No new test skip is recorded. Whole artifact SHA256 `2b01ae9ad03e9cc27cd9e301a1f0e03a00fa212ecd75e3961916c2d0ec1d867c`.
- Test-only run **35306570163**, artifact **10532240617**: the other16 entries pass; Analyzer fails because `addon_evidence` does not yet exist. SHA256 `669290d8d85d40a571abf784a819a4ae75279444625ad3dde1f00e6c83f7ad9e`. No assertion result for the unimportable module is invented.
- Recovered W59 artifact **10531141312** is unchanged. All1777 previous source/config hash entries,103 generated members and91 aggregate group names match current execution. Current input index has1779 entries, with only the two new Python paths added.
- All three archives' full SHA256/CRC and147 internal manifest entries each are verified. Both new source payloads match native Git blobs and CI input hashes. The archives are narrow: their indexes are compared, not falsely described as1779 individually re-downloaded source files.
- Synthetic local CLI checks cover new output, rejected overwrite and missing root. No installed game or native addon execution occurred. A filtered query returned no new host-build check for this Python-only revision; no fresh host warning count is claimed.

The portable package verifier reproduces the evidence comparisons. The41 tests are offline tooling checks, not new quest/combat gameplay cases. The integrated C# results preserve existing coverage; they do not prove every core, quest or live client path.

## Continuation and protected state

W59's merchant20/20 and combat-gap26/26 repairs remain; do not recreate them or revive the older W58 test-only frontier. Keep quest progress33, selection44, dispatch72, Greater87, aura70/Lowbie10, Protection53, engagement38, equipment/buff/LOS and legacyEscort47 controls.

Next acquire exact installed metadata, then review applicable data permission, source layout/corrections, coordinate namespace and core/realm provenance. A reviewed adapter may populate the neutral quarantine format. Runtime integration needs map/elevation/reachability validation and actual owner tests; special-quest recipes and acknowledgement are a separate layer. Keep all W59 unfinished quest/native/LOS/cursor, full GatherBuddy/Targeting/rest/mount, rank/exclusive-buff, equipment/reward and underwater requirements open.

Master remains the approved PR47 merge `f462a9bb4eb18acac9069f495177df35672286d5`. Preserve README and backups c43c50d8/8382a7ec/518baec5; exclude25/43/45; do not remerge44/47. No PR51 merge, force push, deployment, installed addon changes or binary/mesh replacement. This is an implementing-assistant evidence review, not independent approval, full importer completion, live-server certification or exhaustive completion.
