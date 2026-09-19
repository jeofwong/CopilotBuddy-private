# W62 — verified TurnIn conflict guard and interrupted-work reconciliation

18 September 2026. Repository `jeofwong/CopilotBuddy-private`, ID1367174964. Draft PR51, branch `audit/next-55-equipment-observation-20260917`. Original WoW3.3.5a/build12340; TrinityCore3.3.5 primary, AzerothCore WotLK secondary. These are compatibility targets, not live certifications.

## Recovered state; do not restart historical work

The initial native PR read matched the saved W61 documentation commit `1f725161a9503333fbf2953b3a83619e5eb55542`. Its prior tested code was W60 `6cd4f9f9`. The current branch had no intervening unverified test-only addition at that read. Older W42/PR44 handoffs describe historical states, not the current continuation point.

The new integrated run still executes the historical known-master flight suite12/12; the W58 quest-progress repair33/33; W59 merchant20/20 and combat-gap26/26; and the retained item-selection44/dispatch72, Greater/Sanctuary87, aura70/Lowbie10, Protection53, engagement38, equipment/buff/LOS and legacyEscort47 controls. These were retained, not recreated. Passing legacy Escort does not establish automatic escort recognition.

The user already supplied AddOns.zip, SHA256 `75055256969fd6d8baa218113fe49e08773965b09efd4bd35530c106c861eb3c`. W61's exact-source inventory remains the reference. No additional local-drive or account-state access was obtained. Do not ask for the same uploaded inventory again. Current enabled addons, PallyPower assignments, Pawn settings and realm-specific data still require actual runtime observations rather than inference from files.

## New runtime change: a positively observed TurnIn conflict suspends quest execution

The uploaded TurnIn2.1 independently handles accept/reward/gossip events when its status is active. The shared published quest root previously had no check for that competing automation owner. W62 adds the check to `Bots/Quest/PublishedQuestRoot.cs`; the original behavior-tree executor, publication/lifecycle gates and protective branches remain.

- A known active result prevents new quest effects and preempts an already-running quest before the next controlled effect. It does not finish or advance the quest, release scheduled item protection, close an interface, toggle an addon, or fabricate progress.
- Inactive or absent results release this root's recorded conflict and allow a fresh quest lifetime, subject to the existing publication checks. Repeated conflict does not repeat cleanup or fall through into roaming.
- Null, empty, malformed, multiple-result or failed observations cannot clear an already observed conflict. Cancellation/interruption retains its exact stop signal and existing cleanup behavior.
- Death/combat priority bypasses the optional query. Service behavior remains available when the conflicting quest loses exclusivity. Each root owns its conflict state independently.
- The observer is called once per eligible root tick, not on each nested executor admission. It is a new native query cost, not a measured performance improvement. There is no long-lived timestamp cache that would knowingly authorize work from stale clearance.
- A diagnostic is emitted on transition into a known conflict, with `/ti off` as the user-controlled action. No addon settings are changed automatically.

The default observer performs a fixed, read-only Lua query using `rawget`, `type` and ordinary truthiness. The uploaded TOC/header says2.1, but its actual runtime global `TI_VersionString` is `"2.0"`. The query observes that reviewed layout, `TurnIn`, and `TI_status.state`; it does not call the addon or import its source. Version strings and globals are compatibility observations, not an authenticity or server-core detector.

## Important limits and tradeoffs

**An initially unknown observation preserves legacy admission. It is not reported as explicit addon absence or a verified conflict-free state.** This is an additive positive-conflict guard, not a universal addon interlock. Unsupported versions/layouts and other automation addons are outside its current detection contract. Once a conflict is positively observed, unknown cannot release it.

The guard runs at the published-root tick boundary. It cannot undo a reward/acceptance the addon already submitted, intercept every native UI event, or prove that an addon cannot activate after a check within the same tick. It leaves TurnIn itself untouched, so `/ti off` remains the direct way to stop its independent automation. These tests do not attach to a running client or execute third-party addon Lua.

The new two-argument constructor is a read-only observation seam. Tests use the actual compiled host root with controlled external results; the old root's one-argument constructor runs as-is when the seam does not yet exist. Therefore the pre-feature failure demonstrates missing guard behavior, not native TurnIn execution or a missing-method exception. The real publisher, outer lifecycle gate and forced executor remain in the fixture; terminal/support leaves are controlled.

