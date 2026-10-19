"""Tests for the release consistency checker. Run: python3 -m unittest discover -s tools -p 'test_*.py'"""
import os
import shutil
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import check_release as cr  # noqa: E402

GUID = "913b9d44-cafe-4fe7-ae0b-f1362dc12cc7"
BUILD = f'''name: "Disc Menus"
guid: "{GUID}"
version: "0.2.1.0"
changelog: >
  - 0.2.1.0: Fixed a thing and
    added another thing.
  - 0.2.0.0: Older entry.
'''
PLUGIN = f'public override Guid Id => Guid.Parse("{GUID}");'
CSPROJ = "<AssemblyVersion>0.2.1.0</AssemblyVersion><FileVersion>0.2.1.0</FileVersion>"


class CheckRelease(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="rel-")
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.proj = os.path.join(self.tmp, cr.PROJECT)
        os.makedirs(self.proj)

    def put(self, build=BUILD, plugin=PLUGIN, csproj=CSPROJ):
        for name, text in (("build.yaml", build), ("Plugin.cs", plugin), (f"{cr.PROJECT}.csproj", csproj)):
            with open(os.path.join(self.proj, name), "w", encoding="utf-8") as f:
                f.write(text)

    def test_consistent(self):
        self.put()
        self.assertEqual([], cr.check(self.tmp))
        self.assertEqual([], cr.check(self.tmp, "v0.2.1"))

    def test_the_real_repository_is_consistent(self):
        self.assertEqual([], cr.check(cr.ROOT))

    def test_guid_mismatch(self):
        self.put(plugin=PLUGIN.replace("913b9d44", "00000000"))
        self.assertTrue(any("differ" in p for p in cr.check(self.tmp)))

    def test_missing_plugin_id(self):
        self.put(plugin="nothing")
        self.assertTrue(any("Plugin.cs" in p for p in cr.check(self.tmp)))

    def test_assembly_version_mismatch(self):
        self.put(csproj=CSPROJ.replace("<AssemblyVersion>0.2.1.0", "<AssemblyVersion>0.1.0.0"))
        self.assertTrue(any("AssemblyVersion" in p for p in cr.check(self.tmp)))

    def test_bad_version_shape(self):
        self.put(build=BUILD.replace('"0.2.1.0"', '"0.2.1"'))
        self.assertTrue(any("four numbers" in p for p in cr.check(self.tmp)))

    def test_changelog_must_mention_the_version(self):
        self.put(build=BUILD.replace("- 0.2.1.0:", "- 0.9.9.9:"))
        self.assertTrue(any("changelog" in p for p in cr.check(self.tmp)))

    def test_tag_must_name_the_version(self):
        self.put()
        for tag in ("v0.2.0", "v0.2.1.0", "0.2.1", "vx", ""):
            self.assertTrue(any("tag" in p for p in cr.check(self.tmp, tag)), tag)

    def test_tag_to_version(self):
        self.assertEqual("1.2.3.0", cr.tag_to_version("v1.2.3"))
        self.assertIsNone(cr.tag_to_version("v1.2"))
        self.assertIsNone(cr.tag_to_version("release-1.2.3"))

    def test_release_notes_are_the_versions_entry_only(self):
        notes = cr.release_notes(BUILD, "0.2.1.0")
        self.assertIn("Fixed a thing and", notes)
        self.assertIn("added another thing.", notes)
        self.assertNotIn("Older entry", notes)
        self.assertIsNone(cr.release_notes(BUILD, "9.9.9.9"))


if __name__ == "__main__":
    unittest.main()
