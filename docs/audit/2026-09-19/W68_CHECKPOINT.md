# W68 — authoritative UseItemOn acknowledgement boundary

19 September 2026. Repository `jeofwong/CopilotBuddy-private`, draft PR51, branch `audit/next-55-equipment-observation-20260917`.

Verified code **0efaa29cdae649b2fb3c0eba2c2e31f5a3dce310**, tree **1eb88058a060339b30f01b6dc91738c49cf4c2cf**. W67 and all earlier requirements remain retained.

## Red

Test-only **a0074d1354bbdd215b91d0a2a6c559f650ce801c** added `QuestItemAuthoritativeProgressRegressionTests`. Integrated run **35413218193**, artifact **10575435780**, SHA256 **d4cae4f208d04d9412a64475a24de0d5473a18f9d6248cbce5c2caa28fb4cbe2**, failed as intended because tracked UseItemOn had no authoritative success contract. Host compile succeeded. No native use/server execution occurred.

## Production

`5a3fb22d0c13038545dde85eea504095d4a770fa` adds an opt-in success-evidence contract to the complete tracked `runtime-snapshot/Quest Behaviors/UseItemOn.cs`. Constructor-order correction **485ff12db6b40289b95e30d17c3c0eed11f53a1c** validates ObjectiveProgress only after QuestId exists. Test strengthening **0efaa29cdae649b2fb3c0eba2c2e31f5a3dce310** pins that ordering.

Legacy explicit profiles remain unchanged by default: `SuccessEvidence=InvocationCount` retains existing NumOfTimes semantics.

Opt-in generated/curated execution can request:
- `ObjectiveProgress`: capture actual descriptor `ObjectivesDone[ObjectiveIndex]` at start and acknowledge only a later increase.
- `QuestComplete`: acknowledge only explicit observed quest completion.
- `MaxAttempts`: bound local submissions separately from success evidence.

Invocation count is explicitly not server acknowledgement in the authoritative helper. Missing baseline for ObjectiveProgress defers without an item use. Exhausted attempts set `AuthoritativeAttemptsExhausted` and finish the local behavior as a bounded deferral; they do not mark the quest complete.

## Green

Exact final integrated run **35413466475**, artifact **10574816771**, SHA256 **ce28c593d2908357f709784777165f6b446b89a0c279aca7348fe1421ace8a9b**, completed success. Host run **35413466374**, artifact **10575620041**, SHA256 **95c57869cbdbe18f0ea49eb922cf48b5ca1bc2d18a73d892e496d57432313b3e**, completed success, compile only.

The final integrated run reruns the previous target-selection, dispatch and quest-lifetime controls, so opt-in authoritative mode did not reinterpret the legacy default.

## Remaining item-strategy work

This does not yet wire QuestStrategyPack into DataLoader/Scheduler/ProfileBuilder. No automatic special quest is enabled.

A liveness gap remains before wiring: if an item is consumed/disappears after one submission and authoritative progress never arrives, attempt count cannot advance and the behavior can wait indefinitely. Add a bounded post-submission acknowledgement deadline before strategy generation.

After that, strategy runtime identity must include exact strategy-pack bytes without altering legacy DatasetFingerprint when no pack exists. Only UseItemOn should be wired first. GossipEvent and Escort remain separate.

Broader open scope remains all-class effective-strength/exclusive buffs, cap/loadout/set/proc equipment/rewards, runtime Carbonite terrain/floor/Z, underwater escape, full GatherBuddy/rest/remount, native UI/slot/cursor/LOS, independent review and original-client/server acceptance.
