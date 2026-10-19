# Building the experimental Flatpak

The runtime changes described in [flatpak.md](flatpak.md) must land before
this package is usable. The manifest targets x86_64, matching the committed
.NET runtime graph. It uses the Freedesktop 25.08 runtime and .NET 10 SDK
extension. Flatpak 1.16 or newer is required for the USB-only device grant.

```sh
flatpak install --user flathub org.freedesktop.Sdk//25.08 org.freedesktop.Platform//25.08 org.freedesktop.Sdk.Extension.dotnet10//25.08 org.flatpak.Builder
python3 packaging/flatpak/nuget-sources.py --check
flatpak run org.flatpak.Builder --user --force-clean --disable-rofiles-fuse --repo="$HOME/.cache/openxlr-flatpak/repo" "$HOME/.cache/openxlr-flatpak/build" packaging/flatpak/io.github.emaspa.openxlr.json
flatpak --user remote-add --no-gpg-verify --if-not-exists openxlr-local "$HOME/.cache/openxlr-flatpak/repo"
flatpak install --user openxlr-local io.github.emaspa.openxlr
```

Use a build directory and state directory under the home directory, since
the builder's private `/tmp` is not shared with commands it starts outside
its own sandbox. The manifest's directory source builds the current tree.
For Flathub submission this source becomes a pinned Git commit and the
manifest's local packaging files move with it to the Flathub repository.
No Flathub submission or release is created by these commands.

The native host and its LV2 dependencies are built in the SDK. PipeWire
client tools and filter modules, ALSA's `amixer`, and WirePlumber's `wpctl`
are included. They connect to the host audio servers; the package never
starts its own PipeWire server or session manager in normal use.

The manifest grants the audio sockets, USB device nodes, graphics and
specific tray/focus D-Bus names. Only the host WirePlumber configuration
and OpenXLR runtime directory are writable outside private app storage.
Loopback API and Stream Deck clients require the shared network namespace.
Audio clients identify their media category as Manager so WirePlumber can
permit global mixer controls, including default-device selection.
It grants neither home-directory access nor permission to spawn host commands.

After a NuGet update, regenerate `packaging/flatpak/nuget-sources.json` with
`python3 packaging/flatpak/nuget-sources.py`. The generator records package
versions and content identities from the lock files, and separately pins
the downloaded ZIP bytes. NuGet still verifies its own content hashes in
locked mode during the offline build.

Install LV2 plugins separately, for example:

```sh
flatpak install --user flathub org.freedesktop.LinuxAudio.Plugins.LSP//25.08
```

Run the acceptance checks described in [flatpak.md](flatpak.md) before
using the build with a live audio setup. The ordinary packages remain the
recommended installation.
