"""Tests for tools/menucheck.py, the command that checks any menu file.
Run: python3 -m unittest discover -s tools -p "test_*.py"   (needs `pip install jsonschema`)"""
import copy, json, os, subprocess, sys, tempfile, unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import menucheck  # noqa: E402

EXAMPLE = os.path.join(ROOT, "examples", "example.menu.json")


def good():
    with open(EXAMPLE) as f:
        return json.load(f)


class CheckDoc(unittest.TestCase):
    def test_every_shipped_example_passes(self):
        for name in sorted(os.listdir(os.path.join(ROOT, "examples"))):
            if name.endswith(".menu.json"):
                self.assertEqual(menucheck.check_menu_file(os.path.join(ROOT, "examples", name)), [], name)

    def test_schema_errors_say_where(self):
        d = good(); d["menus"]["main"]["entries"][0]["label"] = "<b>x</b>"
        errs = menucheck.check_menu_doc(d)
        self.assertTrue(errs and errs[0].startswith("$.menus.main.entries[0]"), errs)

    def test_semantic_errors_are_reported(self):
        d = good(); d["menus"]["features"]["entries"][0]["extra"] = "nope"
        self.assertIn("features[0] unknown extra nope", menucheck.check_menu_doc(d))
        d = good(); d["menus"]["orphan"] = {"title": "O", "entries": [{"action": "back", "label": "Back"}]}
        self.assertTrue(any("unreachable" in e for e in menucheck.check_menu_doc(d)))

    def test_semantic_checks_only_run_after_the_schema_passes(self):
        d = good(); d["menuId"] = "not-a-uuid"; d["menus"]["features"]["entries"][0]["extra"] = "nope"
        errs = menucheck.check_menu_doc(d)
        self.assertTrue(errs and not any("unknown extra" in e for e in errs))

    def test_non_objects_are_rejected_not_crashed(self):
        for bad in (None, [], "x", 42, True):
            self.assertEqual(menucheck.check_menu_doc(bad), ["a menu must be a JSON object"])

    def test_errors_are_capped(self):
        d = good(); d["menus"]["main"]["entries"] = [{"action": "nope", "label": ""} for _ in range(500)]
        self.assertLessEqual(len(menucheck.check_menu_doc(d)), menucheck.MAX_ERRORS)

    def test_unknown_properties_are_rejected(self):
        d = good(); d["file"] = "/mnt/x.mkv"
        self.assertTrue(menucheck.check_menu_doc(d))


class CheckFile(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="menucheck-")

    def write(self, name, data, mode="w", **kw):
        p = os.path.join(self.dir, name)
        with open(p, mode, **kw) as f:
            f.write(data)
        return p

    def test_missing_file(self):
        errs = menucheck.check_menu_file(os.path.join(self.dir, "nope.menu.json"))
        self.assertTrue(errs and errs[0].startswith("cannot read file"))

    def test_not_json(self):
        for text in ("", "{", "{ nope }", "[1,2"):
            errs = menucheck.check_menu_file(self.write("a.menu.json", text))
            self.assertTrue(errs and errs[0].startswith("not valid JSON"), (text, errs))

    def test_not_utf8(self):
        p = self.write("a.menu.json", b"\xff\xfe{\"a\":1}\x80", mode="wb")
        self.assertEqual(menucheck.check_menu_file(p), ["file is not valid UTF-8"])

    def test_byte_order_mark_is_tolerated(self):
        p = self.write("a.menu.json", "﻿" + json.dumps(good()), encoding="utf-8")
        self.assertEqual(menucheck.check_menu_file(p), [])

    def test_a_directory_is_not_a_menu(self):
        self.assertTrue(menucheck.check_menu_file(self.dir)[0].startswith("cannot read file"))


class Command(unittest.TestCase):
    def run_cli(self, *args):
        p = subprocess.run([sys.executable, os.path.join(HERE, "menucheck.py"), *args], capture_output=True, text=True)
        return p.returncode, p.stdout, p.stderr

    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="menucheck-")
        self.bad = os.path.join(self.dir, "bad.menu.json")
        d = good(); d["menus"]["main"]["entries"][0]["label"] = "<b>"
        with open(self.bad, "w") as f:
            json.dump(d, f)

    def test_passing_files_exit_zero(self):
        code, out, _ = self.run_cli(EXAMPLE)
        self.assertEqual(code, 0)
        self.assertIn("PASS " + EXAMPLE, out)

    def test_any_failure_exits_one_and_still_checks_the_rest(self):
        code, out, _ = self.run_cli(self.bad, EXAMPLE)
        self.assertEqual(code, 1)
        self.assertIn("FAIL " + self.bad, out)
        self.assertIn("PASS " + EXAMPLE, out)
        self.assertIn("  - $.menus.main.entries[0]", out)

    def test_usage_errors_exit_two(self):
        for args in ((), ("--bogus", EXAMPLE), ("--json",)):
            code, _, err = self.run_cli(*args)
            self.assertEqual(code, 2, args)
            self.assertTrue(err)

    def test_json_output_is_machine_readable(self):
        code, out, _ = self.run_cli("--json", self.bad, EXAMPLE)
        self.assertEqual(code, 1)
        data = json.loads(out)
        self.assertEqual([r["ok"] for r in data], [False, True])
        self.assertEqual(data[0]["file"], self.bad)
        self.assertTrue(data[0]["errors"])
        self.assertEqual(data[1]["errors"], [])

    def test_unreadable_file_is_a_failure_not_a_crash(self):
        code, out, _ = self.run_cli(os.path.join(self.dir, "missing.menu.json"))
        self.assertEqual(code, 1)
        self.assertIn("cannot read file", out)




class DuplicateKeys(unittest.TestCase):
    """A repeated key is an error in every layer (the editor and the catalogue need exactly one answer)."""

    def write(self, text):
        import tempfile
        d = tempfile.mkdtemp(prefix="dup-")
        self.addCleanup(__import__("shutil").rmtree, d, ignore_errors=True)
        p = os.path.join(d, "m.menu.json")
        with open(p, "w", encoding="utf-8") as f:
            f.write(text)
        return p

    def test_a_clean_menu_passes_and_a_repeated_key_fails_everywhere(self):
        import menucheck
        with open(os.path.join(menucheck.ROOT, "examples", "example.menu.json"), encoding="utf-8") as f:
            good = f.read()
        self.assertEqual([], menucheck.check_menu_file(self.write(good)))
        for original, doubled in (
            ('"revision": 2,', '"revision": 2, "revision": 3,'),
            ('"schemaVersion": 1,', '"schemaVersion": 1, "schemaVersion": 1,'),
            ('"match": {', '"match": { "itemType": "Movie",'),
        ):
            if original not in good:
                continue
            problems = menucheck.check_menu_file(self.write(good.replace(original, doubled, 1)))
            self.assertTrue(problems, original)
            self.assertTrue(any("more than once" in p for p in problems), problems)

    def test_nested_and_map_duplicates(self):
        import menucheck
        for text in (
            '{"a": {"k": 1, "k": 2}}',
            '{"menus": {"main": {}, "main": {}}}',
            '{"list": [{"x": 1, "x": 2}]}',
        ):
            problems = menucheck.check_menu_file(self.write(text))
            self.assertTrue(any("more than once" in p for p in problems), text)

    def test_load_rejects_them_too(self):
        import menucheck
        with self.assertRaises(ValueError):
            menucheck.load(self.write('{"a": 1, "a": 2}'))


if __name__ == "__main__":
    unittest.main()
