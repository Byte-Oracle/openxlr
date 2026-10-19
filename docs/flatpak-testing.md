# Flatpak acceptance

Build and install the [experimental package](flatpak-build.md), including
its LSP LinuxAudio extension. The runtime and manifest changes must both
be present. This test uses the installed app, not the SDK or a host build:

```sh
xvfb-run -a -s '-screen 0 1920x1080x24' python3 tools/test-flatpak.py
```

The runner reuses the private PipeWire and session-manager setup of
`tools/test-monitor-volume.py`. It gives the Flatpak private preferences,
a private control-port network namespace and test audio sockets. USB is
removed for this automated run. No host audio routes or profiles change.
The shared runner also accepts `--command <executable> <args...>` for other
checks that need the same private audio setup.

The installed-sandbox check covers window startup, narrow and wide sizes,
close-to-tray, restoring through a second launch, output selection and
measured monitor attenuation. It loads an LV2 plugin from the LinuxAudio
extension, changes a live parameter, measures its effect, opens its native
editor, moves/resizes it, closes and reopens it, then quits through the tray menu and checks daemon and graph cleanup. Desktop volume is cubic: a 0.1 input at 50% produces
0.0125 before the plugin, and the plugin's linear output gain of 0.5 makes
that 0.00625. Captures are checked for finite values and settled amplitude.

The Flatpak workflow runs this once per tree, without a language or size
process matrix. While the separate runtime or manifest PR is absent, it
reports that prerequisite and skips sandbox acceptance. That skip is not
a passed acceptance run. A combined disposable checkout can be tested
locally before those prerequisites land.

The normal C# suite checks the Background portal on a private D-Bus session,
including refusal, startup command lines and cancellation. Its TrayWindowTests
exercise the normal Quit path. A real login, tray host and USB interface
still need desktop/hardware acceptance. Never log out an active user to
simulate a login. On hardware, preserve gain, mute and phantom power and
record the model and actual checks in the PR. Xvfb is not Wayland or an
audible check through physical headphones.

On Plasma Wayland with XWayland available, run
`python3 tools/test-flatpak.py --desktop`. This also checks registration, close, restore and removal against
the real desktop tray host. It opens only a new test window; existing windows
are excluded from the window selection.

For the read-only USB check on an XLR Dock MK.1, first stop the installed
daemon and any Flatpak instance that owns the device, then run:

```sh
flatpak run --filesystem="$PWD:ro" --command=python3 io.github.emaspa.openxlr "$PWD/tools/test-flatpak-usb.py"
```

Restart the original daemon afterwards. The check claims only the vendor
control interface and makes two GET transfers. It never writes gain, mute,
phantom power or headphone volume, refuses multiple matching Docks, and
always releases its own handle. It does not establish support for other
models or replace an audible hardware check.
