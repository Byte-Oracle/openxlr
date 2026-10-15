#!/usr/bin/env python3
"""Package inventory and headless doctor checks, without touching an installation."""
import hashlib
import importlib.util
import json
import os
import shutil
from pathlib import Path
import subprocess
import tempfile
import unittest

REPO = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("install_receipt", REPO / "packaging/install_receipt.py")
receipt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(receipt)
COMMIT = "0123456789abcdef0123456789abcdef01234567"


class InstallationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="openxlr-inventory-")
        self.root = Path(self.temp.name)
        self.payload = self.root / "usr"
        self.payload.mkdir()
        self.program = self.payload / "bin/openxlr"
        self.program.parent.mkdir()
        self.program.write_text("package payload\n")
        self.program.chmod(0o755)

    def tearDown(self):
        self.temp.cleanup()

    def generate(self, **kwargs):
        arguments = dict(root=self.root, prefix="/usr", package="openxlr", manager="debian", version="1.2.3", commit=COMMIT)
        arguments.update(kwargs)
        return receipt.write_receipt(**arguments)

    def test_hashes_links_and_modes_are_deterministic_and_include_the_receipt(self):
        link = self.payload / "bin/openxlr-link"
        link.symlink_to("openxlr")
        manifest = self.generate()
        encoded = manifest.read_bytes()
        values = json.loads(encoded)
        files = {f["path"]: f for f in values["files"]}
        self.assertEqual(hashlib.sha256(self.program.read_bytes()).hexdigest(), files["/usr/bin/openxlr"]["sha256"])
        self.assertEqual(0o755, files["/usr/bin/openxlr"]["mode"])
        self.assertEqual("openxlr", files["/usr/bin/openxlr-link"]["linkTarget"])
        self.assertEqual("receipt", files["/usr/share/openxlr/installations/openxlr.json"]["kind"])
        self.generate()
        self.assertEqual(encoded, manifest.read_bytes())
        self.assertNotIn(str(self.root), manifest.read_text())

    def test_links_to_directories_are_not_traversed_and_package_metadata_is_not_owned(self):
        external = self.root / "external"
        external.mkdir()
        (external / "foreign").write_text("keep")
        (self.payload / "linked").symlink_to(external, target_is_directory=True)
        (self.root / "DEBIAN").mkdir()
        (self.root / "DEBIAN/control").write_text("metadata")
        files = json.loads(self.generate().read_text())["files"]
        self.assertTrue(any(f["path"] == "/usr/linked" and f["kind"] == "link" for f in files))
        self.assertFalse(any("foreign" in f["path"] or "DEBIAN" in f["path"] for f in files))

    def test_nix_inventory_anticipates_store_permission_normalization(self):
        data = self.payload / "private-data"
        data.write_text("fixture")
        data.chmod(0o600)
        self.program.chmod(0o700)
        values = json.loads(self.generate(manager="nix").read_text())
        files = {f["path"]: f for f in values["files"]}
        self.assertEqual(0o555, files["/usr/bin/openxlr"]["mode"])
        self.assertEqual(0o444, files["/usr/private-data"]["mode"])
        self.assertEqual(0o444, files["/usr/share/openxlr/installations/openxlr.json"]["mode"])

    def test_documentation_is_optional_but_binaries_are_required(self):
        doc = self.payload / "share/doc/openxlr/README.md"
        doc.parent.mkdir(parents=True)
        doc.write_text("documentation")
        files = {f["path"]: f for f in json.loads(self.generate().read_text())["files"]}
        self.assertTrue(files["/usr/share/doc/openxlr/README.md"]["optional"])
        self.assertFalse(files["/usr/bin/openxlr"].get("optional", False))

    def test_invalid_headers_prefixes_and_special_files_are_refused(self):
        for arguments in ({"prefix": "/"}, {"prefix": "/usr/../etc"}, {"prefix": "relative"},
                          {"package": "x;bad"}, {"commit": "short"}, {"manager": "unknown"},
                          {"version": "control\u0085value"}, {"version": "\U0001f3a7" * 65}):
            with self.subTest(arguments=arguments), self.assertRaises(ValueError):
                self.generate(**arguments)
        os.mkfifo(self.payload / "fifo")
        with self.assertRaises(ValueError):
            self.generate()

    def test_payload_paths_and_link_targets_follow_the_doctor_contract(self):
        for name in ("line\nbreak", "control\u0085name"):
            with self.subTest(name=name):
                path = self.payload / name
                path.write_text("unsupported name")
                try:
                    with self.assertRaises(ValueError):
                        self.generate()
                finally:
                    path.unlink()
        for target in ("line\nbreak", "control\u0085target", "x" * 4097):
            with self.subTest(target=target[:20]):
                link = self.payload / "link"
                # Some filesystems reject long targets before the generator runs.
                try:
                    link.symlink_to(target)
                except OSError:
                    continue
                try:
                    with self.assertRaises(ValueError):
                        self.generate()
                finally:
                    link.unlink()

    def test_receipt_parent_symlinks_cannot_redirect_writes(self):
        external = self.root / "external"
        external.mkdir()
        (self.payload / "share").symlink_to(external, target_is_directory=True)
        with self.assertRaises(ValueError):
            self.generate()
        self.assertEqual([], list(external.iterdir()))

    def test_generator_refuses_a_file_larger_than_the_doctor_can_inspect(self):
        oversized = self.payload / "oversized"
        with oversized.open("wb") as stream:
            stream.truncate(512 * 1024 * 1024 + 1)
        with self.assertRaises(ValueError):
            self.generate()

    def test_replacing_a_receipt_does_not_truncate_an_existing_hardlink(self):
        self.generate()
        target = self.payload / "share/openxlr/installations/openxlr.json"
        target.unlink()
        original = self.root / "original"
        original.write_text("user original")
        os.link(original, target)
        self.generate()
        self.assertEqual("user original", original.read_text())
        self.assertEqual("openxlr", json.loads(target.read_text())["package"])

    def test_unknown_source_and_archive_provenance_are_explicit(self):
        source = self.root / "source"
        (source / "packaging").mkdir(parents=True)
        self.assertEqual((None, False), receipt.provenance(source))
        (source / "packaging/source-commit").write_text(COMMIT + "\n")
        self.assertEqual((COMMIT, False), receipt.provenance(source))

    def test_archive_source_identity_reaches_msbuild_without_a_git_directory(self):
        source = self.root / "archive"
        (source / "src").mkdir(parents=True)
        (source / "packaging").mkdir()
        (source / "packaging/source-commit").write_text(COMMIT + "\n")
        (source / "src/Directory.Build.props").write_bytes((REPO / "src/Directory.Build.props").read_bytes())
        project = source / "src/Fixture.csproj"
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
        result = subprocess.run(["dotnet", "msbuild", str(project), "-getProperty:SourceRevisionId"],
                                check=True, capture_output=True, text=True, timeout=30)
        self.assertEqual(COMMIT, result.stdout.strip())

    def test_real_git_archive_substitutes_the_source_marker(self):
        source = self.root / "source"
        source.mkdir()
        def git(*args):
            return subprocess.run(["git", "-C", str(source), *args], check=True, text=True, capture_output=True).stdout
        git("init", "-q")
        (source / "packaging").mkdir()
        (source / "packaging/source-commit").write_text("$Format:%H$\n")
        (source / ".gitattributes").write_text("packaging/source-commit export-subst\n")
        git("add", ".")
        git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-qm", "Fixture")
        head = git("rev-parse", "HEAD").strip()
        self.assertEqual((head, False), receipt.provenance(source))
        archive = subprocess.run(["git", "-C", str(source), "archive", "HEAD", "packaging/source-commit"], check=True, capture_output=True).stdout
        import io
        import tarfile
        with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
            self.assertEqual(head, tar.extractfile("packaging/source-commit").read().decode().strip())

    @unittest.skipUnless(all(shutil.which(p) for p in ("rpmbuild", "rpm2cpio", "cpio")), "RPM build tools are not installed")
    def test_rpm_receipt_follows_the_platform_post_processing(self):
        import re
        import tarfile
        top = self.root / "rpm"
        for name in ("SOURCES", "SPECS", "BUILD", "RPMS", "SRPMS", "BUILDROOT"):
            (top / name).mkdir(parents=True)
        recipe = (REPO / "packaging/rpm/openxlr.spec").read_text()
        version = re.search(r"^Version:\s*(\S+)", recipe, re.M)[1]
        header = recipe.split("%prep\n", 1)[0]
        # Retain the real platform hooks and add a deterministic last mutation
        # before the recipe's inventory hook. Hashing during %install fails this.
        mutation = "%global __os_install_post %{__os_install_post}\\\nsed -i 's/original/final/' %{buildroot}/usr/bin/openxlr\\\n%{nil}\n"
        header = header.replace("# Keep the platform's final processing", mutation + "# Keep the platform's final processing")
        source = self.root / ("openxlr-" + version)
        (source / "packaging").mkdir(parents=True)
        shutil.copy2(REPO / "packaging/install_receipt.py", source / "packaging/install_receipt.py")
        (source / "packaging/source-commit").write_text(COMMIT + "\n")
        with tarfile.open(top / "SOURCES" / (source.name + ".tar.gz"), "w:gz") as tar:
            tar.add(source, arcname=source.name)
        spec = top / "SPECS/fixture.spec"
        spec.write_text(header + """%prep
%setup -q
%install
mkdir -p %{buildroot}/usr/bin
printf '#!/bin/sh\\necho original\\n' > %{buildroot}/usr/bin/openxlr
chmod 755 %{buildroot}/usr/bin/openxlr
%files
/usr/bin/openxlr
/usr/share/openxlr/installations/openxlr.json
""")
        built = subprocess.run(["rpmbuild", "-bb", "--nodeps", "--define", "_topdir " + str(top), str(spec)],
                               capture_output=True, text=True, timeout=120)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        extracted = self.root / "extracted"
        extracted.mkdir()
        rpm = next((top / "RPMS").rglob("*.rpm"))
        payload = subprocess.run(["rpm2cpio", str(rpm)], check=True, capture_output=True).stdout
        subprocess.run(["cpio", "-idm", "--quiet"], input=payload, cwd=extracted, check=True, capture_output=True)
        program = extracted / "usr/bin/openxlr"
        self.assertIn(b"final", program.read_bytes())
        inventory = json.loads((extracted / "usr/share/openxlr/installations/openxlr.json").read_text())
        entry = next(f for f in inventory["files"] if f["path"] == "/usr/bin/openxlr")
        self.assertEqual(hashlib.sha256(program.read_bytes()).hexdigest(), entry["sha256"])

    def test_headless_doctor_checks_the_actual_generator_format_and_returns_failures(self):
        manifest = self.generate()
        ui = REPO / "src/OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI.dll"
        self.assertTrue(ui.exists(), "Build the Release solution before running these checks")
        def doctor(*extra):
            env = {**os.environ, "DISPLAY": "", "WAYLAND_DISPLAY": "", "XDG_CONFIG_HOME": str(self.root / "isolated-config")}
            return subprocess.run(["dotnet", str(ui), "doctor", "--root", str(self.root), "--receipt", str(manifest), "--json", *extra],
                                  env=env, check=False, capture_output=True, text=True, timeout=15)
        result = doctor()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], json.loads(result.stdout)["findings"])
        self.program.write_text("modified\n")
        result = doctor()
        self.assertEqual(1, result.returncode)
        finding = json.loads(result.stdout)["findings"][0]
        self.assertEqual("contents were modified", finding["problem"])
        self.assertEqual("sudo apt-get install --reinstall 'openxlr'", finding["repair"])
        self.program.unlink()
        self.assertEqual("missing", json.loads(doctor().stdout)["findings"][0]["problem"])


if __name__ == "__main__":
    unittest.main()
