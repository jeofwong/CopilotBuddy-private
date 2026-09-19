# W56 — LOS source map, version evidence and remaining requirements

Review date: 17 September 2026. Own-source base: 527ab39ddf5a28b7ef3a3486e8bfe16e48d593b7; final verified code: 6e9049c1297058bf7128114e88a87bb7fcf2eafc. Target: original WoW3.3.5a build12340, not modern Wrath Classic.

## Verified edges

| From -> to | Finding / evidence level |
|---|---|
| Shared `Spell.Cast` -> admission -> terminal backend | Initial sight/range permission could become stale during setup/logging. Exact shared owner-region tests53/53 after repair. Backend/world are controlled; native spell execution is not measured. |
| Shared `Spell.Buff` -> selector -> `Spell.Cast` | Retains earlier aura coverage and caster semantics; denied LOS submissions cannot acquire success bookkeeping. This is not proof that all aura changes at all callbacks are fenced. |
| `CreateMoveToLosBehavior` -> `Navigator.MoveTo` | Complete Movement owner tests74/74 after repair, with captured player, one selector evaluation, finite geometry and failed-path fallthrough. Navigator effects are controlled. |
| `CreateEnsureMovementStoppedWithinRange` -> `MoveStop` | Range plus visible current target is required; captured-owner changes cannot authorize the tested stop. This does not establish a general movement lease. |
| Druid instance combat buffs -> LOS helper -> Rebirth | Source-traced dead friendly caller; five positive helper controls prevent a blanket corpse veto. The full Druid combat tree and live resurrection are not newly executed. |
| `WoWUnit.InLineOfSpellSight` -> `GameWorld.IsInLineOfSpellSight` -> native trace | Existing source uses geometric trace, missing player returns false and missing executor/caught trace errors assume obstruction. Native/world observations were not executed by the new tests. |

## Own-source identities

- `Styx/WoWInternals/WoWObjects/WoWUnit.cs`, blob `5d7c310fc9b32787dfa990acda14dfea670513df`: GetTraceLinePos uses X/Y/Z+2.132; spell sight uses the two unit trace positions.
- `Styx/WoWInternals/World/GameWorld.cs`, blob `f99351339d3f36b9cb9d2511b92c5048bc9b3bc0`: HitTestSpellLoS=16; general LOS and ground/structure masks differ. Trace error handling is conservative, but a successful-looking default read and complete session freshness require separate tests.
- `Styx/Logic/Combat/SpellManager.cs`, blob `48cc484c0f0b098d50e1a02be241997572915e72`: CanCast performs range/LOS when requested; Cast deliberately does not repeat CanCast. Its backend contract was not changed.
- `runtime-snapshot/Routines/Singular wotlk/ClassSpecific/Druid/Common.cs`, blob `72a50d6542f12991f132923efb1e6d9f23b6dae6`: selects a dead tank/healer and invokes the LOS helper before Rebirth.
- Final Spell and Movement blobs are recorded in W56_EVIDENCE.json. Existing self/melee shortcuts are preserved, not independently certified as appropriate for every spell.

## External evidence: comparison, not proof of this realm

Primary implementation reviewed: AzerothCore, `azerothcore/azerothcore-wotlk`, `src/server/game/Spells/Spell.cpp`, blob **f1a236f650282630c1e09baa4fe09bd17ea9dbd5**, lines6100-6260 at retrieval. Source URL: https://github.com/azerothcore/azerothcore-wotlk/blob/master/src/server/game/Spells/Spell.cpp . The moving branch URL is accompanied by the content blob so future readers must verify identity rather than assume line numbers remain current.

This 3.3.5a-compatible emulator checks target and destination LOS and range, but also recognizes spell attributes and caster cases that bypass or alter LOS checks. It supports the architectural distinction between ordinary spell visibility, ground targeting and spell-specific exceptions. It does **not** prove the owner's realm configuration, the original client's exact native mask, or a universal melee rule. No native mask, offset or spell exception was imported from that emulator in W56.

Official project version references consulted: https://github.com/TrinityCore and https://www.azerothcore.org/wiki/realmlist . Current branches must be checked against the original-client policy; a generic WotLK label is not enough. Incomplete search results or failed page retrievals were not used as implementation authority.

## Remaining source-traced gaps and proposed acceptance tests

### 1. Native geometry and observation validity

Do not blindly replace spell-LOS with the all-collision mask. It can change legitimate spell exceptions and differs from proof of a walkable route. Test unavailable or partial native return reads, invalid/non-finite trace positions, frame/memory/executor replacement, cancellation/interruption and known hit/no-hit results. Verify original-client native behavior with supervised client evidence before changing offsets or masks. The current fixed trace-height assumption and moving-transport world coordinates also need representative validation.

### 2. A visible casting position is not necessarily the target position

The current recovery still requests movement toward the target. It does not choose a safe alternative firing position, prove a route around a corner, or bound repeated no-progress attempts. A future controller should combine valid candidate geometry, spell-specific visibility/range, reachable path evidence, group/encounter movement permission and bounded progress deadlines. Keep it tied to the captured combat target and current movement authority. Do not stop a replacement route during cleanup or interpret unknown reachability as a proven impossible target.

Minimum acceptance scenarios: target behind a wall at short range; walkable corner versus inaccessible roof; blocked nearest candidate with a safe alternative; target changes floor/transport; repeated failed routes; restored sight; active cast/channel; movement disabled by encounter; defensive action remains reachable. Genuine real-world acceptance is still needed after controlled tests.

### 3. Off-target facing remains separate

The unchanged `CreateEnsureTargetAndFaceBehavior` first returns when `NeedsOffTargetCastSetup` is false, but that predicate becomes false once the target is current. Source review suggests the later facing branch can be bypassed during target switching. This is a hypothesis requiring an actual owner test, not a newly verified failure. Preserve self/friendly/casting rules and test target changes, turn-in-progress, disabling movement and ownership changes before modifying it.

### 4. Ground-targeted spells use another pipeline

`CastOnGround` has a separate pending-cursor/remote-click sequence and repeated location retrieval. The new ordinary Cast tests do not prove its target-point visibility, complete cursor ownership, range freshness, successful spell activation or replacement-safe cleanup. Add tests for unavailable/mismatched cursor, location changes, blocked geometry, cancellation and legitimate self-centered effects before refactoring this path.

### 5. Last-moment target and aura changes

W56 rechecks ordinary casting requirements/visibility after logging against the selected recipient. That is not a complete atomic observation lease across every getter, logger, selector and backend. Changes to player/target identity or aura coverage inside later callbacks need separate tests. The earlier shared buff checks are retained; do not claim a universal rank-aware no-spam or singleton coordination engine from these results.

### 6. Whole-routine ordering and efficiency

The new movement helper evaluates its supplied selector once rather than four times in the controlled test. It also preserves casts/channels and returns Failure on path failure. This is not a measured improvement in FPS, total trace calls or DPS. Repeated LOS/native reads across an entire routine may benefit from a short-lived, explicitly owned observation snapshot; benchmark and test invalidation before caching. No broad cache or throttle was added speculatively.

## Release boundary

PR47 is merged; these W56 fixes are on unmerged PR51 with retained W55 equipment repairs. Tests establish helper decisions, not original-client dispatch, all-class rotation correctness or an optimal obstacle planner. Independent review, remaining quest/gear/buff/water requirements and supervised original-client acceptance stay open.
