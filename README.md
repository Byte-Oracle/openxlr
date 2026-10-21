![OpenXLR](docs/banner.png)

[![Latest release](https://img.shields.io/github/v/release/emaspa/openxlr?label=release)](https://github.com/emaspa/openxlr/releases)
[![AUR](https://img.shields.io/aur/version/openxlr?label=AUR)](https://aur.archlinux.org/packages/openxlr)
[![COPR](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fcopr.fedorainfracloud.org%2Fapi_3%2Fpackage%3Fownername%3Demaspa%26projectname%3Dopenxlr%26packagename%3Dopenxlr%26with_latest_succeeded_build%3DTrue&query=%24.builds.latest_succeeded.source_package.version&label=COPR&logo=fedora&logoColor=white&color=51A2DA)](https://copr.fedorainfracloud.org/coprs/emaspa/openxlr/)
[![PPA](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.launchpad.net%2F1.0%2F~sparvoli%2F%2Barchive%2Fubuntu%2Fopenxlr%3Fws.op%3DgetPublishedSources%26source_name%3Dopenxlr%26exact_match%3Dtrue%26distro_series%3Dhttps%3A%2F%2Fapi.launchpad.net%2F1.0%2Fubuntu%2Fresolute%26order_by_date%3Dtrue&query=%24.entries%5B0%5D.source_package_version&label=PPA&logo=ubuntu&logoColor=white&color=E95420)](https://launchpad.net/~sparvoli/+archive/ubuntu/openxlr)
[![OpenDeck plugin](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com%2Femaspa%2Fopenxlr%2Fmain%2Fplugin%2Fcom.emaspa.openxlr.sdPlugin%2Fmanifest.json&query=%24.Version&label=OpenDeck%20plugin&color=3b82f6)](https://github.com/emaspa/openxlr/releases/latest)
[![yabridge](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com%2Femaspa%2Fopenxlr%2Fmain%2Fpackaging%2Fyabridge%2Fsource.json&query=%24.version&label=yabridge&color=8b5cf6)](packaging/yabridge/README.md)

[![Discord](https://img.shields.io/badge/Discord-join%20the%20server-5865F2?logo=discord&logoColor=white)](https://discord.gg/4bswtnGPW4)
[![Reddit](https://img.shields.io/badge/Reddit-r%2FOpenXLR-FF4500?logo=reddit&logoColor=white)](https://www.reddit.com/r/OpenXLR/)
[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-emaspa-FFDD00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/emaspa)
[![Sponsor](https://img.shields.io/badge/Sponsor-GitHub-EA4AAA?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/emaspa)

OpenXLR is a Wave Link replacement for Linux, and it works with any
microphone or audio interface PipeWire can record from. Its mixer runs on
PipeWire: per-application channels, virtual microphones for calls and
streams, monitor mixes on any output, LV2, CLAP and VST3 plugin inserts,
and an OpenDeck plugin for Stream Deck control. XLR 1 and XLR 2 take any
capture source, and each keeps its four-band EQ, insert chain and Sound
Check whatever feeds it. More sources join as channels of their own.

On Elgato hardware OpenXLR also controls the device itself, over
reverse-engineered USB protocols: gain, mute, phantom power and the
onboard effects, the headphone outputs and, on the Wave XLR Pro, the
output routing and a dedicated mix for a second computer on the USB Aux
port. That covers the Elgato XLR interfaces and the Wave:3 and Wave:1 USB
microphones.

![OpenXLR mixer](docs/screenshot-mixer-0142.png)

![OpenXLR terminal mixer](docs/screenshot-tui-0142.png)

The window and the terminal mixer, both from 0.1.42, the terminal mixer
in the Tokyo Night skin. This README and the linked guides describe
current `main`; for a released build, read the docs at its release tag.
Changes merged after a release are available from source until the next release.

Elgato ships no Linux software. Its devices expose class-compliant USB
audio; OpenXLR also supplies device-specific configuration, including
WirePlumber rules that keep XLR Dock, original Wave XLR and Wave:3
capture alive.
Controls beyond standard USB audio use device-specific protocols, decoded
from Wave Link USB captures and prior open-source protocol work. OpenXLR
uses those mappings alongside ALSA controls where available.

Not affiliated with or endorsed by Elgato. Built by protocol analysis on
the author's own hardware.

## Supported devices

Any microphone, headset, webcam or audio interface that PipeWire lists as
a capture source works in the mixer. Bound to XLR 1 or XLR 2
([manual](docs/manual.md#input-bindings)), it gets that strip's sends,
volume, meter, EQ, insert chain and Sound Check, and on XLR 1 the software
low cut and ClipGuard. OpenXLR sends nothing to another brand's device, so
set its preamp gain and phantom power on the device. With no Elgato device
attached the daemon still builds the mixer, and the header offers to
choose a microphone for XLR 1.
[Hardware support](docs/hardware-support.md#other-microphones) lists what
works without Elgato hardware and what does not.

These Elgato devices also get their hardware controls over USB:

| Device | USB id | Status |
|---|---|---|
| Wave XLR Pro | 0fd9:00b4 | mapped controls verified on hardware; the EQ on the unit measured once on the desk; the full mix matrix, mix maximizer and channel booster remain unmapped |
| XLR Dock (Stream Deck+ module) | 0fd9:00a6 | gain, mute, headphone volume, 48V phantom power, low impedance; verified on hardware |
| Wave XLR | 0fd9:007d | gain, mute, headphone volume and low impedance verified by community testers; phantom power implemented, awaiting an OpenXLR hardware check |
| Wave XLR MK.2 | 0fd9:00b6 | gain, mute, phantom power, DSP, ClipGuard, compressor, headphone volume, crossfade; verified on hardware by a community tester |
| XLR Dock MK.2 (Stream Deck+ module) | 0fd9:00c7 | same exposed controls as the Wave XLR MK.2, verified on hardware |
| Wave:3 (beta) | 0fd9:0070 | gain, monitor balance and the desktop volume controls verified on hardware; mute, ClipGuard and headphone volume written and read back on the maintainer's unit; the mute pad, the LED ring and a replug are not yet checked |
| Wave:1 (beta) | 0fd9:006e | detection, the config block, the firmware line, the desktop volume mirror and the gain, mute and headphone volume writes verified from an owner's two diagnostics runs on 0.1.60; ClipGuard and the mic/PC mix are read, their writes not yet seen |

The UI shows the controls OpenXLR exposes for the connected device, and a picker
in the header switches between several attached interfaces. The
per-control state of every device is in
[docs/hardware-support.md](docs/hardware-support.md). If you own an
untested device, open an issue with a diagnostics archive (Options,
SUPPORT, Collect diagnostics).

## Features

- **Hardware control** of Elgato devices through their USB protocols and
  standard ALSA controls. On the Pro: gain,
  mute, low cut, expander, voice tune and phantom power per input,
  ClipGuard, compressor, aux input level and lock, two headphone
  volumes with low-impedance mode, the mic/PC crossfade, and the
  physical output routing (HP1, HP2, Line Out, USB Aux). The Pro's own
  ducking is mapped but not used: it acts inside the unit's mix, which
  OpenXLR's feed to the jacks goes around, so ducking runs in software on
  the Pro like everywhere else. The other
  devices expose the subset their protocol has; see the table above. An
  XLR Dock whose kernel driver lacks a gain, mute or headphone control
  gets that control through the dock's config block instead.
  Where a hardware low cut or ClipGuard control is unavailable, OpenXLR
  offers software processing in PipeWire. Gain lock is a daemon policy
  for devices without physical gain controls. The XLR Dock MK.2 and the
  Wave XLR MK.2 run the microphone EQ on their own processor from
  firmware 1.0.15 (coded from captures, not yet run from OpenXLR on
  hardware), and the Wave XLR Pro on both inputs from firmware 1.0.14
  (one bell measured on the desk). The Pro's mix
  maximizer and channel booster are not exposed; plugin effects run on
  the computer, separate from the device's onboard processing.
- **Submixer** built from PipeWire nodes (null sinks, remap sources,
  filter chains), no kernel modules. Channels for XLR 1 and XLR 2, which
  take an Elgato device's inputs or any other capture source, for the
  Pro's Aux In, for application groups and for other PipeWire capture
  sources (a second microphone, a headset, a capture card); mixes for
  what you hear
  (Monitor A and Monitor B), for virtual microphones other apps record
  from, and for the USB Aux port. Any mix, or a sum of mixes, feeds
  any selected output. The default layout is Game, Music,
  Browser, System, Voice Chat and SFX with Stream and Chat microphones;
  channels and microphones can be added, renamed, reordered and removed
  while audio plays, from the window or the API, and a channel or a mix
  can be hidden with its icon and colour chosen. Per-send levels and
  mutes, a volume per channel, level meters, the monitor mixes on several
  outputs at once with a pause for monitoring that leaves recording and
  streaming alone, an optional boost that takes the monitor volume to
  150%, and ducking: a key microphone lowers chosen channels in chosen
  mixes while it is loud, on any device, set from the window, the
  terminal mixer or a Stream Deck key.
- **Microphone processing** on XLR 1 and XLR 2, whatever feeds them: a
  four-band EQ with Wave Link's band types, ranges and steps, and Sound
  Check, which records a take and loops it through the strip's EQ and
  inserts while you tune them. XLR 1 also has a software low cut and
  ClipGuard when it is bound to another source or the interface has no
  hardware version of them. A capture input has its own insert chain
  and, when mono, Sound Check; the EQ, low cut and ClipGuard stay on the
  XLR strips.
- **Inserts**: LV2, CLAP and VST3 plugin chains on each XLR input and each mix, with a
  plugin picker, generated controls, bypass and native plugin editors.
  Input chains use mono effects; mix chains use stereo effects. VST3 discovery
  checks supported layouts, so effects that default to stereo but accept mono
  are also available on XLR inputs. Editable native editor compatibility rules
  select OpenXLR controls for troublesome editors without changing audio processing.
  Windows VST3 and CLAP plugins use Wine and yabridge; the
  `openxlr-yabridge` package supplies a tested bridge with the Wine editor
  input fix and private wrappers.
- **Plugin installation and management** from Options: import extracted files
  or bundles, add plugin folders to search in place, rescan, and manage Windows
  plugin folders and individual plugins.
  Single Windows-plugin imports do not register neighbouring plugins.
  Disable or re-enable plugins, remove their uses from current chains,
  delete standalone plugins, or open Wine's uninstaller for installed packages.
  Removing a folder from the list keeps its source files and refreshes discovery.
  See the [Windows plugin guide](docs/manual.md#windows-plugins).
- **Application routing**: audio clients are detected from their
  PipeWire registration and routed to a channel by name rules, with the
  assignment remembered per app; an app can also be left to the
  desktop's own routing. Electron apps are identified by their process
  binary rather than the "Chromium" name they report, and each app's
  tile shows its own desktop icon. Missing stream
  metadata falls back to the owning client, and Windows executable names
  share an identity with their Wine/Proton client so saved routing persists.
  A desktop shortcut or a Stream Deck key routes the application that has
  the focus to a channel.
- **Profiles**: named scenes holding the hardware settings and the
  whole submix (levels, mutes, outputs, insert chains), saved per device
  and recalled from the UI, the API or a Stream Deck key. One profile
  per device can be recalled on connect, so an interface comes up in a
  known scene at login or after a power cycle. OpenXLR also restores the
  last settings on the original Wave XLR and first XLR Dock without a
  profile, with a reset to the defaults recorded after a power cycle.
  With no Elgato device connected, profiles hold the mixer alone.
- **OpenDeck plugin**: key and dial actions for every switch, mute,
  level, channel volume, insert and output, rendered with level meters
  and status LEDs, plus output volume and mute keys, a ducking key, the
  system output switch and focused-application routing. It is a client of the daemon's API, so it
  reflects changes made in the UI or on the hardware; a dial turn leads
  locally, so the daemon's echo never pulls the dial back mid-turn.
- **Daemon and UI**: the daemon owns the device and the graph, keeps
  running with the window closed, follows the PipeWire registry through
  one subscription, re-asserts the chosen default sink and source once a
  second, and serves a WebSocket API and a versioned HTTP API (`/api/v1`)
  on 127.0.0.1:37890. The UI has a Flow window with selectable signal
  paths through inputs, channels, mixes and outputs, a tray icon and a
  diagnostics archive exporter. It also owns the desktop keys: global
  shortcuts registered through the desktop portal that route the focused
  application, step an output's volume, toggle its mute or switch the
  system output, active while the window sits in the tray. Options
  separates background audio and app startup at login, with independent
  choices for launch visibility and whether closing the window keeps the
  app in the tray or quits.
- **Terminal mixer**: `openxlr-tui` is the whole mixer in a terminal, for a
  machine with no desktop session, an ssh connection or a tiling setup. It
  speaks the same socket as the window, so both can be open at once, and it
  wears the same skins. Tabs in the manner of btop: the submix with
  live meters, the send matrix, the hardware inputs, the outputs, the
  applications, the insert chains, the profiles and the options.
  See [the manual](docs/manual.md#terminal).
- **Omarchy bar**: `openxlr.mixer` shows XLR meters, mute and monitor feeds
  in Omarchy 4, with channel sends and mix masters in a popout. The Arch
  package integration and user enable command are in
  [docs/omarchy.md](docs/omarchy.md). The bar and the popout have been
  checked on Omarchy 4.0.4; the live theme switch and the hardware
  controls still need desktop acceptance.
- **Skins**: the window's appearance is a set of named values a skin file
  can replace. Sixteen ship, all built into the application: Material, the
  classic look, Deck, which dresses the window in the visual language of the
  OpenDeck plugin's keys and dials, one for each of the eleven Omarchy
  palettes, Nyan Pink, Broadcast and Wave. The last two arrange the window
  as a mix matrix, with inputs as rows and mixes as columns. Broadcast is
  the default, for new installs and for installs that never picked a skin,
  and uses neutral greys; Wave uses charcoal surfaces and blue accents. A skin is
  data, not code: colours, sizes, local images from its own folder and a
  choice between the control appearances OpenXLR draws,
  picked in Options and applied to open windows without touching audio. The
  terminal mixer reads the same files and follows the same choice.
  See [docs/skins.md](docs/skins.md).
- **Optional update notice**: the UI can check the upstream GitHub release
  feed for a newer stable release. Startup checks are off by default and,
  when enabled, run at most once per day. Nothing is installed automatically.

The full feature list, area by area: [docs/features.md](docs/features.md).

### OpenDeck plugin

Dials get a touch panel with a knob, a level meter, the value and a
mute overlay; one dial can hold several targets, cycled by tap or
press.

![Dial panels](docs/plugin-dials.png)

Keys show an icon and a status LED (red for a mute, green for an
engaged feature or the active monitor output). Every hardware switch,
mute, level and insert is a target.

![Keys](docs/plugin-keys.png)

## Install

Packages exist for Arch Linux, CachyOS and Omarchy (AUR), Ubuntu (PPA),
Fedora (COPR) and NixOS (flake); every release also carries a `.deb` and
an `.rpm` for a manual install, with SHA-256 checksums and a GitHub build
provenance attestation (`gh attestation verify <file> --owner emaspa`).
An experimental Flatpak is built by CI and not yet on Flathub; see
[docs/flatpak.md](docs/flatpak.md). Pick your distribution:

**Arch Linux, CachyOS and Omarchy**, from the
[AUR](https://aur.archlinux.org/packages/openxlr); the two derivatives
install the same package:

```sh
yay -S openxlr        # or: paru -S openxlr
systemctl --user enable --now openxlr-daemon
openxlr               # the mixer UI, also in your application menu
```

A package built on Omarchy also carries the bar plugin. Run
`openxlr-omarchy-enable` once to link and enable it, as
[docs/omarchy.md](docs/omarchy.md) describes.

**Ubuntu** 24.04 and 26.04, from the
[PPA](https://launchpad.net/~sparvoli/+archive/ubuntu/openxlr):

```sh
sudo add-apt-repository ppa:sparvoli/openxlr
sudo apt install openxlr
systemctl --user enable --now openxlr-daemon
openxlr
```

Without the PPA, download the `.deb` from the
[latest release](https://github.com/emaspa/openxlr/releases/latest) and
run `sudo apt install ./openxlr_*_amd64.deb`.

**Fedora** 44 or newer, from the
[COPR repository](https://copr.fedorainfracloud.org/coprs/emaspa/openxlr/):

```sh
sudo dnf copr enable emaspa/openxlr
sudo dnf install openxlr
systemctl --user enable --now openxlr-daemon
openxlr
```

Without COPR, download the `.rpm` from the
[latest release](https://github.com/emaspa/openxlr/releases/latest) and
run `sudo dnf install ./openxlr-*.x86_64.rpm`.

**NixOS**: the repo is a flake with a package and a module. The module
enables the daemon itself; after a rebuild, `openxlr` is in the
application menu.

Add `inputs.openxlr.url = "github:emaspa/openxlr";` to your system flake,
then include these entries in its `nixosSystem.modules` list:

```nix
openxlr.nixosModules.default
{ services.openxlr.enable = true; }
```

Here `openxlr` is the input passed to the flake's `outputs` function. The
[module options](packaging/nix/module.nix) configure LV2 plugins, software
ClipGuard and the optional Windows bridge.

With an Elgato device, replug it once after installing so the udev rule
applies. For the Stream Deck, install
`com.emaspa.openxlr.sdPlugin.zip` from the release with OpenDeck's
install-from-file, or copy the folder the package puts in
`/usr/share/openxlr/` into `~/.config/opendeck/plugins/`. Inserts show
compatible LV2, CLAP and VST3 plugins (`lsp-plugins-lv2` is the set used
during development); the software ClipGuard for the XLR Dock needs
`swh-plugins`. The NixOS module wires both up itself. Focused-application
routing asks the window over D-Bus with `gdbus` from the GLib command-line
tools (`glib2` on Arch and Fedora, `libglib2.0-bin` on Ubuntu), which the
packages depend on. The packages also
raise pipewire-pulse's open-file limit with a systemd drop-in, which
applies at the next login or after `systemctl --user restart
pipewire-pulse`; a source install needs the same file before growing the
layout ([manual: open-file limit](docs/manual.md#open-files)).

### Upgrading

A package upgrade replaces the files on disk and leaves the running
programs on the code they started with, so a new version appears only
after they restart. Rebooting does that, and so does this:

```sh
systemctl --user restart openxlr-daemon
```

Then close the mixer window and open it again, and quit `openxlr-tui` if
it is running. On Omarchy, reload the bar plugin with:

```sh
omarchy-shell shell rescanPlugins
```

Replug the interface only when the release says the udev rule changed.

### Build from source

Needs the .NET 10 SDK, PipeWire tools, libusb, lilv, the GLib
command-line tools, a C/C++ compiler and the native host's development
headers. Install the prerequisites in
[the source guide](docs/install-from-source.md) first. From the repository root:

```sh
git clone https://github.com/emaspa/openxlr.git
cd openxlr
dotnet restore src/OpenXLR.slnx --locked-mode
dotnet build src/OpenXLR.slnx -c Release --no-restore -warnaserror -p:EnableNativeLv2Host=true
OPENXLR_BUILD_MIXER=1 ./src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon
```

In a second terminal, from the same repository root:

```sh
./src/OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI
```

Elgato device access needs the tracked udev rule and a replug. The source
guide
also covers WirePlumber rules, the open-file limit, the user service,
updating and uninstalling. Keep the native build flag on subsequent builds
to retain CLAP, VST3 and plugin editors.

For Windows plugins, install `openxlr-yabridge` from the same place you
installed OpenXLR: the AUR, the PPA, the COPR repository, or the packages
on the release page. It carries a tested bridge with the Wine editor input
fix, without which a plugin editor ignores the mouse on Wine 9.22 and
newer. [The manual](docs/manual.md#windows-plugins) walks through it and
[the package guide](packaging/yabridge/README.md) covers building it
yourself.

## Documentation

[Documentation index](docs/README.md): user guides, developer references and
historical protocol research.

- [Manual](docs/manual.md): first run, the concepts behind the mixer,
  step-by-step tasks, the Stream Deck plugin, troubleshooting
- [Features](docs/features.md): every control, the submixer, inserts,
  routing, profiles and the OpenDeck plugin in detail
- [Roadmap](docs/roadmap.md): what comes next, in order, and the rules a
  change has to meet to land
- [Installing from source](docs/install-from-source.md): prerequisites
  by distribution, device access, the user service, updating,
  uninstall, environment variables
- [Installation receipts and doctor](docs/installation.md): owned files,
  build identity and repair commands
- [WebSocket API](docs/api.md): the daemon's command set and the files
  under `~/.config/openxlr`; [HTTP API](docs/http-api.md) for the same
  commands over plain HTTP
- [Saved mixer layout](docs/mixer-layout.md): the layout file and the
  live layout commands
- [Skins](docs/skins.md): the appearance values a skin sets, where skins
  are found, and what a skin is not allowed to do
- [Omarchy bar plugin](docs/omarchy.md): the shell plugin, its enable
  command, the Arch package recipe and what the popout does not do
- [Architecture](docs/architecture.md): daemon, UI and plugin, the
  PipeWire graph, the device protocols, repository layout
- [Hardware support](docs/hardware-support.md): per-control status of
  every device
- [Wave XLR Pro protocol](docs/wave-xlr-pro-protocol.md): the vendor
  protocol as reverse engineered, with offsets
- [USB capture guide](docs/usb-capture.md): how to capture Wave Link
  traffic for an untested device
- [CONTRIBUTING.md](CONTRIBUTING.md): how to build, test and send a
  change; [AGENTS.md](AGENTS.md) for AI-assisted contributions, which
  are allowed as long as the author owns and can explain the code

## Community

Support, hardware reports, feature requests and release news also live
on the OpenXLR Discord server, [discord.gg/4bswtnGPW4](https://discord.gg/4bswtnGPW4),
and on Reddit at [r/OpenXLR](https://www.reddit.com/r/OpenXLR/). The
window links to both from About in the main header. Confirmed bugs still end up
as GitHub issues, so any of the three works to start. To help with
code or hardware reports, read [CONTRIBUTING.md](CONTRIBUTING.md)
first.

## Reporting problems

Open Options, then SUPPORT, then Collect diagnostics. It writes
`~/openxlr-diagnostics-<timestamp>.tar.gz` with the app and device
state, a raw vendor-block dump, the PipeWire graph, daemon logs and
configs. Nothing gets uploaded; attach the archive to an issue, or to a
post in the Discord support forum, yourself.

For a plugin that will not appear, the same section lists the bundles the
last scan skipped and the folders it could not read. A Windows plugin that
fails only under the bridge has a deep Wine trace checkbox there: turn it
on, Rescan, collect the archive, then turn it off. Scans are much slower
while it is on, and it clears itself on a daemon restart.

## Credits

- [Emanuele Sparvoli](https://github.com/emaspa): author, maintainer and code.
- [Carina Schoppe](https://github.com/CarinaSchoppe): maintainer and code.
- [Michael Brooks](https://github.com/Michael-Brooks): code and hardware testing.
- [BenjyEX3](https://github.com/BenjyEX3): hardware testing.
- [chromacurse](https://github.com/chromacurse): hardware testing.
- [Astros52](https://github.com/Astros52): hardware testing.
- [marv4u](https://github.com/marv4u): hardware testing and protocol research.
- [rikkichy](https://github.com/rikkichy): prior protocol research.
- [nekename](https://github.com/nekename): upstream software.
- [FrostyCoolSlug](https://github.com/FrostyCoolSlug): technical guidance.
- All the various community testers (thank you!): hardware and packaging testing.

## Status

Developed and used daily by the author with a Wave XLR Pro, an XLR
Dock, an XLR Dock MK.2 and a Stream Deck + XL. The Wave XLR and Wave
XLR MK.2 backends were verified on hardware by community testers. The
Wave:3 and Wave:1 are in beta: their backends are written from public
protocol research. On the maintainer's Wave:3 every control writes and
reads back, and the checks that need someone at the microphone are
still open. A Wave:1 owner confirmed detection, the readings and the
gain, mute and headphone volume writes through two diagnostics
archives. See the device table and
docs/hardware-support.md for what is still open.

A microphone bound to XLR 1 or XLR 2 in place of an Elgato input, and a
desk with no Elgato device at all, are covered by tests against a private
PipeWire server with simulated sources. The checks on real setups that are
still open are listed under input bindings in
[docs/hardware-support.md](docs/hardware-support.md#input-bindings).

The code is written by the two maintainers, with AI tooling (Anthropic's
Claude and OpenAI's Codex) assisting with protocol capture analysis, UI
design and parts of the coding; the person who opens a pull request is
its author and answers for every line of it (AGENTS.md). Every hardware
finding on the devices the project owns or testers ran was verified live
on a real device. The Wave:3 and Wave:1 facts name their source in the
code and are marked by what an owner has confirmed so far.

## License

[GPL-3.0](LICENSE). If you find OpenXLR useful, consider
[buying me a coffee](https://buymeacoffee.com/emaspa).
