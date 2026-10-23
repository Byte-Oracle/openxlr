#!/usr/bin/env python3
"""The Chrome Web Store package of the browser companion, built into a temporary directory."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile

REPO = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("browser_extension", REPO / "tools/browser-extension.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class BrowserExtensionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="openxlr-browser-test-")
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def built(self, **kwargs):
        path = package.build(self.root / "out", **kwargs)
        with zipfile.ZipFile(path) as archive:
            return path, {name: archive.read(name) for name in archive.namelist()}

    def test_the_package_is_the_extension_without_its_key(self):
        path, files = self.built()
        source = json.loads((REPO / "browser/chromium/manifest.json").read_text())
        manifest = json.loads(files["manifest.json"])
        self.assertEqual(f"openxlr-browser-{source['version']}.zip", path.name)
        self.assertNotIn("key", manifest)
        del source["key"]
        self.assertEqual(source, manifest)
        self.assertEqual(sorted(package.source_files()), sorted(files))
        self.assertNotIn("key.pem", files)
        for name in ("worker.mjs", "routing.mjs", "popup.html", "offscreen.html", "devices.html", "icons/icon-48.png"):
            self.assertEqual((REPO / "browser/chromium" / name).read_bytes(), files[name])
        self.assertEqual([], package.validate(path))

    def test_two_builds_are_identical(self):
        first = package.build(self.root / "a").read_bytes()
        self.assertEqual(first, package.build(self.root / "b").read_bytes())

    def test_the_manifest_key_pins_the_id_the_native_host_allows(self):
        self.assertEqual(package.EXTENSION_ID, package.extension_id(package.manifest_key()))
        host = json.loads((REPO / "browser/native-host.json").read_text())
        self.assertEqual([f"chrome-extension://{package.EXTENSION_ID}/"], host["allowed_origins"])

    def test_a_matching_private_key_is_added_at_the_root(self):
        pem = self.root / "key.pem"
        pem.write_text("stand-in private key\n")
        with mock.patch.object(package, "public_der", return_value=package.manifest_key()):
            path, files = self.built(key_pem=pem)
        self.assertEqual(pem.read_bytes(), files["key.pem"])
        self.assertEqual([], package.validate(path, key_pem=True))
        self.assertIn("key.pem is present", package.validate(path))

    def test_another_private_key_is_refused(self):
        pem = self.root / "other.pem"
        pem.write_text("stand-in private key\n")
        with mock.patch.object(package, "public_der", return_value=b"another key"), \
                self.assertRaises(SystemExit) as refused:
            package.build(self.root / "out", key_pem=pem)
        self.assertIn(package.EXTENSION_ID, str(refused.exception))
        self.assertFalse((self.root / "out").exists())

    def test_tests_and_hidden_files_stay_out(self):
        source = self.root / "source"
        (source / "tests").mkdir(parents=True)
        for name in ("manifest.json", "worker.mjs", "tests/a.mjs", "b.test.mjs", ".hidden"):
            (source / name).write_text("{}")
        self.assertEqual(["manifest.json", "worker.mjs"], package.source_files(source))

    def test_validation_names_a_missing_reference_a_key_and_a_wrong_icon(self):
        broken = self.root / "broken.zip"
        manifest = json.loads((REPO / "browser/chromium/manifest.json").read_text())
        with zipfile.ZipFile(broken, "w") as archive:
            archive.writestr(zipfile.ZipInfo("icons/icon-128.png", package.STAMP), (REPO / "browser/chromium/icons/icon-16.png").read_bytes())
            archive.writestr(zipfile.ZipInfo("manifest.json", package.STAMP), json.dumps(manifest))
            archive.writestr(zipfile.ZipInfo("popup.html", package.STAMP), '<script type="module" src="popup.mjs"></script>')
        problems = package.validate(broken)
        self.assertIn("the manifest still carries a key, which the Store refuses", problems)
        self.assertIn("icons/icon-128.png is not a 128 px PNG", problems)
        self.assertIn("popup.mjs is referenced but not in the zip", problems)
        self.assertIn("worker.mjs is referenced but not in the zip", problems)
        self.assertIn("icons 16 names icons/icon-16.png, which is not in the zip", problems)

    def test_the_references_cover_imports_pages_and_offscreen_documents(self):
        worker = (REPO / "browser/chromium/worker.mjs").read_text()
        popup = (REPO / "browser/chromium/popup.mjs").read_text()
        self.assertTrue({"routing.mjs", "offscreen.html"} <= package.references("worker.mjs", worker))
        self.assertTrue({"routing.mjs", "devices.html"} <= package.references("popup.mjs", popup))
        self.assertEqual({"style.css", "popup.mjs"},
                         package.references("popup.html", (REPO / "browser/chromium/popup.html").read_text()))

    def test_check_passes_on_the_tree(self):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(0, package.check())
        self.assertIn("ok", output.getvalue())


if __name__ == "__main__":
    unittest.main()
