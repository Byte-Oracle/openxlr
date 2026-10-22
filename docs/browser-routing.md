# Browser tab routing

The optional Chromium companion gives each HTTP or HTTPS tab a separate
entry under APPLICATIONS and in Manage. Choose Music for YouTube and Voice
Chat for a Teams tab or PWA, for example. It supports native Linux installs
of Chrome, Chromium, Brave and Edge, starting with Chromium 116. Firefox and
the Flatpak build are not supported by this companion.

PipeWire often calls every browser stream `Playback`. It can also combine
several tabs into one stream. OpenXLR therefore does not guess a tab from a
stream title or process id. The companion takes over audio from a tab that
you activate and plays it into the selected OpenXLR application channel.
It does not record an audio file. A call still uses the microphone chosen
in the browser or website. Check a real call's echo cancellation and delay
with your headset before relying on this for meetings.

## Install the companion

This is an unpacked extension, installed separately from OpenXLR. Use a
checkout of the same version as your native OpenXLR installation.

1. Open `chrome://extensions`, `brave://extensions` or `edge://extensions`.
   Enable Developer mode, choose Load unpacked and select
   `browser/chromium` from the checkout. Pin its action if useful.
2. Check that its id is `ojdjoifendmgooonjlbdbaiondkpeihh`. The public key in
   the manifest keeps this id stable across checkouts. No private signing
   key is shipped.
3. Copy `browser/native-host.json` into your browser's native messaging
   directory as `com.emaspa.openxlr.browser.json`. Use the row for your
   browser:

   | Browser | Directory under `~/.config` |
   | --- | --- |
   | Chrome | `google-chrome/NativeMessagingHosts` |
   | Chrome Beta | `google-chrome-beta/NativeMessagingHosts` |
   | Chrome Unstable | `google-chrome-unstable/NativeMessagingHosts` |
   | Chromium | `chromium/NativeMessagingHosts` |
   | Brave | `BraveSoftware/Brave-Browser/NativeMessagingHosts` |
   | Edge | `microsoft-edge/NativeMessagingHosts` |

   For example, for Chromium:

   ```sh
   mkdir -p ~/.config/chromium/NativeMessagingHosts
   cp browser/native-host.json ~/.config/chromium/NativeMessagingHosts/com.emaspa.openxlr.browser.json
   ```

   Custom browser profiles and managed installations can use another
   directory. The manifest's `path` must be the absolute path to your
   installed `openxlr` executable. Its default is `/usr/bin/openxlr`.
   Keep `allowed_origins` restricted to the extension id above. Restart the
   browser after installing the host, or press Reconnect in the popup.

## Choose and activate a tab

1. In OpenXLR's APPLICATIONS card, enable browser output selection. This
   suspends generic application routing for recognized Chromium browsers
   and keeps their selected outputs. Existing browser-wide assignments
   stay saved and resume when you turn the option off. Browser audio that
   has not been taken over keeps the browser or desktop's selected output,
   usually the Linux default. Enabling this option does not move existing
   streams to another output.
2. Open the companion's popup on the tab that should play into OpenXLR.
   Output access opens a visible setup page. Chromium requires microphone
   permission to reveal non-default output devices. Press the setup
   button to grant it; the companion immediately stops the temporary
   microphone stream and neither records nor forwards it.
3. Pick a channel in OpenXLR's tab entry or in the popup. Check Remember
   website to save that origin's default. A tab-only choice leaves the
   site's existing default for other tabs intact. Forget website removes
   the saved default and returns that tab to Browser default.
4. Return to the audio tab, open the companion and press Activate. A PWA
   without a toolbar can use the extension action shortcut, initially
   Ctrl+Shift+9. Change it in your browser's extension shortcuts page if
   another application owns it. Activation requires a browser gesture;
   choosing a channel in OpenXLR alone cannot grant capture permission.
5. While active, channel changes in either place reroute that tab. Other
   active tabs keep their own channel. Stop, closing the tab, navigating
   to another origin, disabling browser output selection or losing the
   daemon connection releases the takeover. Normal browser playback can
   then resume. Reconnection restores the list, not capture permission.

Control requests already waiting when the daemon connection is lost are
discarded, rather than replayed after reconnection. An old error cannot
replace the new connection's status. The latest tab metadata is kept and
registered again so the list remains current; retry a refused channel
choice after the connection returns.

A same-origin page change keeps the tab's selection. A different origin
uses its own saved default. A late assignment from the previous website is
refused instead of changing the new website's routing. After restarting the
browser, website defaults remain, but each tab needs activation again.
OpenXLR supports up to eight
active tab takeovers and 128 listed tabs per browser connection, with up
to eight connections and 128 saved website defaults. Private browsing,
browser settings pages and non-HTTP(S) URLs are excluded.
The daemon also limits each command to 64 KiB. Long tab metadata can reach
that limit before 128 tabs; the popup reports it instead of sending a
partial list. Close some tabs and reconnect to publish a smaller list.

If outputs cannot be enumerated, a channel is missing or an output label
is ambiguous, activation is refused without taking over audio. Renaming
an already matched channel preserves its output-device binding. A removed
output or a failed reroute stops only the affected tab. The companion
does not keep retrying a failed capture or native host in the background;
correct the problem and activate or reconnect explicitly.

## Privacy and removal

The companion sends the current tab list to the local native host: tab id,
bounded title, HTTP(S) origin and routing status. The URL field is reduced
to its origin, excluding its path, query and credentials. Page titles come
from the website and are not redacted; a title can itself include a name
or link. The daemon keeps live tab ids and titles in memory and saves only
remembered origin-to-channel defaults in machine settings, outside
profiles. A manually collected diagnostic archive can include the current
tab list and saved origins. Review it before sharing.
No tab audio is sent through the control API or stored by the companion.

The host accepts only tab registration, channel assignment and the browser
output preference. It authenticates using the existing local daemon client
and does not expose the API token or unrelated daemon state to the browser.
Web pages cannot call this host through the extension.

To remove the companion, stop its active tabs, remove it in the browser's
extension page and delete only the native host manifest you installed.
Turn off browser output selection in OpenXLR to resume generic browser
routing. Remembered website defaults can be forgotten from their tab
entries before removing the companion.
