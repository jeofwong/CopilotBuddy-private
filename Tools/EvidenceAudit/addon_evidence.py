"""Read-only addon inventory and quarantined 3.3.5a map hints.

This offline tool never runs addon Lua, uploads files, reads account data, or
changes Wholesome's runtime database. TOC declarations are evidence, not proof
of the installed client's active addon set or a realm's server implementation.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import sys
from typing import Any

DEFAULT_ADDONS = r"D:\World of Warcraft 3.3.5a\Interface\AddOns"
TEXT_SUFFIXES = {".toc", ".lua", ".xml", ".json", ".txt", ".md"}
EXCLUDED_DIRECTORIES = {".git", ".svn", "wtf", "savedvariables", "__pycache__"}
TOC_KEYS = {"interface", "title", "version", "author", "notes", "dependencies",
            "requireddeps", "optionaldeps", "savedvariables",
            "savedvariablespercharacter", "loadondemand", "defaultstate"}
MAX_FILE_BYTES = 32 * 1024 * 1024
MAX_DEPTH = 16
MAX_TOC_BYTES = 1024 * 1024


def _positive_limit(value: int, label: str) -> None:
    if type(value) is not int or value <= 0:
        raise ValueError(label + " must be a positive integer")


def _link(info: os.stat_result) -> bool:
    return stat.S_ISLNK(info.st_mode) or bool(
        getattr(info, "st_file_attributes", 0) & 0x400)  # Windows reparse point


def _identity(info: os.stat_result) -> tuple:
    # Python 3.12+ deprecates st_ctime_ns as creation time on Windows, and
    # path-stat versus handle-stat observations can disagree for a fresh file.
    # Keep a stable object/content identity there; retain ctime on POSIX.
    if os.name == "nt":
        return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns,
                getattr(info, "st_birthtime_ns", None))
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns


def _no_link_parents(path: Path) -> None:
    """Reject existing symlink/junction components instead of following them."""
    for part in (path, *path.parents):
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        if _link(info):
            raise ValueError("Symbolic links and reparse points are not allowed")


def scan(root: Path, *, max_bytes: int = 256 * 1024 * 1024,
         max_entries: int = 50000) -> dict[str, Any]:
    """Hash bounded text snapshots below an AddOns root; retain no Lua payload."""
    _positive_limit(max_bytes, "max_bytes")
    _positive_limit(max_entries, "max_entries")
    root = Path(os.path.abspath(root))
    if root.name.casefold() != "addons":
        raise ValueError("Select the AddOns directory, not the WoW or account root")
    _no_link_parents(root)
    if not root.is_dir():
        raise ValueError("AddOns directory is missing or is not a directory")
    report: dict[str, Any] = {
        "schema": "addon-inventory-335-v1", "complete": True, "issues": [],
        "addons": [], "payloads_included": False, "lua_executed": False,
        "account_data_read": False, "network_used": False,
        "excluded_directories": sorted(EXCLUDED_DIRECTORIES),
        "limits": {"bytes": max_bytes, "entries": max_entries,
                   "file_bytes": MAX_FILE_BYTES, "depth": MAX_DEPTH},
    }
    entries_seen = bytes_read = 0
    entry_limit_hit = False

    def issue(path: Path, reason: str) -> None:
        report["complete"] = False
        report["issues"].append({"path": path.relative_to(root).as_posix(), "reason": reason})

    def children(path: Path) -> list:
        nonlocal entries_seen, entry_limit_hit
        if entry_limit_hit:
            return []
        found = []
        try:
            with os.scandir(path) as iterator:
                for entry in iterator:
                    if entries_seen >= max_entries:
                        issue(path, "entry-budget-exhausted")
                        entry_limit_hit = True
                        break
                    entries_seen += 1
                    found.append(entry)
        except OSError as error:
            issue(path, "directory-unreadable:" + type(error).__name__)
        return sorted(found, key=lambda entry: (entry.name.casefold(), entry.name))

    def snapshot(path: Path, before: os.stat_result) -> bytes | None:
        nonlocal bytes_read
        remaining = max_bytes - bytes_read
        if before.st_size > min(remaining, MAX_FILE_BYTES):
            issue(path, "byte-budget-or-file-limit")
            return None
        flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
        try:
            descriptor = os.open(path, flags)
            with os.fdopen(descriptor, "rb") as source:
                opened = os.fstat(source.fileno())
                if _identity(opened) != _identity(before) or not stat.S_ISREG(opened.st_mode):
                    issue(path, "file-changed-before-read")
                    return None
                data = source.read(min(remaining, MAX_FILE_BYTES) + 1)
                bytes_read += len(data)
                after = os.fstat(source.fileno())
            current = path.lstat()
            if (len(data) != before.st_size or _identity(after) != _identity(before)
                    or _identity(current) != _identity(before) or _link(current)):
                issue(path, "file-changed-during-read")
                return None
            return data
        except OSError as error:
            issue(path, "file-unreadable:" + type(error).__name__)
            return None

    def inspect(path: Path, addon: dict, depth: int) -> None:
        if depth > MAX_DEPTH:
            issue(path, "depth-limit")
            return
        try:
            before = path.lstat()
        except OSError as error:
            issue(path, "entry-unreadable:" + type(error).__name__)
            return
        if _link(before):
            issue(path, "link-or-reparse-point-excluded")
            return
        if stat.S_ISDIR(before.st_mode):
            if path.name.casefold() in EXCLUDED_DIRECTORIES:
                return
            for child in children(path):
                inspect(Path(child.path), addon, depth + 1)
            return
        if not stat.S_ISREG(before.st_mode):
            issue(path, "non-regular-entry-excluded")
            return
        if path.suffix.casefold() not in TEXT_SUFFIXES:
            return
        raw = snapshot(path, before)
        if raw is None:
            return
        relative = path.relative_to(root).as_posix()
        addon["files"].append({"path": relative, "bytes": len(raw),
                               "sha256": hashlib.sha256(raw).hexdigest()})
        if path.suffix.casefold() == ".toc":
            metadata: dict[str, list[str]] = {}
            if len(raw) > MAX_TOC_BYTES:
                issue(path, "toc-metadata-size-limit")
            else:
                try:
                    text = raw.decode("utf-8-sig")
                except UnicodeError:
                    issue(path, "toc-metadata-not-utf8")
                else:
                    for line in text.splitlines():
                        match = re.match(r"^\s*##\s*([^:]+):\s*(.*)$", line)
                        if match and match[1].strip().casefold() in TOC_KEYS:
                            key, value = match[1].strip().casefold(), match[2].strip()
                            if len(value) > 1024 or sum(map(len, metadata.values())) >= 200:
                                issue(path, "toc-metadata-limit")
                                break
                            metadata.setdefault(key, []).append(value)
            addon["tocs"].append({"path": relative, "metadata": metadata})

    for entry in children(root):
        path = Path(entry.path)
        try:
            info = path.lstat()
        except OSError as error:
            issue(path, "entry-unreadable:" + type(error).__name__)
            continue
        if _link(info):
            issue(path, "link-or-reparse-point-excluded")
            continue
        if path.name.casefold() in EXCLUDED_DIRECTORIES:
            continue
        if not stat.S_ISDIR(info.st_mode):
            if path.suffix.casefold() in TEXT_SUFFIXES:
                issue(path, "root-file-not-associated-with-an-addon")
            continue
        addon = {"folder": path.name, "files": [], "tocs": [], "candidates": [],
                 "compatibility_verified": False, "enabled_in_game": None}
        inspect(path, addon, 0)
        label = path.name.casefold()
        if any(name in label for name in ("carbonite", "questie", "questhelper", "pfquest")):
            addon["candidates"].append("quest-map")
        if any(name in label for name in ("gathermate", "gatherer")):
            addon["candidates"].append("gathering-map")
        if "tomtom" in label:
            addon["candidates"].append("waypoints")
        if "atlas" in label:
            addon["candidates"].append("instance-reference")
        report["addons"].append(addon)
    report["entries_seen"] = entries_seen
    report["bytes_read"] = bytes_read
    return report


def write_report(output: Path, report: dict, input_root: Path) -> None:
    """Create a new report outside the input tree; never overwrite a file."""
    output = Path(os.path.abspath(output))
    root = Path(os.path.abspath(input_root))
    _no_link_parents(output)
    if output.resolve().is_relative_to(root.resolve()):
        raise ValueError("Report must be outside the input directory")
    encoded = json.dumps(report, ensure_ascii=True, sort_keys=True, indent=2, allow_nan=False) + "\n"
    with output.open("x", encoding="utf-8", newline="\n") as target:
        target.write(encoded)


def loads_strict(text: str, *, max_bytes: int = 16 * 1024 * 1024) -> Any:
    _positive_limit(max_bytes, "max_bytes")
    if not isinstance(text, str) or len(text.encode("utf-8")) > max_bytes:
        raise ValueError("JSON input exceeds byte limit or is not text")

    def pairs(values: list[tuple]) -> dict:
        result = {}
        for key, value in values:
            if key in result:
                raise ValueError("Duplicate JSON key: " + key)
            result[key] = value
        return result

    def constant(_: str) -> None:
        raise ValueError("Nonfinite JSON number")

    try:
        return json.loads(text, object_pairs_hook=pairs, parse_constant=constant)
    except RecursionError as error:
        raise ValueError("JSON nesting exceeds parser limit") from error


def _fields(value: Any, expected: set[str], label: str) -> None:
    if type(value) is not dict or set(value) != expected:
        raise ValueError(label + " has missing or unsupported fields")


def _integer(value: Any, label: str, minimum: int = 1, nullable: bool = False) -> None:
    if nullable and value is None:
        return
    if type(value) is not int or not minimum <= value <= 0xFFFFFFFF:
        raise ValueError(label + " must be an explicit in-range integer")


def stage(pack: dict, *, max_hints: int = 100000) -> dict[str, Any]:
    """Validate our neutral hint format, NOT raw Carbonite/Questie Lua tables.

    All results remain quarantined even if the producer declares a known core
    or licence. Runtime navigation and quest actions do not consume this format.
    """
    _positive_limit(max_hints, "max_hints")
    _fields(pack, {"schema", "client_build", "source", "hints"}, "pack")
    if pack["schema"] != "addon-hints-335-v1" or type(pack["client_build"]) is not int or pack["client_build"] != 12340:
        raise ValueError("Original client build12340 and addon-hints-335-v1 required")
    source = pack["source"]
    _fields(source, {"provider", "version", "sha256", "core", "revision", "licence"}, "source")
    if any(type(value) is not str or not value.strip() or len(value) > 512 for value in source.values()):
        raise ValueError("Source fields must be nonblank bounded strings")
    if not re.fullmatch(r"[a-f0-9]{64}", source["sha256"]):
        raise ValueError("Source SHA256 must be 64 lowercase hexadecimal characters")
    if source["core"] not in {"unknown", "trinitycore-3.3.5", "azerothcore-wotlk"}:
        raise ValueError("Unsupported source core; do not guess one")
    if type(pack["hints"]) is not list or len(pack["hints"]) > max_hints:
        raise ValueError("Hints must be a bounded list")
    hints, seen = [], set()
    for hint in pack["hints"]:
        _fields(hint, {"quest_id", "objective_index", "entity_type", "entity_id",
                       "map_namespace", "map_id", "floor", "geometry"}, "hint")
        _integer(hint["quest_id"], "quest_id")
        _integer(hint["objective_index"], "objective_index", minimum=0, nullable=True)
        _integer(hint["map_id"], "map_id")
        _integer(hint["floor"], "floor", minimum=0, nullable=True)
        if hint["entity_type"] not in ("creature", "gameobject", "item", "unknown"):
            raise ValueError("Unsupported entity namespace")
        if hint["entity_type"] == "unknown":
            if hint["entity_id"] is not None:
                raise ValueError("Unknown entity namespace cannot authorize a numeric identity")
        else:
            _integer(hint["entity_id"], "entity_id")
        if hint["map_namespace"] not in ("world-map-area-335", "carbonite-zone", "questie-area"):
            raise ValueError("Explicit supported map namespace required")
        geometry = hint["geometry"]
        if type(geometry) is not dict or geometry.get("kind") not in ("point", "box"):
            raise ValueError("Geometry must be a point or box")
        coordinates = {"x", "y"} if geometry["kind"] == "point" else {"x_min", "y_min", "x_max", "y_max"}
        _fields(geometry, {"kind", "units"} | coordinates, "geometry")
        if geometry["units"] not in ("zone-percent", "zone-fraction"):
            raise ValueError("Explicit zone coordinate units required")
        limit = 100 if geometry["units"] == "zone-percent" else 1
        for key in coordinates:
            value = geometry[key]
            if type(value) not in (int, float) or not 0 <= value <= limit or not math.isfinite(value):
                raise ValueError("Invalid zone coordinate: " + key)
        if geometry["kind"] == "box" and (geometry["x_min"] > geometry["x_max"] or geometry["y_min"] > geometry["y_max"]):
            raise ValueError("Inverted box")
        identity = json.dumps(hint, sort_keys=True, allow_nan=False)
        if identity in seen:
            continue
        seen.add(identity)
        record = dict(hint)
        record["geometry"] = dict(geometry)
        record.update(authority="search-hint-only", world_xyz=None)
        hints.append(record)
    return {"schema": "quarantined-addon-hints-335-v1", "status": "quarantined",
            "client_build": 12340, "runtime_enabled": False, "source_verified": False,
            "licence_verified": False, "source": dict(source), "hints": hints,
            "duplicates_removed": len(pack["hints"]) - len(hints)}


def _finite_number(value: Any, label: str) -> float:
    if type(value) not in (int, float) or not math.isfinite(value):
        raise ValueError(label + " must be a finite number")
    return float(value)


def _world_map_area_bounds(pack: dict) -> tuple[dict[int, dict[str, Any]], dict[str, str]]:
    _fields(pack, {"schema", "client_build", "source", "areas"}, "world-map-area bounds pack")
    if pack["schema"] != "world-map-area-bounds-335-v1" or pack["client_build"] != 12340:
        raise ValueError("world-map-area-bounds-335-v1 for original client build12340 required")
    source = pack["source"]
    _fields(source, {"provider", "revision", "sha256"}, "world-map-area bounds source")
    if any(type(source[key]) is not str or not source[key].strip() or len(source[key]) > 512
           for key in ("provider", "revision")):
        raise ValueError("WorldMapArea bounds source identity must be bounded text")
    if type(source["sha256"]) is not str or not re.fullmatch(r"[a-f0-9]{64}", source["sha256"]):
        raise ValueError("WorldMapArea bounds source SHA256 must be lowercase hexadecimal")
    if type(pack["areas"]) is not list or len(pack["areas"]) > 100000:
        raise ValueError("WorldMapArea bounds must be a bounded list")

    areas: dict[int, dict[str, Any]] = {}
    for area in pack["areas"]:
        _fields(area, {"world_map_area_id", "continent_map_id", "loc_left", "loc_right",
                       "loc_top", "loc_bottom"}, "WorldMapArea record")
        _integer(area["world_map_area_id"], "world_map_area_id")
        _integer(area["continent_map_id"], "continent_map_id", minimum=0)
        left = _finite_number(area["loc_left"], "loc_left")
        right = _finite_number(area["loc_right"], "loc_right")
        top = _finite_number(area["loc_top"], "loc_top")
        bottom = _finite_number(area["loc_bottom"], "loc_bottom")
        if left == right or top == bottom:
            raise ValueError("WorldMapArea bounds must have nonzero width and height")
        ident = area["world_map_area_id"]
        if ident in areas:
            raise ValueError("Duplicate WorldMapArea id: " + str(ident))
        areas[ident] = {
            "continent_map_id": area["continent_map_id"],
            "loc_left": left, "loc_right": right,
            "loc_top": top, "loc_bottom": bottom,
        }
    return areas, dict(source)


def _world_xy(area: dict[str, Any], x: float, y: float, units: str) -> tuple[float, float]:
    scale = 100.0 if units == "zone-percent" else 1.0
    # 3.x client map axes are swapped versus world X/Y. This matches the
    # pinned MaNGOS WorldMapArea conversion used as the offline reference.
    zone_x = y / scale * 100.0
    zone_y = x / scale * 100.0
    world_x = zone_x * ((area["loc_bottom"] - area["loc_top"]) / 100.0) + area["loc_top"]
    world_y = zone_y * ((area["loc_right"] - area["loc_left"]) / 100.0) + area["loc_left"]
    return world_x, world_y


def convert_world_map_area_hints(staged: dict, bounds_pack: dict) -> dict[str, Any]:
    """Attach candidate world XY to already-quarantined neutral hints.

    This does not decode Carbonite, invent elevation, validate terrain, or make
    hints executable. Only an explicitly normalized world-map-area-335 hint is
    eligible. Other source namespaces remain unresolved until their own mapping
    is separately proven.
    """
    _fields(staged, {"schema", "status", "client_build", "runtime_enabled",
                     "source_verified", "licence_verified", "source", "hints",
                     "duplicates_removed"}, "staged hint pack")
    if (staged["schema"] != "quarantined-addon-hints-335-v1" or
            staged["status"] != "quarantined" or staged["client_build"] != 12340 or
            staged["runtime_enabled"] is not False):
        raise ValueError("Only quarantined build12340 hints may be converted")
    if type(staged["hints"]) is not list:
        raise ValueError("Quarantined hints must be a list")

    areas, bounds_source = _world_map_area_bounds(bounds_pack)
    result = copy.deepcopy(staged)
    result["conversion_authority"] = "coordinate-candidate-only"
    result["terrain_verified"] = False
    result["path_verified"] = False
    result["world_map_area_bounds"] = bounds_source

    for hint in result["hints"]:
        if type(hint) is not dict or hint.get("authority") != "search-hint-only":
            raise ValueError("Converted input must retain search-hint-only authority")
        if hint.get("world_xyz") is not None:
            raise ValueError("Quarantined hints cannot arrive with world XYZ authority")
        hint["world_xy"] = None
        if hint.get("map_namespace") != "world-map-area-335":
            hint["conversion_status"] = "source-namespace-unmapped"
            continue
        if hint.get("floor") is not None:
            hint["conversion_status"] = "floor-unresolved"
            continue
        area = areas.get(hint.get("map_id"))
        if area is None:
            hint["conversion_status"] = "world-map-area-unresolved"
            continue
        geometry = hint.get("geometry")
        if type(geometry) is not dict or geometry.get("units") not in ("zone-percent", "zone-fraction"):
            raise ValueError("Converted hint has invalid geometry units")
        kind = geometry.get("kind")
        if kind == "point":
            x, y = _world_xy(area, _finite_number(geometry.get("x"), "geometry.x"),
                             _finite_number(geometry.get("y"), "geometry.y"),
                             geometry["units"])
            hint["world_xy"] = {
                "continent_map_id": area["continent_map_id"],
                "kind": "point", "x": x, "y": y}
        elif kind == "box":
            corners = [
                _world_xy(area, geometry[xk], geometry[yk], geometry["units"])
                for xk in ("x_min", "x_max") for yk in ("y_min", "y_max")
            ]
            hint["world_xy"] = {
                "continent_map_id": area["continent_map_id"], "kind": "box",
                "x_min": min(value[0] for value in corners),
                "y_min": min(value[1] for value in corners),
                "x_max": max(value[0] for value in corners),
                "y_max": max(value[1] for value in corners)}
        else:
            raise ValueError("Converted hint geometry must be point or box")
        hint["conversion_status"] = "xy-candidate-only"
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    inventory = commands.add_parser("scan", help="read metadata/hashes only; no Lua or account data")
    inventory.add_argument("--addons", type=Path, default=Path(DEFAULT_ADDONS))
    inventory.add_argument("--out", type=Path, required=True)
    staging = commands.add_parser("stage", help="validate neutral JSON hints; no runtime activation")
    staging.add_argument("--input", type=Path, required=True)
    staging.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "scan":
            report = scan(args.addons)
            write_report(args.out, report, args.addons)
            print("Inventory:", len(report["addons"]), "folders; complete=" + str(report["complete"]))
            return 0 if report["complete"] else 1
        _no_link_parents(Path(os.path.abspath(args.input)))
        with args.input.open("rb") as source:
            raw = source.read(16 * 1024 * 1024 + 1)
        report = stage(loads_strict(raw.decode("utf-8-sig")))
        write_report(args.out, report, args.input)
        print("Quarantined hints:", len(report["hints"]), "; runtime remains disabled")
        return 0
    except (OSError, ValueError, UnicodeError) as error:
        print("Failed:", type(error).__name__, "-", str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
