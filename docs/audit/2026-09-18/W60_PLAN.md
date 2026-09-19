# W60 addon evidence boundary implementation plan

Goal: identify installed addon sources without executing or copying their Lua; validate optional normalized map hints without granting runtime authority.
Architecture: stdlib-only offline inventory + strict quarantined JSON hint envelope. No native process, credentials, SQL, live addon bridge, source-specific Lua decoder or scheduler change.
Basis: approved W59 provenance design and current request to inspect the exact AddOns folder. Local Windows source remains inaccessible until explicitly connected or its inventory is returned.

1. Add tests for bounded read-only inventory (TOC metadata, hashes, unknown versions, links, missing/unreadable roots, no WTF traversal, output location) and quarantined hint validation (version/core provenance, map namespace, finite coordinate units, entity namespace, unknown objective, duplicate keys, bounded input, no automatic world Z or execution).
2. Run tests before implementation; distinguish a new-module import failure from a gameplay assertion reproduction.
3. Implement one standalone offline tool; run the same tests on real temporary files and JSON inputs. Preserve all existing bot/tests/workflows.
4. Publish coherent test-only and utility commits through native GitHub actions, re-read refs and exact blob identities. Use the existing analyzer discovery; do not weaken any CI guard.
5. Inspect integrated/host results and save exact artifact identities. Write findings and next steps; do not claim actual installed addon verification or a completed Carbonite importer.

Constraints: original3.3.5a build12340; TrinityCore3.3.5 primary, AzerothCore WotLK secondary. Legacy licence review before any dataset distribution. Addon hints never authorize kills, item use, progress or safe paths. No absolute machine path/account SavedVariables in inventory output; no upload from the tool. Pending real installed sources remain explicit.
