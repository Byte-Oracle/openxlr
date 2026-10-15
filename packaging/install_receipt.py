#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Inventory a finished package payload. This is a build tool, not an uninstaller."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tempfile
import unicodedata


def valid_text(value, maximum):
    """The reader accepts bounded text without Unicode control characters."""
    return 0 < len(value.encode("utf-16-le")) <= maximum * 2 and not any(unicodedata.category(c) == "Cc" for c in value)


def provenance(source):
    """git archive substitutes this marker; ordinary checkouts use their HEAD."""
    marker = source / "packaging/source-commit"
    value = marker.read_text().strip() if marker.exists() else ""
    if re.fullmatch(r"[0-9a-fA-F]{40}", value):
        return value.lower(), False
    try:
        commit = subprocess.run(["git", "-C", str(source), "rev-parse", "HEAD"],
                                check=True, capture_output=True, text=True).stdout.strip()
        dirty = subprocess.run(["git", "-C", str(source), "status", "--porcelain", "--untracked-files=no"],
                               check=True, capture_output=True, text=True).stdout
        if re.fullmatch(r"[0-9a-f]{40}", commit):
            return commit, bool(dirty)
    except (OSError, subprocess.CalledProcessError):
        pass
    return None, False


def write_receipt(root, prefix, package, manager, version, commit=None, dirty=False):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.+-]{0,79}", package):
        raise ValueError("Invalid package name")
    if manager not in ("debian", "rpm", "arch", "nix", "manual"):
        raise ValueError("Unsupported package manager")
    if manager == "manual" and (package != "openxlr-yabridge" or prefix != "/usr/local"):
        raise ValueError("Manual receipts are reserved for the local companion install")
    if not valid_text(version, 128):
        raise ValueError("Invalid version")
    if commit is not None and not re.fullmatch(r"[0-9a-fA-F]{40}", commit):
        raise ValueError("Commit must be a full Git commit or unknown")
    if not prefix.startswith("/") or prefix.startswith("//") or prefix == "/" or str(Path(prefix)) != prefix or ".." in Path(prefix).parts or not valid_text(prefix, 4096):
        raise ValueError("Prefix must be an absolute normalized installation prefix")
    root = Path(root).resolve()
    if root == Path("/") and manager != "nix":
        raise ValueError("Use a package staging root, not the running filesystem")
    payload = root / prefix.lstrip("/")
    for part in [payload, *payload.parents]:
        if part == root:
            break
        if part.is_symlink():
            raise ValueError("Payload parents must not be symbolic links")
    if payload.is_symlink() or not payload.is_dir():
        raise ValueError("Payload prefix must be a real directory")
    # Only this package's payload belongs here. Call after all stripping,
    # wrapping and compression, before package-manager metadata is generated.
    target = payload / "share/openxlr/installations" / (package + ".json")
    for part in [target, *target.parents]:
        if part == payload:
            break
        if part.is_symlink():
            raise ValueError("Receipt parents must not be symbolic links")
    target.parent.mkdir(parents=True, exist_ok=True)
    files = []
    for directory, directories, names in os.walk(payload, followlinks=False):
        for name in sorted(directories + names):
            path = Path(directory) / name
            mode = path.lstat().st_mode
            if stat.S_ISDIR(mode):
                continue  # Shared directories are not owned or removed by OpenXLR.
            installed = "/" + path.relative_to(root).as_posix()
            if not valid_text(installed, 4096):
                raise ValueError("Unsupported payload path: " + repr(installed))
            if path == target:
                continue
            if stat.S_ISLNK(mode):
                link_target = os.readlink(path)
                if not valid_text(link_target, 4096):
                    raise ValueError("Unsupported symbolic link target: " + repr(link_target))
                files.append({"path": installed, "kind": "link", "linkTarget": link_target})
            elif stat.S_ISREG(mode):
                if path.stat().st_size > 512 * 1024 * 1024:
                    raise ValueError("Payload file exceeds the 512 MiB inspection limit: " + str(path))
                if stat.S_IMODE(mode) > 0o777:
                    raise ValueError("Special file permissions are not supported: " + str(path))
                with path.open("rb") as stream:
                    digest = hashlib.file_digest(stream, "sha256").hexdigest()
                permissions = (0o555 if mode & stat.S_IXUSR else 0o444) if manager == "nix" else stat.S_IMODE(mode)
                entry = {"path": installed, "kind": "file", "sha256": digest, "mode": permissions}
                if installed.startswith(prefix + "/share/doc/") or installed.startswith(prefix + "/share/licenses/"):
                    entry["optional"] = True
                files.append(entry)
            else:
                raise ValueError("Unsupported payload file type: " + str(path))
    if len(files) >= 8192:
        raise ValueError("Too many payload files")
    files.append({"path": "/" + target.relative_to(root).as_posix(), "kind": "receipt", "mode": 0o444 if manager == "nix" else 0o644})
    receipt = {"formatVersion": 1, "package": package, "manager": manager, "version": version,
               "commit": commit.lower() if commit else None, "dirty": dirty, "prefix": prefix,
               "files": sorted(files, key=lambda f: f["path"])}
    encoded = (json.dumps(receipt, indent=2, ensure_ascii=True) + "\n").encode()
    if len(encoded) > 4 * 1024 * 1024:
        raise ValueError("Receipt exceeds 4 MiB")
    if target.is_symlink():
        raise ValueError("Receipt destination must not be a symbolic link")
    with tempfile.NamedTemporaryFile(dir=target.parent, prefix=".openxlr-receipt-", delete=False) as temporary:
        temporary_path = Path(temporary.name)
        try:
            temporary.write(encoded)
            temporary.flush()
            os.fchmod(temporary.fileno(), 0o644)
            os.replace(temporary_path, target)
        finally:
            temporary_path.unlink(missing_ok=True)
    return target


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, type=Path, help="Finished package staging root")
    parser.add_argument("--prefix", default="/usr")
    parser.add_argument("--package", default="openxlr")
    parser.add_argument("--manager", required=True, choices=("debian", "rpm", "arch", "nix", "manual"))
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit")
    parser.add_argument("--dirty", action="store_true")
    parser.add_argument("--source", type=Path, default=Path(__file__).resolve().parent.parent)
    args = parser.parse_args()
    commit, dirty = (args.commit, args.dirty) if args.commit else provenance(args.source)
    dirty = dirty or args.dirty
    try:
        print(write_receipt(args.root, args.prefix, args.package, args.manager, args.version, commit, dirty))
    except (OSError, ValueError) as error:
        parser.exit(1, str(error) + "\n")


if __name__ == "__main__":
    main()