## Test-first commits and exact Windows evidence

| Revision | Meaning | Verified result |
|---|---|---|
| `a54bd21dc5e3d2ef6838fe4f6d8da1992a0ccb40` | New24-case test only | 3/24;21 intended assertion failures;0 unexpected. Other91 groups and16 companion entries pass. |
| `a4d82ce7ff3cee703bc6e64fa521141fb2e04dc9` | Narrow runtime guard | Unchanged24/24;0 assertions;0 unexpected. All17 integrated entries and92 aggregate groups pass. |

Latest verified code is **a4d82ce7ff3cee703bc6e64fa521141fb2e04dc9**, tree **6ebe86b159f196fe3d14734f0b0aff45874aeb47**.

- Red integrated35311539169/art10533780541: SHA256 `619dd63f6d2322a3aafdfb4ba389672d21c3fcba79717c05d4c46ed2845381c0`.
- Green integrated35312015670/art10533840947: SHA256 `3866cd3ea66c1cf5ada2b1a799fc8531ef26daad16cbf0e2c431a50e51a8aa39`.
- Host35312015667/art10534325066: SHA256 `aca28821258874f4963403d00fc28a90773402ab8220362d85a784f53b77e628`;0 errors,3292 warnings, compile only.

Both integrated archives have148 fully covered internal-manifest entries. All1780 source/configuration hash-index entries are compared; only PublishedQuestRoot.cs changes between red and green. All104 normalized members and the other91 aggregate named-outcome inventories match. All74 retained Python analyzer tests pass. Narrow archives contain indices and normalized tests, not all1780 original source files; no full independent source download is claimed.

Native source blobs match the prepared payloads: original root `06ab9f9dda109dae5cca08ffa14be585adc39f8b`; repaired root `633d8095c035d92228b4385d6bd32ebcc458967b`; new test `427ed1526a19490f338d1943064ae315bbca3d9d`. The complete test source is identical across the production repair. No workflow or normalizer change was needed.

The portable verifier initially assumed stderr aggregate failures appeared before the next stdout BEGIN marker; the actual red archive interleaves those records. Its corrected association uses explicit group names and still requires exactly21 intended assertion records plus the one target aggregate failure, rejecting any unaccounted failure. No bot fixture or result was changed. Thirteen retained verifier utility tests pass; they are not gameplay cases.

A separate local probe executes only our fixed query against13 synthetic cases using the container's **Lua5.4** shared library. It does not execute TurnIn, WoW, or an original Lua5.1 runtime. It checks absent/partial/malformed states, Lua-truthy zero/strings, and no invocation of a synthetic `__index` callback. This supplemental probe is not original-client acceptance and is not added to the24 Windows cases. The used language constructs were checked against the official Lua5.1 manual: https://www.lua.org/manual/5.1/manual.html (values/types and rawget, read18Sep2026).

## Review and remaining work

The named Copilot reviewer request returned GitHub422: reviewer is not a repository collaborator. No assignment/access change or independent approval is inferred. An implementing-assistant source/evidence review is not independent review.

W62 implements TurnIn conflict detection, **not** the PallyPower bridge or Carbonite importer. Keep the existing W60 neutral-hint quarantine and W61 source/licence review. Future PallyPower handling must verify the actual Wrath flavor and translate assignment4 correctly; no raw integer or addon realm heuristic may become a core detector. Carbonite areas remain search evidence, not exact world spawns, elevation, reachability or action recipes. Do not upload restricted addon databases/source or personal SavedVariables.

Continue explicit, source-backed special-item/gossip/event/escort strategies with core/realm provenance and authoritative credit; final item-slot/UI/cursor/LOS admission; full GatherBuddy/Targeting/rest/remount transitions; all-class strength/exclusive buff policies; cap/loadout equipment/reward decisions; underwater recovery and original-client/server acceptance. This new green result does not close those requirements.

Master remains the previously approved PR47 merge **f462a9bb4eb18acac9069f495177df35672286d5** at the last read. Preserve README and backups c43c50d8/8382a7ec/518baec5; exclude25/43/45. No PR51 merge, force push, deployment or installed-addon/binary/mesh change. Subsequent checkpoint publication is documentation only; no pending unexecuted test remains at the verified code revision.
