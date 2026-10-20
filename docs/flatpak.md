# Flatpak

Flatpak support is experimental. Distro packages remain the recommended
installation. The Flatpak window owns its daemon: closing to the tray keeps
audio running; Quit stops both. Stop the packaged daemon before launching
the Flatpak, since they use the same control port. Disable the native
installation's login options before enabling Flatpak login startup.

## Startup and logs

The login settings use the desktop background portal. Start audio in the
background launches in the tray. Start the OpenXLR app shows the window,
unless Tray only is selected under On launch. A refused request is reported
without saving it. If autostart is granted but background activity is not,
the login choice is saved with a warning so the entry can still be disabled.
Desktop settings can revoke permission later.

Restart daemon restarts the child without touching host services. Failed
children are retried with a delay; a busy API port or owned daemon lock
ends the attempt. Restart interrupts a pending retry delay. A parent-process wait also stops the daemon if the
window crashes. The tray uses the app's D-Bus namespace, including when
the desktop tray host restarts. There is no systemd watchdog. Private `daemon.log`
under `$XDG_STATE_HOME/openxlr` has one rotated copy. Collect
diagnostics includes both instead of the journal.
Existing log paths must be regular files, so a leftover pipe cannot block
the daemon. Diagnostic copies use the same privacy filtering as native logs.
Log links are omitted, and credential-shaped fields are masked even when
their values are not known in advance.
Tray menu queries and event batches have a fixed item limit, so an invalid
desktop request cannot create an unbounded list of menu items.
Tray hosts can request the complete menu with an empty item list.

## Host setup

On first opening the mixer, the three WirePlumber rules are installed in
the host user's `~/.config/wireplumber/wireplumber.conf.d`. Identical files
are kept. Different files and symbolic links are reported and never
overwritten. The rules take effect at the next login. For a custom host
`XDG_CONFIG_HOME`, grant its `wireplumber/wireplumber.conf.d` directory and set
`OPENXLR_HOST_CONFIG_HOME` to its parent with a Flatpak override. App
preferences and profiles stay in Flatpak's private XDG directories.

USB controls require the host udev rule. Run outside the sandbox, then
reconnect the interface:

```sh
sudo install -m 644 "$(flatpak info --show-location io.github.emaspa.openxlr)/files/share/openxlr/70-openxlr.rules" /etc/udev/rules.d/70-openxlr.rules
sudo udevadm control --reload-rules
```

The optional [Pro UCM profile](../packaging/ucm/README.md) remains a manual
host step. Flatpak cannot raise the host pipewire-pulse open-file limit or
grant unlimited memory locking. Large layouts and bridged Windows plugins
remain better suited to distro packages. Removing the app leaves manually
installed host files in place. Remove only the exact files you installed,
after checking they have not been modified.

## Plugins

LV2 uses Flathub LinuxAudio plugin extensions at
`/app/extensions/Plugins/lv2`. The catalogue and both hosts share this path.
Host plugin folders and yabridge are not exposed. Native editors need X11
or XWayland. The native helper retains CLAP and VST3 support for compatible
bundles imported into the app's private storage, provided their external
dependencies are available in the sandbox.
Imports under `~/.lv2`, `~/.clap` and `~/.vst3` are private persistent
Flatpak mappings. They never replace bundles in the host's folders.

Desktop-specific helper features, such as Plasma's shared maximum-volume
preference, require helpers not included in the Freedesktop runtime. The
normal mixer volume controls still work. The installation doctor does not
yet inventory the manual Flatpak host setup; use the exact paths above.
