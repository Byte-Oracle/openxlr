#!/usr/bin/env python3
"""Companion inventory, staging contract and local publication regressions."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("bridge_build", HERE / "build.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class ReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="openxlr-companion-")
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        self.source.mkdir()
        (self.source / "COPYING").write_text("license")
        self.output = self.root / "build"
        (self.output / "native").mkdir(parents=True)
        (self.output / "cargo/release").mkdir(parents=True)
        for name in bridge.FILES:
            (self.output / "native" / name).write_bytes(b"test binary")
        (self.output / "cargo/release/yabridgectl").write_bytes(b"controller")
        self.stage_root = self.root / "stage"
        self.destination = self.root / "installed"
        self.destination.mkdir()

    def tearDown(self):
        self.temporary.cleanup()

    def stage(self, manager="manual", prefix="/usr/local"):
        original = bridge.run
        def run(arguments, **kwargs):
            # Real binaries and post-processing are covered by the package job.
            # This fixture isolates the inventory and safe publication contract.
            if arguments[0] != "strip":
                return original(arguments, **kwargs)
        with patch.object(bridge, "run", side_effect=run):
            bridge.stage(self.source, self.output, self.stage_root, prefix, manager=manager)
        return self.stage_root / prefix.lstrip("/")

    def test_manager_is_required_in_cli_and_function(self):
        with self.assertRaises(TypeError):
            bridge.stage(self.source, self.output, self.stage_root)
        result = subprocess.run(["python3", str(HERE / "build.py"), "stage", "--source", str(self.source),
                                 "--output", str(self.output), "--destdir", str(self.stage_root)], capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertIn("requires --manager", result.stderr)
        self.assertFalse(self.stage_root.exists())

    def test_arch_receipt_uses_the_pin_and_correct_manager(self):
        payload = self.stage(manager="arch", prefix="/usr")
        receipt = json.loads((payload / "share/openxlr/installations/openxlr-yabridge.json").read_text())
        self.assertEqual("arch", receipt["manager"])
        self.assertEqual(bridge.PIN["commit"], receipt["commit"])
        self.assertTrue(any(entry["path"] == "/usr/bin/openxlr-yabridgectl" for entry in receipt["files"]))
        self.assertEqual(bridge.PIN, json.loads((HERE / "source.json").read_text()))

    def test_debian_source_recipe_stages_with_the_debian_manager(self):
        recipe = subprocess.run(["make", "--no-print-directory", "--just-print", "-f", str(HERE / "debian/rules"),
                                 "override_dh_auto_install"], check=True, capture_output=True, text=True)
        commands = [shlex.split(line) for line in recipe.stdout.splitlines()]
        command, = [args for args in commands if len(args) > 2 and args[2] == "stage"]
        for flag, value in (("--source", self.source), ("--output", self.output), ("--destdir", self.stage_root)):
            command[command.index(flag) + 1] = str(value)
        original = bridge.run
        def run(arguments, **kwargs):
            if arguments[0] != "strip":
                return original(arguments, **kwargs)
        with patch.object(sys, "argv", command[1:]), patch.object(bridge, "run", side_effect=run):
            bridge.main()
        receipt = json.loads((self.stage_root / "usr/share/openxlr/installations/openxlr-yabridge.json").read_text())
        self.assertEqual("debian", receipt["manager"])
        self.assertEqual(bridge.PIN["commit"], receipt["commit"])

    def test_explicit_local_install_stages_before_publication(self):
        with patch.object(bridge, "stage", wraps=bridge.stage) as stage, patch.object(bridge, "install_staged", return_value=Path("/usr/local")) as install:
            # Recursive staging is mocked only after checking the explicit route.
            original = stage._mock_wraps
            def route(source, output, destination, prefix="/usr", *, manager):
                if destination == Path("/"):
                    return original(source, output, destination, prefix, manager=manager)
                return destination / prefix.lstrip("/")
            stage.side_effect = route
            self.assertEqual(Path("/usr/local"), stage(self.source, self.output, Path("/"), "/usr/local", manager="manual"))
            install.assert_called_once()
        for manager, prefix in (("arch", "/usr/local"), ("manual", "/usr")):
            with self.assertRaises(RuntimeError):
                bridge.stage(self.source, self.output, Path("/"), prefix, manager=manager)

    def test_local_install_owns_only_companion_files_and_can_be_repeated(self):
        self.stage()
        unrelated = self.destination / "usr/local/unrelated"
        unrelated.parent.mkdir(parents=True)
        unrelated.write_text("keep")
        bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        receipt = json.loads((self.destination / "usr/local/share/openxlr/installations/openxlr-yabridge.json").read_text())
        self.assertEqual("manual", receipt["manager"])
        self.assertFalse(any(f["path"].endswith("unrelated") for f in receipt["files"]))
        self.assertEqual("keep", unrelated.read_text())
        for entry in receipt["files"]:
            if entry["kind"] == "file":
                self.assertEqual(entry["sha256"], hashlib.sha256((self.destination / entry["path"].lstrip("/")).read_bytes()).hexdigest())

    def test_unchanged_owned_files_can_be_upgraded_without_claiming_other_paths(self):
        payload = self.stage()
        bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        changed = payload / "lib/openxlr/yabridge/libyabridge-vst3.so"
        changed.write_bytes(b"new build")
        bridge.inventory(self.stage_root, "/usr/local", manager="manual")
        bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        self.assertEqual(b"new build", (self.destination / "usr/local/lib/openxlr/yabridge/libyabridge-vst3.so").read_bytes())

    def test_local_install_uses_public_modes_only_for_new_directories(self):
        self.stage()
        prefix = self.destination / "usr/local"
        prefix.mkdir(parents=True)
        prefix.chmod(0o750)
        old_umask = os.umask(0o077)
        try:
            bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        finally:
            os.umask(old_umask)
        self.assertEqual(0o750, prefix.stat().st_mode & 0o777)
        self.assertEqual(0o755, (prefix / "lib/openxlr/yabridge").stat().st_mode & 0o777)
        self.assertEqual(0o755, (prefix / "bin/openxlr-yabridgectl").stat().st_mode & 0o777)

    def test_foreign_or_modified_file_refuses_the_entire_install_before_writes(self):
        self.stage()
        conflicting = self.destination / "usr/local/bin/openxlr-yabridgectl"
        conflicting.parent.mkdir(parents=True)
        conflicting.write_text("foreign")
        with self.assertRaises(RuntimeError):
            bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        self.assertEqual("foreign", conflicting.read_text())
        self.assertFalse((self.destination / "usr/local/lib/openxlr/yabridge").exists())
        conflicting.unlink()
        bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        conflicting.write_text("modified")
        with self.assertRaises(RuntimeError):
            bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        self.assertEqual("modified", conflicting.read_text())

    def test_a_regular_parent_file_is_refused_before_any_live_writes(self):
        self.stage()
        blocked = self.destination / "usr/local/share"
        blocked.parent.mkdir(parents=True)
        blocked.write_text("foreign file")
        with self.assertRaises(RuntimeError):
            bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        self.assertEqual("foreign file", blocked.read_text())
        self.assertFalse((self.destination / "usr/local/bin/openxlr-yabridgectl").exists())
        self.assertFalse((self.destination / "usr/local/lib/openxlr/yabridge").exists())

    def test_redirected_parent_is_never_followed(self):
        self.stage()
        outside = self.root / "outside"
        outside.mkdir()
        (self.destination / "usr").symlink_to(outside, target_is_directory=True)
        with self.assertRaises(RuntimeError):
            bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        self.assertEqual([], list(outside.iterdir()))

    def test_rpm_retains_stripping_and_inventories_after_post_processing(self):
        for name in ("source.spec.in", "openxlr-yabridge.spec.in"):
            recipe = (HERE / name).read_text()
            self.assertNotIn("__strip /usr/bin/true", recipe)
            self.assertIn("%global __os_install_post %{__os_install_post}", recipe)
            self.assertIn("--manager rpm", recipe)


if __name__ == "__main__":
    unittest.main()
