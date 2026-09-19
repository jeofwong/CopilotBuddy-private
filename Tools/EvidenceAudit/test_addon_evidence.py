"""Offline addon-boundary tests, not gameplay or installed-addon acceptance."""
import copy
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import addon_evidence as ae


def sample_pack():
    return {
        "schema": "addon-hints-335-v1", "client_build": 12340,
        "source": {"provider": "test-fixture", "version": "1", "sha256": "a" * 64,
                   "core": "unknown", "revision": "unknown", "licence": "unresolved"},
        "hints": [{"quest_id": 867, "objective_index": None, "entity_type": "creature", "entity_id": 77,
                   "map_namespace": "world-map-area-335", "map_id": 42, "floor": None,
                   "geometry": {"kind": "point", "units": "zone-percent", "x": 10, "y": 20}}],
    }


class InventoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "Interface" / "AddOns"
        self.root.mkdir(parents=True)

    def put(self, path, content):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content if isinstance(content, bytes) else content.encode())
        return target

    def test_legacy_toc_identity_not_live_support(self):
        raw = b"## Interface: 30300\n## Title: Carbonite 3.34\n## Version: 3.34\n"
        self.put("Carbonite/Carbonite.toc", raw)
        result = ae.scan(self.root)
        addon = result["addons"][0]
        self.assertTrue(result["complete"])
        self.assertEqual(addon["tocs"][0]["metadata"]["interface"], ["30300"])
        self.assertEqual(addon["files"][0]["sha256"], hashlib.sha256(raw).hexdigest())
        self.assertEqual(addon["candidates"], ["quest-map"])
        self.assertFalse(addon["compatibility_verified"])
        self.assertIsNone(addon["enabled_in_game"])

    def test_does_not_execute_lua(self):
        self.put("Test/Test.toc", "## Interface: 30300\nrun.lua\n")
        self.put("Test/run.lua", "os.execute('untrusted command')")
        with patch("subprocess.run", side_effect=AssertionError("execution forbidden")):
            self.assertEqual(len(ae.scan(self.root)["addons"][0]["files"]), 2)

    def test_does_not_include_lua_payload_or_absolute_root(self):
        self.put("Test/Test.lua", "secret_fixture_payload=1")
        encoded = json.dumps(ae.scan(self.root))
        self.assertNotIn("secret_fixture_payload", encoded)
        self.assertNotIn(str(self.root), encoded)

    def test_missing_root_is_not_empty_inventory(self):
        with self.assertRaises(ValueError): ae.scan(self.root / "missing")

    def test_file_is_not_addon_root(self):
        target = self.put("file", "test")
        with self.assertRaises(ValueError): ae.scan(target)

    def test_rejects_wrong_root_name(self):
        with self.assertRaises(ValueError): ae.scan(self.root.parent)

    def test_empty_root_is_known_empty(self):
        result = ae.scan(self.root)
        self.assertEqual(result["addons"], [])
        self.assertTrue(result["complete"])

    def test_no_wtf_read_or_toc_path_following(self):
        secret = self.root.parent.parent / "WTF" / "Account" / "Private.lua"
        secret.parent.mkdir(parents=True)
        secret.write_text("secret marker")
        self.put("Test/Test.toc", "## SavedVariables: TestData\n../../WTF/Account/Private.lua\n")
        result = ae.scan(self.root)
        self.assertNotIn("Private.lua", json.dumps(result))
        self.assertNotIn("secret marker", json.dumps(result))
        self.assertEqual(result["addons"][0]["tocs"][0]["metadata"]["savedvariables"], ["TestData"])

    def test_unknown_toc_and_multiple_flavours_remain_explicit(self):
        self.put("Mixed/Mixed.toc", "## Title: Mixed\n")
        self.put("Mixed/Mixed_Mainline.toc", "## Interface: 120100\n")
        addon = ae.scan(self.root)["addons"][0]
        self.assertEqual(len(addon["tocs"]), 2)
        self.assertFalse(addon["compatibility_verified"])

    def test_duplicate_metadata_is_preserved(self):
        self.put("Test/Test.toc", "## Interface: 30300\n## Interface: 30403\n")
        self.assertEqual(ae.scan(self.root)["addons"][0]["tocs"][0]["metadata"]["interface"], ["30300", "30403"])

    def test_utf8_bom_and_crlf(self):
        self.put("Test/Test.TOC", b"\xef\xbb\xbf## Version: 2\r\n")
        self.assertEqual(ae.scan(self.root)["addons"][0]["tocs"][0]["metadata"]["version"], ["2"])

    def test_windows_fresh_text_identity_probe(self):
        if os.name != "nt":
            self.skipTest("Windows file-identity probe")
        original_identity = ae._identity
        for attempt in range(128):
            target = self.put("Probe/Probe.toc", b"## Interface: 30300\r\n## Version: 2\r\n")
            observed = []
            def capture(info):
                value = original_identity(info)
                observed.append(value)
                return value
            with patch.object(ae, "_identity", side_effect=capture):
                result = ae.scan(self.root)
            addon = next((item for item in result["addons"] if item["folder"] == "Probe"), None)
            if addon is None or len(addon["tocs"]) != 1:
                try:
                    current = original_identity(target.lstat())
                except OSError as error:
                    current = (type(error).__name__, str(error))
                self.fail(
                    f"fresh text snapshot dropped at attempt {attempt}; "
                    f"identity_calls={observed!r}; current={current!r}; issues={result['issues']!r}")
            target.unlink()
            target.parent.rmdir()

    def test_oversized_file_marks_partial(self):
        self.put("Test/Test.lua", "x" * 20)
        result = ae.scan(self.root, max_bytes=10)
        self.assertFalse(result["complete"])
        self.assertTrue(result["issues"])

    def test_budget_exhaustion_marks_partial(self):
        self.put("Test/a.lua", "123456")
        self.put("Test/b.lua", "123456")
        self.assertFalse(ae.scan(self.root, max_bytes=10)["complete"])

    def test_entry_bound_marks_partial(self):
        for i in range(5): self.put(f"Test/{i}.lua", "x")
        self.assertFalse(ae.scan(self.root, max_entries=2)["complete"])

    def test_symlink_never_followed(self):
        other = Path(self.temp.name) / "outside.lua"
        other.write_text("secret")
        self.put("Test/Test.toc", "## Title: Test")
        link = self.root / "Test" / "outside.lua"
        try: link.symlink_to(other)
        except OSError: self.skipTest("OS does not permit symlink creation")
        result = ae.scan(self.root)
        self.assertFalse(result["complete"])
        self.assertFalse(any(f["path"].endswith("outside.lua") for a in result["addons"] for f in a["files"]))

    def test_no_input_modification(self):
        path = self.put("Test/Test.lua", "x=1")
        before = (path.read_bytes(), path.stat().st_mtime_ns)
        ae.scan(self.root)
        self.assertEqual(before, (path.read_bytes(), path.stat().st_mtime_ns))

    def test_excludes_binary_assets(self):
        self.put("Test/image.tga", b"texture")
        self.put("Test/Test.lua", "x=1")
        self.assertEqual(len(ae.scan(self.root)["addons"][0]["files"]), 1)

    def test_output_must_not_be_inside_addons(self):
        with self.assertRaises(ValueError): ae.write_report(self.root / "report.json", {}, self.root)

    def test_existing_report_is_not_overwritten(self):
        out = Path(self.temp.name) / "report.json"
        out.write_text("retain")
        with self.assertRaises(FileExistsError): ae.write_report(out, {}, self.root)
        self.assertEqual(out.read_text(), "retain")

    def test_output_json_and_sorting(self):
        self.put("Z/Z.toc", "## Title: Z")
        self.put("A/A.toc", "## Title: A")
        result = ae.scan(self.root)
        out = Path(self.temp.name) / "report.json"
        ae.write_report(out, result, self.root)
        self.assertEqual(json.loads(out.read_text()), result)
        self.assertEqual([a["folder"] for a in result["addons"]], ["A", "Z"])


