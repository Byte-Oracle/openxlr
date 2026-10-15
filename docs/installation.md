# Installation receipts and doctor

Run `openxlr doctor` in a terminal to check an installation. It does not
open the mixer, start or stop services, change audio routing, or repair
anything automatically. It reports each recorded path that is missing,
has different contents or permissions (including special permission bits), or has become a different symbolic
link. Each finding includes a repair or inspection command. Review that
command before running it, particularly if a file was edited on purpose.

The daemon check compares the installed daemon assembly's build identity
with the running daemon's authenticated state. It detects an old daemon
left running after an upgrade even when both builds have the same version.
A stopped, unreachable or older daemon is reported as unverified, with
`systemctl --user restart openxlr-daemon.service` as the repair. An absent
receipt is reported as unknown ownership, not as a healthy installation.

```sh
openxlr doctor
openxlr doctor --json
openxlr doctor --no-runtime
```

Exit codes are 0 when the requested checks pass, 1 for findings, and 2 for
invalid arguments or a maintenance command that could not be applied.
`--no-runtime` skips the daemon comparison. `--json` includes the receipts,
findings, suggested commands and daemon result; it does not include tokens
or the contents of user files. Terminal messages are in English.
An explicit companion receipt can be checked on its own with `--no-runtime`
or as a staged payload with `--root`; it does not require the main package.

## What is recorded

New Debian, RPM and Nix packages include a receipt at
`<prefix>/share/openxlr/installations/openxlr.json`. The optional bridge
can use its own `openxlr-yabridge.json` in that directory. This inventory is
separate from the bridge's existing compatibility marker under
`lib/openxlr/yabridge`.

Each receipt records the package manager, package version, full source
commit when available, whether the source was modified, and the final
payload's exact paths. A regular file has a SHA-256 digest and permissions;
a symbolic link has its target. Absolute and relative links to the same
location are equivalent, including Nix links normalized after fixup. Shared directories are not owned
and are never removed by OpenXLR. The receipt lists its own path without
a self-hash. Package-manager databases remain responsible for verifying
the receipt itself; this is an inventory, not a signed attestation.

The inventory includes the packaged unit, udev rule, WirePlumber rules,
pipewire-pulse drop-in, desktop entry, binaries, plugin host and other
payload files. NixOS generates its user unit outside the package output;
that declarative configuration belongs to NixOS, not to the package's
receipt. Profiles, mixer settings, plugins installed separately by the
user, runtime tokens and caches are user data, not installation payload.

Nix receipts use the read-only permissions the store applies when a build
finishes. Package discovery follows profile links to the actual receipt,
including profiles that link its parent directories;
explicit receipt arguments refuse links.

The daemon saves package receipts under
`~/.config/openxlr/installation-history` when it starts. To record them
without starting the daemon:

```sh
openxlr installation remember
```

These private copies survive an upgrade or package removal. Doctor compares
old inventories with the current one and reports remaining paths that the
current package no longer claims. It suggests moving an unchanged leftover
under the package prefixes (the running installation's own, `/usr` and
`/usr/local`) to an explicit backup path, with an interactive overwrite
prompt. Modified paths, and paths anywhere else, get an inspection command
instead: a saved record proves its own integrity, not who wrote it. A
rejected current receipt disables historical
leftover suggestions until the current inventory can be read again, so active
files are not mistaken for retired paths. Unreadable receipt locations remain
in discovery and are reported rather than silently treated as missing.
An invalid historical receipt gets
a command to move that private copy aside, not to reinstall the package;
saving history skips it and keeps recording newer inventories. Nix store outputs are managed by Nix's
garbage collector and are not treated as stale installation leftovers.
Nix receipts are not archived. Rebuilds of the same package, prefix and
version share one record, retaining paths retired by those rebuilds.
History is bounded to 128 versions and doctor reads at most 16 MiB of
receipts per run; saving history has the same combined byte limit. Older records can be retained elsewhere and checked
individually with `--receipt`.

Ownership before receipts were introduced cannot be reconstructed safely.
An external package recipe must include the inventory hook below to supply
one. In particular, adding the hook here does not change an external AUR
recipe. Package receipts do not take ownership of `/etc` overrides or
files supplied by another installer.

Uninstall packaged files with the package manager that installed them.
Doctor suggests `apt-get install --reinstall`, `dnf reinstall`, an AUR
rebuild with `paru`, or `nix-store --repair-path` for package damage. Use
your original Arch package source if it is not an AUR package. OpenXLR
does not run a privileged, directory-based uninstaller. After a full
uninstall, run doctor from another OpenXLR build and pass a saved receipt:

```sh
openxlr doctor --removed --receipt ~/.config/openxlr/installation-history/<sha256>.json
```

`--removed` checks for remaining paths rather than reporting expected
missing files. It requires an explicit receipt and skips the running daemon. Saved copies
must be named with the SHA-256 of their exact bytes followed by `.json`.
Live receipts must contain a self entry matching their actual path; copies
without a matching content-addressed name are refused.

## Packaging hook

Generate the receipt after stripping, wrapping and compression, and before
the package manager creates its database metadata. The staging root must
contain only this package's payload. Python is needed at build time only.

```sh
python3 packaging/install_receipt.py --root "$pkgdir" --prefix /usr \
  --manager arch --version "$pkgver-$pkgrel"

# Check a staged payload without inspecting the host's daemon or config.
openxlr doctor --root "$pkgdir" \
  --receipt "$pkgdir/usr/share/openxlr/installations/openxlr.json" --json
```

`--manager` accepts `debian`, `rpm`, `arch` and `nix`. The companion also
uses `manual` for its explicit package-free `/usr/local` installation. The prefix is the
absolute installed prefix, not the staging path. Pass a full `--commit`
when the builder already knows it. Otherwise the generator uses Git HEAD
and its tracked-file dirty status, or `packaging/source-commit`, which
`git archive` substitutes through `.gitattributes`. Unknown source
provenance and modified source trees are informational, not failing checks.

An Arch recipe generating its receipt in `package()` must use
`options=('!strip')` and include `python` in `makedepends`, because makepkg
otherwise strips binaries after `package()` and invalidates their hashes.
The packaged receipt generator must run after any manual stripping.

Receipt format 1 is JSON with `formatVersion`, `package`, `manager`,
`version`, `commit`, `dirty`, `prefix` and `files`. File entries carry
`path`, `kind` (`file`, `link` or `receipt`), and the applicable `sha256`,
`mode` or `linkTarget`. Documentation under `share/doc` and `share/licenses`
is marked `optional`: if present it is checked, but its absence is allowed
for package-manager documentation exclusions. User entries can also carry `replacedSha256`,
`replacedMode` and `removed`. Paths are absolute and normalized, confined
to the prefix and unique. SHA-256 digests use lowercase hexadecimal.
The package generator rejects unsupported paths and link targets before
producing a receipt. Duplicate JSON properties, invalid hashes,
linked receipt files, contradictory entry fields, special file types and
oversized records are refused. A staged check also refuses symbolic links
in the installation prefix inside the staging root.