class HintTests(unittest.TestCase):
    def test_valid_hint_never_becomes_executable(self):
        result = ae.stage(sample_pack())
        self.assertEqual(result["status"], "quarantined")
        self.assertFalse(result["runtime_enabled"])
        self.assertEqual(result["hints"][0]["authority"], "search-hint-only")
        self.assertIsNone(result["hints"][0]["world_xyz"])
        self.assertIsNone(result["hints"][0]["objective_index"])

    def test_unknown_provenance_stays_unknown(self):
        result = ae.stage(sample_pack())
        self.assertEqual(result["source"]["core"], "unknown")
        self.assertFalse(result["source_verified"])

    def test_declared_core_does_not_certify_compatibility(self):
        for core in ("trinitycore-3.3.5", "azerothcore-wotlk"):
            p = sample_pack(); p["source"]["core"] = core
            self.assertFalse(ae.stage(p)["source_verified"])

    def test_rejects_wrong_client_or_bool(self):
        for value in (30403, 335, True, "12340"):
            p = sample_pack(); p["client_build"] = value
            with self.assertRaises(ValueError): ae.stage(p)

    def test_rejects_missing_source_fields(self):
        for key in sample_pack()["source"]:
            p = sample_pack(); del p["source"][key]
            with self.assertRaises(ValueError): ae.stage(p)

    def test_rejects_unknown_fields_instead_of_forwarding_actions(self):
        for where in ("root", "source", "hint", "geometry"):
            p = sample_pack()
            target = p if where == "root" else p["source"] if where == "source" else p["hints"][0] if where == "hint" else p["hints"][0]["geometry"]
            target["execute_lua"] = "RunMacroText('use item')"
            with self.assertRaises(ValueError): ae.stage(p)

    def test_rejects_unknown_coordinate_space(self):
        p = sample_pack(); p["hints"][0]["geometry"]["units"] = "world"
        with self.assertRaises(ValueError): ae.stage(p)

    def test_rejects_nonfinite_bool_and_out_of_range_coordinates(self):
        for value in (float("nan"), float("inf"), -1, 101, True, "12"):
            p = sample_pack(); p["hints"][0]["geometry"]["x"] = value
            with self.assertRaises(ValueError): ae.stage(p)

    def test_fraction_coordinates_require_fraction_scale(self):
        p = sample_pack(); g = p["hints"][0]["geometry"]; g.update(units="zone-fraction", x=.5, y=1)
        self.assertEqual(ae.stage(p)["hints"][0]["geometry"]["x"], .5)
        g["y"] = 2
        with self.assertRaises(ValueError): ae.stage(p)

    def test_explicit_map_namespace_required(self):
        p = sample_pack(); p["hints"][0]["map_namespace"] = "world-map"
        with self.assertRaises(ValueError): ae.stage(p)

    def test_distinct_entity_namespaces_do_not_merge(self):
        p = sample_pack(); other = copy.deepcopy(p["hints"][0]); other["entity_type"] = "gameobject"; p["hints"].append(other)
        self.assertEqual(len(ae.stage(p)["hints"]), 2)

    def test_unknown_entity_does_not_get_invented_id(self):
        p = sample_pack(); p["hints"][0].update(entity_type="unknown", entity_id=None)
        self.assertIsNone(ae.stage(p)["hints"][0]["entity_id"])

    def test_exact_duplicates_are_removed_without_changing_input(self):
        p = sample_pack(); p["hints"].append(copy.deepcopy(p["hints"][0])); before = copy.deepcopy(p)
        result = ae.stage(p)
        self.assertEqual(result["duplicates_removed"], 1)
        self.assertEqual(p, before)

    def test_box_stays_box_not_synthetic_center(self):
        p = sample_pack(); p["hints"][0]["geometry"] = {"kind":"box", "units":"zone-percent", "x_min":1, "y_min":2, "x_max":10, "y_max":20}
        h = ae.stage(p)["hints"][0]
        self.assertEqual(h["geometry"]["kind"], "box")
        self.assertNotIn("x", h["geometry"])

    def test_rejects_inverted_box(self):
        p = sample_pack(); p["hints"][0]["geometry"] = {"kind":"box", "units":"zone-percent", "x_min":10, "y_min":2, "x_max":1, "y_max":20}
        with self.assertRaises(ValueError): ae.stage(p)

    def test_rejects_invalid_identity_and_floor(self):
        for key, value in (("quest_id",0), ("entity_id",True), ("map_id",0), ("objective_index",-1), ("floor",-1)):
            p = sample_pack(); p["hints"][0][key] = value
            with self.assertRaises(ValueError): ae.stage(p)

    def test_empty_pack_is_explicit_and_non_executable(self):
        p = sample_pack(); p["hints"] = []
        self.assertEqual(ae.stage(p)["hints"], [])

    def test_rejects_oversized_hint_set(self):
        p = sample_pack(); p["hints"] *= 3
        with self.assertRaises(ValueError): ae.stage(p, max_hints=2)

    def test_json_duplicate_keys_rejected(self):
        with self.assertRaises(ValueError): ae.loads_strict('{"x":1,"x":2}')

    def test_json_nonfinite_literals_rejected(self):
        for text in ('{"x":NaN}', '{"x":Infinity}'):
            with self.assertRaises(ValueError): ae.loads_strict(text)

    def test_json_bounded_input_and_normal_control(self):
        with self.assertRaises(ValueError): ae.loads_strict(' ' * 20, max_bytes=10)
        self.assertEqual(ae.loads_strict('{"x":1}'), {"x":1})


def sample_world_map_bounds():
    return {
        "schema": "world-map-area-bounds-335-v1",
        "client_build": 12340,
        "source": {
            "provider": "controlled-worldmaparea-fixture",
            "revision": "fixture-rev",
            "sha256": "b" * 64,
        },
        "areas": [{
            "world_map_area_id": 42,
            "continent_map_id": 1,
            "loc_left": 100.0,
            "loc_right": 300.0,
            "loc_top": 1000.0,
            "loc_bottom": 600.0,
        }],
    }


class WorldMapConversionTests(unittest.TestCase):
    def convert(self, staged, bounds):
        self.assertTrue(
            hasattr(ae, "convert_world_map_area_hints"),
            "WorldMapArea candidate conversion contract is missing")
        return ae.convert_world_map_area_hints(staged, bounds)

    def staged(self):
        return ae.stage(sample_pack())

    def test_point_percent_uses_335_client_axis_swap(self):
        result = self.convert(self.staged(), sample_world_map_bounds())
        hint = result["hints"][0]
        self.assertEqual(hint["world_xy"], {
            "continent_map_id": 1, "kind": "point", "x": 920.0, "y": 120.0})
        self.assertIsNone(hint["world_xyz"])
        self.assertEqual(hint["conversion_status"], "xy-candidate-only")
        self.assertFalse(result["runtime_enabled"])

    def test_fraction_scale_matches_percent_scale(self):
        staged = self.staged()
        staged["hints"][0]["geometry"].update(units="zone-fraction", x=.1, y=.2)
        result = self.convert(staged, sample_world_map_bounds())
        self.assertEqual(result["hints"][0]["world_xy"]["x"], 920.0)
        self.assertEqual(result["hints"][0]["world_xy"]["y"], 120.0)

    def test_box_remains_box_and_transforms_all_corners(self):
        staged = self.staged()
        staged["hints"][0]["geometry"] = {
            "kind": "box", "units": "zone-percent",
            "x_min": 10, "y_min": 30, "x_max": 20, "y_max": 40}
        hint = self.convert(staged, sample_world_map_bounds())["hints"][0]
        self.assertEqual(hint["world_xy"], {
            "continent_map_id": 1, "kind": "box",
            "x_min": 840.0, "y_min": 120.0,
            "x_max": 880.0, "y_max": 140.0})
        self.assertNotIn("x", hint["world_xy"])

    def test_carbonite_zone_stays_unmapped(self):
        staged = self.staged()
        staged["hints"][0]["map_namespace"] = "carbonite-zone"
        hint = self.convert(staged, sample_world_map_bounds())["hints"][0]
        self.assertIsNone(hint["world_xy"])
        self.assertEqual(hint["conversion_status"], "source-namespace-unmapped")
        self.assertIsNone(hint["world_xyz"])

    def test_questie_area_stays_unmapped(self):
        staged = self.staged()
        staged["hints"][0]["map_namespace"] = "questie-area"
        hint = self.convert(staged, sample_world_map_bounds())["hints"][0]
        self.assertIsNone(hint["world_xy"])
        self.assertEqual(hint["conversion_status"], "source-namespace-unmapped")

    def test_floor_specific_hint_is_not_flattened(self):
        staged = self.staged()
        staged["hints"][0]["floor"] = 2
        hint = self.convert(staged, sample_world_map_bounds())["hints"][0]
        self.assertIsNone(hint["world_xy"])
        self.assertEqual(hint["conversion_status"], "floor-unresolved")

    def test_missing_area_never_invents_world_zero(self):
        staged = self.staged()
        staged["hints"][0]["map_id"] = 99
        hint = self.convert(staged, sample_world_map_bounds())["hints"][0]
        self.assertIsNone(hint["world_xy"])
        self.assertEqual(hint["conversion_status"], "world-map-area-unresolved")
        self.assertIsNone(hint["world_xyz"])

    def test_degenerate_bounds_are_rejected(self):
        bounds = sample_world_map_bounds()
        bounds["areas"][0]["loc_bottom"] = bounds["areas"][0]["loc_top"]
        with self.assertRaises(ValueError):
            self.convert(self.staged(), bounds)

    def test_wrong_client_build_is_rejected(self):
        bounds = sample_world_map_bounds()
        bounds["client_build"] = 30403
        with self.assertRaises(ValueError):
            self.convert(self.staged(), bounds)

    def test_duplicate_world_map_area_ids_are_rejected(self):
        bounds = sample_world_map_bounds()
        bounds["areas"].append(copy.deepcopy(bounds["areas"][0]))
        with self.assertRaises(ValueError):
            self.convert(self.staged(), bounds)

    def test_unknown_bounds_fields_are_rejected(self):
        bounds = sample_world_map_bounds()
        bounds["areas"][0]["z"] = 0
        with self.assertRaises(ValueError):
            self.convert(self.staged(), bounds)

    def test_conversion_does_not_mutate_staged_input(self):
        staged = self.staged()
        before = copy.deepcopy(staged)
        self.convert(staged, sample_world_map_bounds())
        self.assertEqual(staged, before)

    def test_conversion_never_promotes_search_hint_authority(self):
        result = self.convert(self.staged(), sample_world_map_bounds())
        hint = result["hints"][0]
        self.assertEqual(hint["authority"], "search-hint-only")
        self.assertFalse(result["runtime_enabled"])
        self.assertFalse(result["terrain_verified"])
        self.assertFalse(result["path_verified"])
        self.assertEqual(result["conversion_authority"], "coordinate-candidate-only")

    def test_bounds_source_is_retained_without_becoming_core_provenance(self):
        result = self.convert(self.staged(), sample_world_map_bounds())
        self.assertEqual(result["world_map_area_bounds"]["provider"],
                         "controlled-worldmaparea-fixture")
        self.assertEqual(result["world_map_area_bounds"]["revision"], "fixture-rev")
        self.assertEqual(result["world_map_area_bounds"]["sha256"], "b" * 64)
        self.assertFalse(result["source_verified"])


if __name__ == "__main__": unittest.main()
