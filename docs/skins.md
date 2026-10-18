# Skins

A skin changes how the OpenXLR window looks. It is a folder with a
`skin.json` in it and, optionally, a few images beside it. It sets named
appearance values and, if it wants, where the header and the
window's sections go ([Layout](#layout)); it carries no markup, no script and
no code, and it cannot reach a file outside its own folder.

OpenXLR ships thirteen appearances, all compiled into the application, so
every package has them and there is nothing to install:

- **Material**, the default, in a dark and a light palette (see
  [Material's mode](#materials-mode)). The dark palette is what the window
  has always looked like, and it is what every unset value falls back to.
- **Deck**, which dresses the window in the visual language of the
  OpenDeck keys and the Wave interfaces: near-black faceplates, black keys
  whose lettering is backlit green when a control is on and red when
  something is muted, bypassed or destructive, console faders with a
  machined cap, indicator lamps in a bezel, and meters that run green to
  amber to red across the scale.
- Eleven built from the [Omarchy](https://omarchy.org) palettes, so the
  window matches the rest of a themed desktop: Catppuccin, Catppuccin
  Latte, Everforest, Gruvbox, Kanagawa, Matte Black, Nord, Osaka Jade,
  Ristretto, Rose Pine and Tokyo Night. Rose Pine and Catppuccin Latte
  are the light ones.

Pick one in Options under APPEARANCE. The terminal mixer reads the same
folders and the same saved choice, on its own Options tab, so an appearance
picked in one is the appearance in the other. A terminal has no gradients,
images or corner radii, so it takes a gradient at its first stop. It also
reads `controls`: console faders get a three-cell cap, cap keys get
brackets and lamp LEDs get a square face rather than a round one. The pixel sizes and segment counts
remain the window's; the terminal sizes these drawings to its cells. A
meter is a solid bar in eighth blocks whichever appearance is named, and
its colour blends from `Ox.Meter.Fill` through `Ox.Meter.Warning` to
`Ox.Meter.Hot` along the scale, anchored at the warning and hot levels.
The terminal requires `schema: 1`, as do the window and Deck. A missing,
invalid or newer schema leaves the whole terminal palette at its defaults.
Within a supported document, malformed terminal palette fields keep their
default while valid fields still apply. Names use the same bounded,
control-free text in the skin picker and the loaded palette.
The terminal checks the 256 KiB document limit each time it reads a file,
including after the picker has listed it. A file that exceeds the limit or
grows during that read keeps the default palette; repairing it allows the
next load to use it again.

The choice is saved in
`~/.config/openxlr/ui.json` as `"skin": "<id>"` and nowhere else: it is not
part of the mixer layout, the daemon's preferences or a profile, and
changing it never touches audio. Windows that are already open repaint;
nothing is rebuilt and nothing is restarted. A skin with a layout moves the
header and the sections that are already there; their controls are the same ones.
If the terminal cannot read `ui.json`, it uses the default appearance.
A malformed document, including duplicate keys or a value other than a
JSON object, is left untouched when saving a terminal skin choice. Repair
the file before saving a new choice; settings unrelated to the skin are
preserved in a valid object. The window does the same. It wears the chosen
skin for that run, leaves a `ui.json` it cannot read untouched, and says on
the line at the bottom of Options why the choice was not saved.

If a skin ever makes something unreadable, start the window once with

```sh
OPENXLR_SKIN=default openxlr
```

which ignores the saved choice for that run and leaves it saved, so you
can pick another one from Options. With `default` the window wears dark
Material whatever the saved mode says. Options says when a run was started
that way, and choosing a skin there ends the override for the rest of the
run. Any id works there, not only `default`, which is the quickest
way to try a skin without selecting it.

## Material's mode

Material has a **Mode** beside the skin picker in Options: **System**,
**Light** or **Dark**. Light and Dark hold that palette. System, the
default, follows the desktop's light or dark preference as the
[settings portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Settings.html)
reports it, and repaints the open windows when the desktop switches. A
desktop that states no preference, or no portal at all, gets the dark
palette.

On a desktop that prefers light, a window on Material with no saved mode
therefore opens in the light palette. A window wearing any other skin looks
as it did. Deck, the Omarchy palettes and installed skins are one set of
values each and keep them in every mode, and the Mode picker is greyed out
while one of them is chosen. The mode is kept for when Material is chosen
again.

The light palette is skin data in the same format as any skin, compiled
into the application and read the same way. It is not a skin of its own and
does not appear in the picker. Unlike the dark default it colours its meter
zones: the fill is green, the warning zone amber and the hot zone red.

The mode is saved in `~/.config/openxlr/ui.json` as
`"appearanceMode": "system"`, `"light"` or `"dark"`, beside the skin and,
like it, nowhere else. A missing value, an unknown name or a value of the
wrong type reads as System and leaves the rest of the file's preferences
alone. A choice goes on at once even when it cannot be saved, and Options
says why on its preference line.

The terminal mixer reads the same key and offers the same three modes as
**Material mode** on its Options tab, changed with Left and Right. In System
it reads the portal's `color-scheme` with `gdbus` and watches it for
changes, only while Material is the skin and System is the mode. Without a
session bus or without `gdbus` it stays dark. `--skin` and `OPENXLR_SKIN`
hold their skin, and dark Material, until a skin or mode is chosen in
Options.

## Stream Deck colours

The OpenDeck plugin uses the same saved skin for key surfaces, lettering,
indicators, dial icons and needles, and meter colours and thresholds. Built-in
palettes travel with the plugin; regenerate `skin-palettes.json` with
`python3 tools/deck-skins.py` after changing embedded skins or colour defaults.
CI checks the generated table and tests its defaults against `SkinTokens`.

The window publishes its realised palette to the private
`~/.config/openxlr/deck-palette.json` when it applies a skin. This also carries
the effective light or dark colours without a second desktop-theme connection
in the plugin. The file is an atomic, schema-1 object containing `skin`, the window process `pid` and
`tokens`, with only the twelve colour and meter values the keys need. It does
not change `ui.json` or audio profiles. A write failure appears in the skin's
errors and leaves the plugin's file-based fallback available.

Without a matching palette from a running window, the plugin reads the saved `skin` from
`ui.json` and the selected package from the usual XDG skin roots, user first,
then system, then the embedded palette. Unknown ids and invalid documents fall
back to Material. `OPENXLR_SKIN` in the plugin's own environment overrides the
saved choice and the published palette for that run. Empty variables are unset.
File events follow atomic saves and newly created or removed skin folders;
unchanged colours cause no redraw. Meter updates perform no filesystem work.

The Deck preserves its owned glyphs and key geometry. Gradients on surfaces
use their first stop; images and control templates stay in the window. Hex
RGB and alpha-first ARGB colours are accepted, as are black, white,
transparent, red, green, blue, yellow, gray and grey. Other named colours use
the token default unless the window publishes their resolved colour. Only
entries declared in the named-colour table qualify; inherited JavaScript
properties such as `constructor` or `__proto__` are not colours, including
when used as gradient stops. Meter
and LED colours are flat; invalid or crossed meter thresholds use the defaults.
Documents are limited to 256 KiB and must be regular files. Up to 32 XDG data
roots are considered. No document can supply SVG, code or an asset URL.

## Getting started

A skin can be eight lines. Put this in
`~/.local/share/openxlr/skins/my-skin/skin.json`:

```json
{
  "schema": 1,
  "name": "My skin",
  "tokens": {
    "Ox.Window.Background": "#101418",
    "Ox.Text.Primary": "#eef2f6"
  }
}
```

Open Options, press Reload beside the picker, and choose "My skin
(installed for you)". Everything the file does not mention keeps the
default appearance, so you can start with two values and grow the file.
Anything OpenXLR could not use is listed under the picker, with the token
name and the reason.

Edit, press Reload, look. Reload rereads the folders, so a skin you have
just edited, added or removed is picked up without restarting the window.

### Start from a working skin

The shipped appearances are compiled in, so a package installs none of
them on disk. Copy the one you want to start from out of the repository:

```sh
mkdir -p ~/.local/share/openxlr/skins/my-skin
# a small starting point
curl -fLo ~/.local/share/openxlr/skins/my-skin/skin.json \
  https://raw.githubusercontent.com/emaspa/openxlr/main/docs/examples/skins/example/skin.json
# or the whole Deck appearance, which uses every feature of the format
curl -fLo ~/.local/share/openxlr/skins/my-skin/skin.json \
  https://raw.githubusercontent.com/emaspa/openxlr/main/src/OpenXLR.UI/Assets/Skins/opendeck/skin.json
```

Any of the other appearances works the same way. They are the `skin.json`
files under
[src/OpenXLR.UI/Assets/Skins](../src/OpenXLR.UI/Assets/Skins), one folder
per id. A palette skin sets colours and nothing else, so it is a shorter
read than Deck.

Then change `name` (and `id`, if you keep one) so your copy is not
confused with the original, and edit from there. In a source checkout the
starting point is at
[docs/examples/skins/example/skin.json](examples/skins/example/skin.json).

The example skin and every shipped one are read by the test suite on every
build, so they are always files this version accepts.

### Editor help

[skin.schema.json](skin.schema.json) is a JSON Schema for the format. An
editor that understands it will complete token names and flag a value of
the wrong shape as you type. Point at it from the file itself:

```json
{
  "$schema": "https://raw.githubusercontent.com/emaspa/openxlr/main/docs/skin.schema.json",
  "schema": 1,
  "name": "My skin",
  "tokens": {}
}
```

OpenXLR ignores `$schema`, and the schema is checked against the token
list and the limits in the code by the test suite, so the two cannot drift
apart. The schema is a writing aid: OpenXLR itself decides what it
accepts, and this document describes that.

## Where skins are found

| Where | Path |
|---|---|
| yours | `$XDG_DATA_HOME/openxlr/skins/<id>/skin.json` (`~/.local/share` when unset) |
| this system | each of `$XDG_DATA_DIRS` plus `/openxlr/skins/<id>/skin.json` (`/usr/local/share:/usr/share` when unset) |
| shipped | compiled into the application |

The folder name is the skin's id. An id found in more than one place is
taken from the first row that has it, so your own copy shadows a system
one and a system one shadows a shipped one. Ids are lower-case letters,
digits, dot, dash and underscore, start with a letter or a digit, and are
at most 64 characters. At most 200 skins are read in one scan.

The id is what is stored; the name is what is read. `ui.json` holds the
id, so renaming a skin's `name` never moves the choice, and a folder you
rename is a different skin as far as the saved choice is concerned.
Material is always in the picker and always first; the rest follow by
name.

"Reload" in Options reads the folders again without restarting.

## The file

```json
{
  "schema": 1,
  "id": "my-skin",
  "name": "My skin",
  "description": "One line about it.",
  "author": "Someone",
  "controls": { "fader": "console" },
  "tokens": {
    "Ox.Card.Background": "#1d2027",
    "Ox.Text.Primary": "#e6e9f0",
    "Ox.Card.CornerRadius": 8
  }
}
```

| Field | Required | What it is |
|---|---|---|
| `schema` | yes | the format version this file is written for, `1` |
| `id` | no | the id the author intended; the folder name wins when they disagree, and Options says so |
| `name` | no | what the picker shows; the id when it is missing |
| `description` | no | one line, shown under the picker |
| `author` | no | shown under the picker as "By ..." |
| `controls` | no | one of OpenXLR's own appearances per control, see [Controls](#controls) |
| `tokens` | yes | the appearance values, see [The tokens](#the-tokens) |
| `layout` | no | where the header and the window's sections go, see [Layout](#layout) |

`name`, `description` and `author` are cut at 400 characters, and control
characters in them are dropped.

There is no `version` field and no `license` field: OpenXLR reads none, so
put a version in the `description` if you want one, and licensing in a
file beside `skin.json`. Files in the folder other than `skin.json` and
the images the tokens name are ignored by OpenXLR and are yours to use,
which is where a `LICENSE`, a `README` or your source artwork can live.

Nothing in a skin identifies the machine it runs on, and no part of it is
ever sent anywhere.

### How a skin is read

The whole file is refused, and the previous appearance stays on, when:

- the folder name is not a usable id;
- `skin.json` is larger than 256 KB, or is not valid JSON, or is not a
  JSON object;
- `schema` is missing, is not a number, or is below 1;
- `schema` is above the version this OpenXLR reads, which is `1`. The
  message says to update OpenXLR. A newer OpenXLR keeps reading schema 1
  files.

Anything else is a complaint about one value: it is reported, that value
is dropped, and the rest of the file still applies. A token name this
version does not have, a control or an appearance it does not have, a
value of the wrong type, a number outside its bounds, an unreadable or
oversized image: each of those costs you that one value and nothing else.
Every token the file does not mention keeps the default. A partial skin is
an overlay, not a half-painted window, which is also what makes a skin
written for a later OpenXLR usable here as far as this version
understands it.

The `layout` block is the one exception: it is taken whole or not at all.
A fault anywhere in it drops the whole block, and only the block, as
[Layout](#layout) describes.

Unknown fields at the top level of the file are ignored without comment,
which is what lets `$schema` sit there. Unknown names inside `tokens` and
`controls` are reported, since those are almost always a typo.

The complaints appear in Options under the picker. The mixer window never
shows them.

## Controls

A skin does not supply a template, a style or any markup. What it can do is
choose, for each control below, one of the appearances OpenXLR itself
draws, and then tint and size it with the tokens further down.

```json
"controls": {
  "button": "cap",
  "fader": "console",
  "meter": "segmented",
  "led": "lamp",
  "mute": "cap"
}
```

| Control | Appearances | What it is |
|---|---|---|
| `button` | `flat` (default), `cap` | `flat` is the framework's button. `cap` is the raised key: the control paints its own face and `Ox.Cap.Bevel` lays the gloss over it. One drawing serves the plain button, the toggle and the dropdown button, so a skin cannot raise one and leave the next one flat. Corner shape comes from `Ox.Control.CornerRadius`, so a skin can make every key square |
| `fader` | `flat` (default), `console` | `flat` is the framework's slider. `console` is OpenXLR's own: an inset groove, a filled section up to the value, and a machined cap with a grip line across it. It is drawn for horizontal faders, which is all the window has; a vertical one keeps the framework's |
| `meter` | `continuous` (default), `segmented` | `continuous` is one bar that grows. `segmented` is a ladder of cells that light one after another, as on a console |
| `led` | `flat` (default), `lamp` | `flat` is a filled dot. `lamp` puts the dot in a bezel ring and gives it a halo while it is lit |
| `mute` | `flat` (default), `cap` | `flat` is the framework's button. `cap` is the same raised key the `button` slot draws, so the mute matches the controls around it |

A control a skin does not mention keeps the appearance the window has
always had. The `button` slot covers the plain button, the toggle and the
dropdown button together on purpose: a skin picks a key face, and every
key wears it.

The console fader keeps the framework's own slider underneath. The
template hands the track, the two groove halves and the cap to the slider
under the names it looks for, so dragging, clicking the groove, the
keyboard, tick snapping and display scaling are unchanged. A skin cannot
change what a control does, only how it is drawn.

Some tokens are read only by one of these appearances and do nothing under
the others. They are marked in the tables below: the grip and track radius
under `fader: console`, the segment count and gap under `meter:
segmented`, the bezel, glow and core scale under `led: lamp`, and
`Ox.Cap.Bevel` and `Ox.Control.BorderThickness` under the `cap` faces.

A lamp draws entirely inside the box `Ox.Led.Size` gives it: the lit core
fills `Ox.Led.CoreScale` of it, the bezel is stroked outside the core, and
the halo fills what is left up to the edge. Nothing is drawn beyond the
box, so a lamp at the edge of a rounded card is never cut. Give the lamp a
larger `Ox.Led.Size` and a smaller `Ox.Led.CoreScale` for more halo, not a
bigger glow. The halo is drawn as soft rings rather than a real blur,
since the drawing context has none; at the size these lamps are drawn it
reads the same.

## Values

A token holds one of six kinds of value: a **brush**, a **colour** (a
brush that must stay flat), a **number**, a **radius**, a **thickness**, a
**family** or a **weight**. The kind is in the tables below, and a value
of the wrong kind is refused with a message naming the token.

**A brush** is a colour string, or an object:

```json
"Ox.Card.Background": "#1d2027",
"Ox.Badge.Background": "#cc1d2027",
"Ox.Divider": "slategray",
"Ox.Window.Background": {
  "type": "linear", "from": [0, 0], "to": [0, 1],
  "stops": [{ "offset": 0, "color": "#262626" }, { "offset": 1, "color": "#151515" }]
},
"Ox.Control.Background": {
  "type": "radial", "from": [0.5, 0.25], "to": [0.5, 0.5], "radius": 0.9,
  "stops": [{ "offset": 0, "color": "#4a4a4a" }, { "offset": 1, "color": "#333333" }]
},
"Ox.Tile.Background": {
  "type": "image", "source": "faceplate.png", "stretch": "uniformToFill", "opacity": 1
}
```

A colour string is `#rgb`, `#rrggbb`, `#aarrggbb` or a colour name.
`"type": "solid"` with a `color` is the same thing written as an object.

`from` and `to` are relative to the box being painted: `[0, 0]` its top
left corner, `[1, 1]` its bottom right, and each number is between -2 and
3 so a gradient may start outside the box. For a linear gradient `from`
defaults to `[0, 0]` and `to` to `[0, 1]`, which is top to bottom. For a
radial gradient `to` is the centre and `from` the highlight, both
defaulting to `[0.5, 0.5]`, and `radius` is between 0 and 4, 0.5 by
default. A gradient needs between 2 and 16 `stops`, each with a `color`
and an `offset` between 0 and 1.

An image is described under [Images](#images).

**A number** is a plain JSON number inside the bounds in the tables.

**A radius** is one number, applied to all four corners, or four: top
left, top right, bottom right, bottom left.

**A thickness** is one number, or two (left and right, then top and
bottom), or four (left, top, right, bottom).

**A family** is the name of a font already installed on the machine, up to
120 characters. A skin never loads a font file, so a family with a `#` or
a URL in it is refused. Name a family the machine is likely to have, or
one your skin's readme tells the user to install; an unknown family falls
back to the system default without an error.

**A weight** is `"Thin"`, `"ExtraLight"`, `"Light"`, `"Normal"`,
`"Medium"`, `"SemiBold"`, `"Bold"`, `"ExtraBold"`, `"Black"` and the other
names the toolkit knows, in any case, or a number from 1 to 1000.

## The tokens

Every value the window has is below. The **default** column is the
appearance OpenXLR ships with: a `#rrggbb` or `#aarrggbb` colour, a
number, or `framework`, which means OpenXLR sets nothing there and the
toolkit's own value stays. **Range** is the bounds a number, a radius or a
thickness is held to; a value outside them is refused and the default
stays.

A `framework` value is the toolkit's, and the toolkit follows the
desktop's light or dark setting. A skin that wants to look the same on
every desktop sets those tokens rather than leaving them; a skin that sets
only the surfaces and the text inherits whatever the desktop asked for
underneath.

The tables are checked against the code by the test suite, so a token
without a row here fails the build.

### Surfaces

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Window.Background` | brush | #16181d |  | the ground behind every window |
| `Ox.Card.Background` | brush | #1d2027 |  | a section card: INPUTS, HEADPHONES, an Options group |
| `Ox.Card.BorderBrush` | brush | #00000000 |  | that card's border |
| `Ox.Card.BorderThickness` | thickness | 0 | 0 to 16 | that border's width |
| `Ox.Card.CornerRadius` | radius | 8 | 0 to 48 | that card's corners |
| `Ox.Tile.Background` | brush | #23262f |  | a tile on a card: a channel strip, a mix master, an application chip |
| `Ox.Tile.BorderBrush` | brush | #00000000 |  | that tile's border |
| `Ox.Tile.BorderThickness` | thickness | 0 | 0 to 16 | that border's width |
| `Ox.Tile.CornerRadius` | radius | 6 | 0 to 48 | that tile's corners |
| `Ox.Dialog.Background` | brush | #1d2027 |  | the small confirmation and rename dialogs |
| `Ox.Danger.Background` | brush | #a03434 |  | a button that deletes something |
| `Ox.Danger.Foreground` | brush | #f6dede |  | that button's label |
| `Ox.Badge.Background` | brush | #2a2e38 |  | the small format badge beside a plugin name |
| `Ox.Divider` | brush | #2d313c |  | the rule under a plugin control group |

### Text

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Text.Primary` | brush | #e6e9f0 |  | names and readouts |
| `Ox.Text.Secondary` | brush | #8b93a7 |  | labels and section headings |
| `Ox.Text.Muted` | brush | #6b7285 |  | explanatory lines |
| `Ox.Text.Detail` | brush | #b9bfcc |  | paragraphs in About, release notes, plugin group headings |
| `Ox.Text.Warning` | brush | #e0a05a |  | something went wrong |
| `Ox.Text.Alert` | brush | #e0a030 |  | something needs attention |
| `Ox.Text.Info` | brush | #5ba8f5 |  | an update is available |
| `Ox.Link.Foreground` | brush | #5ba8f5 |  | a link |
| `Ox.Link.ForegroundPointerOver` | brush | #8cc4ff |  | a link under the pointer |

A colour given to one channel or mix in the layout editor paints that
item's name and icon in place of `Ox.Text.Primary`, in the window, the
terminal mixer and on Stream Deck keys, whatever the skin. It is part of
the mixer layout, not of a skin
([Saved mixer layout](mixer-layout.md#appearance)).

### Typography

| Token | Kind | Default | Range | What it sets |
|---|---|---|---|---|
| `Ox.Label.FontSize` | number | 12 | 8 to 24 | section headings and labels |
| `Ox.Label.FontWeight` | weight | SemiBold |  | those same headings and labels |
| `Ox.Title.FontSize` | number | 20 | 10 to 40 | the window's own title line |
| `Ox.Title.FontWeight` | weight | Bold |  | that title line |
| `Ox.Strip.FontWeight` | weight | SemiBold |  | the name over a channel strip or a mix |
| `Ox.Value.FontFamily` | family | monospace |  | the numeric readouts |
| `Ox.Font.Family` | family | framework |  | the font the buttons, labels and dropdowns use |

### Indicators

These six reach the window through code as well as through markup, so they
must be flat colours. A gradient or an image in one of them is refused
with a message saying why.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Led.On` | colour | #3ecf7a |  | a connected device, a plugin that is processing |
| `Ox.Led.Off` | colour | #4a4f5c |  | a device that is not there |
| `Ox.Led.Alert` | colour | #ff3c4e |  | a plugin bypassed or failed |
| `Ox.Meter.Fill` | colour | #3ecf7a |  | the quiet end of the scale |
| `Ox.Meter.Warning` | colour | #3ecf7a |  | the middle of the scale |
| `Ox.Meter.Hot` | colour | #3ecf7a |  | the loud end of the scale |

The rest of an indicator's appearance is shape and size:

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Led.Size` | number | 9 | 4 to 40 | the box a lamp or a dot is drawn in, both appearances |
| `Ox.Led.Bezel` | brush | #00000000 |  | the ring around the core, `led: lamp` only |
| `Ox.Led.BezelThickness` | number | 0 | 0 to 6 | that ring's width, `led: lamp` only |
| `Ox.Led.Glow` | number | 0 | 0 to 1 | the halo while the lamp is lit, 0 for none, `led: lamp` only |
| `Ox.Led.CoreScale` | number | 1 | 0.1 to 1 | how much of the box the lit core fills, `led: lamp` only |
| `Ox.Meter.Track` | brush | #14161b |  | the groove a meter sits in, and an unlit cell |
| `Ox.Meter.CornerRadius` | radius | 2 | 0 to 48 | the meter's corners, and each cell's |
| `Ox.Meter.Height` | number | 4 | 2 to 24 | how tall a meter is |
| `Ox.Meter.Segments` | number | 16 | 2 to 64 | how many cells, `meter: segmented` only |
| `Ox.Meter.SegmentGap` | number | 2 | 0 to 8 | the gap between cells in pixels, `meter: segmented` only |

Cells are laid out on whole pixels, and a ladder too narrow to draw is
drawn as one continuous bar instead, so a segmented meter never comes out
as a smear.

### The meter's scale

A meter reads **RMS dBFS**: 0 is -60 dBFS and below, 1 is 0 dBFS, so a
reading of `x` is `60 * x - 60` decibels. It is an RMS meter, not a peak
meter, which is why the loud end starts lower than you might expect.

| Token | Kind | Default | Range | Where it sits |
|---|---|---|---|---|
| `Ox.Meter.WarningLevel` | number | 0.7 | 0 to 1 | -18 dBFS, the usual alignment level |
| `Ox.Meter.HotLevel` | number | 0.9 | 0 to 1 | -6 dBFS, where an RMS reading leaves no headroom for the peaks |

The colour belongs to the **place on the scale**, not to the reading. The
quiet end is drawn in `Ox.Meter.Fill` however loud the signal gets, and
the warning and top colours appear only on the parts of the bar the signal
has actually reached. A meter never repaints itself end to end. A
continuous meter draws the lit part as one bar: only its two ends are
rounded, and the zone edges sit on whole pixels, so where the colours are
the same the bar is unbroken. A cell of
a segmented meter is coloured by its own place on the scale for the same
reason. A hot level below the warning level is read as equal to it, so the
zones can never cross.

All three colours default to the same green, so the shipped appearance
draws no zones and looks exactly as it always has. A skin that wants a
hi-fi ladder sets `Ox.Meter.Warning` and `Ox.Meter.Hot` and gets the
default thresholds without having to state them.

### Mute buttons

A mute button is checked when the channel is muted, so its colours are
named for what the audio is doing rather than for the toggle, and they are
its own rather than the ordinary control's.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Mute.Background` | brush | #1e5c3a |  | the face while audio passes |
| `Ox.Mute.Foreground` | brush | #d9efe2 |  | its label while audio passes |
| `Ox.Mute.BackgroundPointerOver` | brush | #27754b |  | the face under the pointer |
| `Ox.Mute.BackgroundChecked` | brush | #a03434 |  | the face when muted |
| `Ox.Mute.ForegroundChecked` | brush | #f6dede |  | its label when muted |
| `Ox.Mute.BorderBrush` | brush | #00000000 |  | its ring |
| `Ox.Mute.BorderThickness` | thickness | 0 | 0 to 16 | that ring's width |

### Faders

Under `fader: flat` these feed the framework's own slider; under
`fader: console` they feed OpenXLR's. Either way the slider's behaviour is
the framework's.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Fader.Track` | brush | framework |  | the part of the groove above the value |
| `Ox.Fader.Fill` | brush | framework |  | the part below the value |
| `Ox.Fader.TrackHeight` | number | 2 | 1 to 24 | how thick the groove is |
| `Ox.Fader.TrackCornerRadius` | radius | 1 | 0 to 48 | the groove's corners, `fader: console` only |
| `Ox.Fader.Thumb.Background` | brush | framework |  | the cap |
| `Ox.Fader.Thumb.BorderBrush` | brush | #00000000 |  | the cap's ring |
| `Ox.Fader.Thumb.BorderThickness` | thickness | 0 | 0 to 16 | that ring's width |
| `Ox.Fader.Thumb.Width` | number | 22 | 4 to 64 | the cap across the groove |
| `Ox.Fader.Thumb.Height` | number | 10 | 4 to 64 | the cap along the groove |
| `Ox.Fader.Thumb.CornerRadius` | radius | 2 | 0 to 48 | the cap's corners |
| `Ox.Fader.Thumb.Grip` | brush | #00000000 |  | the line across the cap, `fader: console` only |
| `Ox.Fader.Thumb.GripLength` | number | 6 | 0 to 64 | how long that line is, `fader: console` only |

A vertical fader swaps the thumb's width and height, so one pair of values
sizes both.

### Buttons, toggles and dropdowns

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Control.Background` | brush | framework |  | a button, a toggle, a dropdown |
| `Ox.Control.BackgroundPointerOver` | brush | framework |  | one under the pointer |
| `Ox.Control.BackgroundPressed` | brush | framework |  | one held down |
| `Ox.Control.BackgroundChecked` | brush | framework |  | a toggle that is on |
| `Ox.Control.BackgroundDisabled` | brush | framework |  | one that cannot be used |
| `Ox.Control.Foreground` | brush | framework |  | the lettering on a button or a dropdown |
| `Ox.Control.ForegroundChecked` | brush | framework |  | a toggle's lettering when on, unless `Ox.Toggle.ForegroundChecked` is set |
| `Ox.Control.ForegroundDisabled` | brush | framework |  | the lettering when the control cannot be used |
| `Ox.Control.BorderBrush` | brush | framework |  | the ring |
| `Ox.Control.BorderBrushPointerOver` | brush | framework |  | the ring under the pointer |
| `Ox.Control.BorderBrushDisabled` | brush | framework |  | the ring when the control cannot be used |
| `Ox.Control.BorderThickness` | thickness | 1 | 0 to 16 | the ring's width on a raised key, `button: cap` and `mute: cap` only |
| `Ox.Control.CornerRadius` | radius | framework | 0 to 48 | the corners of every key, and of the boxes the framework rounds |
| `Ox.Control.DisabledOpacity` | number | 1 | 0.2 to 1 | how far a key fades when it cannot be used, 1 for no fade |
| `Ox.Ghost.Background` | brush | #00000000 |  | the face behind a button a view keeps out of the way: the header actions, the rows inside a flyout |
| `Ox.Cap.Bevel` | brush | #00000000 |  | the gloss over every raised key face, `button: cap` and `mute: cap` only; usually a gradient, and one brush for all of them so the keys cannot drift apart |

A toggle's lettering can say what the toggle is doing, which is how the
Deck skin works: the key stays black and the legend lights up. These take
precedence over `Ox.Control.Foreground` and `Ox.Control.ForegroundChecked`
for toggles, so a skin that sets only the older tokens behaves as it did.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Toggle.Foreground` | brush | framework |  | a toggle that is off |
| `Ox.Toggle.ForegroundChecked` | brush | framework |  | a toggle that is on |
| `Ox.Toggle.ForegroundDisabled` | brush | framework |  | a toggle that cannot be used |
| `Ox.Toggle.BorderBrushChecked` | brush | framework |  | the ring of a toggle that is on |
| `Ox.Bypass.Foreground` | brush | #ffffff |  | the plugin bypass key while the plugin is processing |
| `Ox.Bypass.ForegroundBypassed` | brush | #ffffff |  | the same key while the plugin is bypassed |
| `Ox.Check.Foreground` | brush | #e6e9f0 |  | the label beside a checkbox, which is running text rather than a lit legend |

The bypass key reads the other way round from an ordinary toggle, because
its checked state is the one that stops the audio. Its two colours default
to the plain lettering the framework uses, so the key stays readable in an
appearance that does not colour by state.

### Text boxes, flyouts and the accent

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Input.Background` | brush | framework |  | a text box |
| `Ox.Input.Foreground` | brush | framework |  | what you type |
| `Ox.Input.BorderBrush` | brush | framework |  | its ring |
| `Ox.Flyout.Background` | brush | framework |  | a dropdown's list and a flyout |
| `Ox.Tooltip.Background` | brush | framework |  | a tooltip |
| `Ox.Tooltip.Foreground` | brush | framework |  | its text |
| `Ox.Tooltip.BorderBrush` | brush | framework |  | its border |
| `Ox.Accent` | colour | framework |  | the tick in a checkbox and the selection in a list; must be flat, since the theme reads it as a colour |

A checkbox, a repeat button inside a fader groove and the toggle that
opens a section header are not keys: they keep their own appearance
whichever button variant is chosen.

A control token a skin does not set keeps the framework's own value, so a
skin that only recolours the cards leaves buttons exactly as they are.

### Audio flow window

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Flow.Background` | brush | #242624 |  | the ground behind the graph |
| `Ox.Flow.Card` | brush | #333633 |  | a node |
| `Ox.Flow.CardSelected` | brush | #414541 |  | the node whose path is being traced |
| `Ox.Flow.Text` | brush | #f0f2ef |  | a node's name and the window's title |
| `Ox.Flow.TextDim` | brush | #a3aaa3 |  | a node's second line, the legend and the selected node's outline |
| `Ox.Flow.Input` | brush | #20c4d1 |  | an edge from an input to a channel |
| `Ox.Flow.Channel` | brush | #c958e5 |  | an edge from a channel to a mix |
| `Ox.Flow.Output` | brush | #20d49a |  | an edge from a mix to an output |
| `Ox.Flow.Muted` | brush | #757d75 |  | an edge that is muted or not in the traced path |
| `Ox.Flow.NodeHoverBackground` | brush | #414541 |  | a node under the pointer |
| `Ox.Flow.NodeHoverBorderBrush` | brush | #717971 |  | that node's outline |

### Mixer control sizing

These size the main mixer's controls and strips. Options offers two
sizings, saved in `ui.json` as `"touchControls"`: Standard wears the values
below, and Touch raises the five control minimums to 44 units, a channel
strip to 180 and a mix tile to 280. A skin value larger than that is kept in
both. While Touch is on, or while a skin sets `Ox.Mixer.ControlMinSize`, a
slider is also made at least as tall as the control minimum and the
`Ox.Fader.Thumb.Height` cap, so an enlarged cap stays inside its row. The
other windows and the plugins' own editors keep their sizes.

| Token | Kind | Default | Range | What it paints |
|---|---|---|---|---|
| `Ox.Mixer.ControlMinSize` | number | framework | 0 to 64 | the smallest width and height of a mixer button, dropdown, check box, text box, slider and fader cap |
| `Ox.Mixer.SliderMinHeight` | number | framework | 0 to 96 | the smallest height of a mixer slider |
| `Ox.Mixer.DeviceSliderHeight` | number | 30 | 30 to 96 | the height of a slider in the interface's control rows |
| `Ox.Mixer.SmallControlMinSize` | number | 0 | 0 to 64 | the smallest size of the compact controls: the interface rows' buttons and sliders, the tile headers and their buttons, the Arrange buttons and drag handles, the gain lock, the inserts buttons and a send's mute |
| `Ox.Mixer.InsertControlMinSize` | number | 24 | 16 to 64 | the smallest width and height of an insert row's own process, bypass and controls buttons |
| `Ox.Mixer.ChannelWidth` | number | 132 | 132 to 400 | the width of a submixer channel strip |
| `Ox.Mixer.MixWidth` | number | 232 | 232 to 500 | the width of a mix master tile |

## Images

An image comes from the skin's own folder:

```json
"Ox.Window.Background": {
  "type": "image", "source": "art/faceplate.png", "stretch": "uniformToFill", "opacity": 0.8
}
```

`stretch` is `fill`, `uniform`, `uniformToFill` (the default) or `none`,
and `opacity` is between 0 and 1, 1 by default.

The rules a `source` is held to:

- it is a relative path inside the skin's folder, with forward slashes, no
  leading slash, no `.` or `..` segment, no colon and no URL. Nothing is
  ever fetched over the network;
- a symbolic link whose target is outside the folder is not followed;
- the file is a PNG or a JPEG, at most 4 MB, at most 4096 pixels a side,
  and a skin's images together decode to at most 16 megapixels;
- a skin may name at most 32 images;
- a name is at most 256 characters.

The size is read from the file's header before anything is decoded, so a
small file that claims to be enormous is refused rather than unpacked. An
image that breaks one of these rules is reported in Options and that one
token keeps its default.

Images are paintable wherever a brush is: a faceplate behind the window, a
texture on a card, a metal face on a key. They are not available to the
six flat indicator colours.

## Layout

A skin can also say where the main window's parts go: the header and the
five sections, placed in a tree of stacks, grids and docks, with a few
options for each of them. That is the optional `layout` object, beside
`tokens` and `controls`. It moves what the application builds; it never
adds a control, a binding or a picture of its own.

A complete skin that puts the header across the top, INPUTS beside the
submixer, the headphones and the monitor under INPUTS, and the
applications along the bottom, folded at first:

```json
{
  "schema": 1,
  "id": "console-grid",
  "name": "Console grid",
  "tokens": {
    "Ox.Card.Background": "#1d2027"
  },
  "layout": {
    "version": 1,
    "main": {
      "kind": "grid",
      "id": "desktop",
      "spacing": 12,
      "columns": [{ "size": 640 }, { "star": 1 }],
      "rows": ["auto", "auto", "auto", "auto"],
      "children": [
        { "kind": "region", "region": "header", "row": 0, "column": 0, "columnSpan": 2 },
        { "kind": "region", "region": "inputs", "row": 1, "column": 0 },
        { "kind": "region", "region": "submixer", "row": 1, "column": 1, "rowSpan": 2 },
        {
          "kind": "stack",
          "id": "outputs",
          "orientation": "vertical",
          "row": 2,
          "column": 0,
          "children": [
            { "kind": "region", "region": "headphones" },
            { "kind": "region", "region": "monitor" }
          ]
        },
        { "kind": "region", "region": "applications", "row": 3, "column": 0, "columnSpan": 2 }
      ]
    },
    "regions": {
      "applications": { "visibility": "collapsed" },
      "header": { "actions": ["flow", "profiles"], "overflow": ["restart-daemon", "about"] }
    }
  }
}
```

And one that docks the header at the bottom, INPUTS down the left side,
the headphones and the monitor in a row along the top and the submixer in
what is left, with the applications hidden until you show them:

```json
{
  "schema": 1,
  "id": "docked",
  "name": "Docked",
  "tokens": {
    "Ox.Card.Background": "#20232a"
  },
  "layout": {
    "version": 1,
    "main": {
      "kind": "dock",
      "id": "window",
      "spacing": 8,
      "children": [
        { "kind": "region", "region": "header", "dock": "bottom" },
        { "kind": "region", "region": "inputs", "dock": "left" },
        {
          "kind": "stack",
          "id": "outputs",
          "orientation": "horizontal",
          "dock": "top",
          "children": [
            { "kind": "region", "region": "headphones" },
            { "kind": "region", "region": "monitor" },
            { "kind": "region", "region": "applications" }
          ]
        },
        { "kind": "region", "region": "submixer", "dock": "fill" }
      ]
    },
    "regions": {
      "inputs": { "width": 680 },
      "applications": { "visibility": "hidden" }
    }
  }
}
```

The block has a version of its own, `"version": 1`, separate from the
file's `schema`. A newer layout can then come along without making the
rest of the skin unreadable to an older OpenXLR.

| Field | Required | What it is |
|---|---|---|
| `version` | yes | the layout format this block is written for, `1` |
| `main` | no | the window's tree: a stack, a grid or a dock. Without it the sections keep the classic arrangement |
| `regions` | no | options for the header and the sections, by region id |

### The tree

`main` and every container inside it is a stack, a grid or a dock, and
every container has these fields:

| Field | Required | What it is |
|---|---|---|
| `kind` | yes | `"stack"`, `"grid"` or `"dock"` |
| `id` | yes | 1 to 32 lowercase letters, digits and hyphens, unique in the tree. Your own arrangement names its places by it |
| `spacing` | no | the gap between children in pixels, from 0 to 64; 8, the classic gap, when it is missing |
| `children` | yes | regions and containers; a container holds no more than 16 children |

A stack puts its children one after another:

| Field | Required | What it is |
|---|---|---|
| `orientation` | yes | `"vertical"` puts them one above the other, the classic window. `"horizontal"` puts them side by side in one row |

A grid puts its children in cells:

| Field | Required | What it is |
|---|---|---|
| `rows` | yes | 1 to 8 tracks, top to bottom |
| `columns` | yes | 1 to 8 tracks, left to right |

A track is `"auto"`, as large as what it holds; `{ "star": n }`, a share of
what the other tracks leave, with `n` from 0.1 to 10; or `{ "size": n }`, a
fixed size in pixels from 40 to 2000. Each child of a grid says where it
goes:

| Field | Required | What it is |
|---|---|---|
| `row` | yes | its row, counted from 0 |
| `column` | yes | its column, counted from 0 |
| `rowSpan` | no | how many rows it covers, 1 when it is missing, and never past the last row |
| `columnSpan` | no | how many columns it covers, 1 when it is missing, and never past the last column |

Two children never cover the same cell. A cell can stay empty.

A dock puts its children against its edges, in order, and the last one
fills what is left. Each child of a dock says which edge:

| Field | Required | What it is |
|---|---|---|
| `dock` | yes | `"top"`, `"bottom"`, `"left"`, `"right"` or `"fill"`. Exactly one child fills, and it is the last |

A child that is not a container is a region:

| Field | Required | What it is |
|---|---|---|
| `kind` | yes | `"region"` |
| `region` | yes | which part of the window goes here, by the ids below |

| Region | What it is |
|---|---|
| `header` | the header: Options, the interface status and the header actions |
| `inputs` | INPUTS: the gain, the input switches and the microphone inserts |
| `headphones` | HEADPHONES: the headphone levels and the direct monitor blend |
| `monitor` | MONITOR: the output picker and its volume |
| `applications` | APPLICATIONS: the running applications and their channels |
| `submixer` | SUBMIXER: the channel strips and the mix masters |

`main` places each of the five sections exactly once, and the header at
most once. A tree that leaves the header out sits under it. The header
stays at the top of the window, as wide as the window, and the tree goes
below it, as in the classic window. A stack of the five sections without
the header therefore looks the way it always has. A tree that places the
header puts it where it says.

A tree holds no more than 64 nodes, containers and regions together.
Containers nest at most 4 deep, `main` included, and a grid has at most 8
rows and 8 columns. Options in the header is always shown, so the way back
is always reachable wherever the header goes. The notes the application
shows, such as an update or a daemon left running across an upgrade, stay
above the tree: under the header when the tree leaves it out, at the top
of the window when the tree places it.

A section the window sets aside, such as INPUTS, HEADPHONES and
APPLICATIONS in the mini view, or MONITOR without the software mixer,
stays hidden wherever the skin puts it. A skin can hide a section; it can
never bring back one the window hides.

### Sizes

Wherever a container gives a region a width of its own, in a horizontal
stack, a grid cell or a dock's left or right side, the region is at least
600 pixels wide, the width every section is held to fit. A region in a
vertical stack, or along a dock's top or bottom, or filling it, takes the
container's width. A `size` column smaller than what its region needs
grows to fit, and a column of `auto` is as wide as its widest region.

When the tree needs more width than the window has, it scrolls sideways
instead of squeezing a region. With the header above the tree the header
keeps the window's width; with the header inside the tree it scrolls with
the rest. A horizontal stack at the top of the tree shares the window's
width among its regions; one inside another container gives each region
the width it needs.

Heights follow the contents, since the page scrolls downwards. A `size`
row is the least it takes and grows with what it holds, and a `star` row
is as tall as its contents, like an `auto` one. A region lines up along
the top of its cell or side, so a collapsed section is just its header and
does not stretch to the height of the one beside it.

### Region options

`regions` holds an object for each region it sets options for, by region
id, with only the options that region has:

| Option | Region | What it is |
|---|---|---|
| `visibility` | the five sections | `"visible"`, the default. `"collapsed"` folds the section the first time the window wears the skin, and after that the section stays as you leave it. `"hidden"` leaves it out of the window and out of the tab order until you show it from Arrange mode |
| `width` | the five sections | a preferred width in pixels, from 120 to 4000 |
| `height` | the five sections | a preferred height in pixels, from 120 to 4000 |
| `actions` | `header` | header action ids in the order the header shows them |
| `overflow` | `header` | header action ids to put in the ⋯ menu at the end of the header instead |

A preferred width applies where the region has a width of its own: in a
horizontal stack, a grid cell and a dock's left or right side. It never
goes below the 600 pixels a section needs, and in a vertical stack, or
along a dock's top or bottom, it is ignored and the section takes the
container's width. In a grid the region is that wide at the start of its
cell, and its column is at least that wide. A preferred height is the
least the section takes; it grows past it with its contents and never
cuts them off.

The header actions:

| Action | What it is |
|---|---|
| `profiles` | Profiles and its menu |
| `arrange` | the Arrange toggle |
| `flow` | Flow |
| `restart-daemon` | Restart daemon |
| `version` | the version number |
| `about` | About |

An action appears at most once across `actions` and `overflow`. One left
out of both keeps its usual place after the listed ones. Options is not
an action a skin places. It is always first in the header and always
shown. The actions in the ⋯ menu are the same controls, with the same
names for a screen reader; Profiles opens its own menu from there.

### How a layout is read

A layout is taken whole or not at all. Any of these drops the whole
`layout` block:

- `version` is missing, is not a whole number, or is not `1`. A newer
  version says to update OpenXLR;
- a property the object does not have, or one it needs and lacks;
- the same key twice in one object;
- a `kind` other than `stack`, `grid`, `dock` and `region`, or a region as
  `main`;
- a container `id` that is missing, is not 1 to 32 lowercase letters,
  digits and hyphens, or is used twice;
- an `orientation` other than `vertical` and `horizontal`;
- a `spacing` that is not a finite number from 0 to 64;
- `rows` or `columns` that are not 1 to 8 tracks, or a track that is not
  `"auto"`, a `star` from 0.1 to 10 or a `size` from 40 to 2000;
- a grid child without its `row` and `column`, one outside the grid, a
  span past the last track, or two children over one cell;
- a dock child without its `dock` edge, an edge other than the five, or a
  dock that does not end with exactly one `fill` child;
- a region id this OpenXLR does not have, a region placed twice, or a
  section left out;
- more than 16 children in a container, more than 64 nodes in the tree,
  or containers more than 4 deep;
- in `regions`, a region id this OpenXLR does not have, an option the
  region does not have, a `visibility` other than the three, a `width` or
  `height` that is not a finite number from 120 to 4000, or a header
  action that does not exist, is `options`, or is listed twice.

The complaint names where in the file the fault is, for example
`layout.main.children[2].region`, and appears in Options under the picker
with the others. The skin's tokens and controls still apply; the window
wears its colours in the classic arrangement.

Anything that reads skins only for their colours ignores `layout`: the
terminal mixer, the Omarchy bar plugin and the Stream Deck plugin. An older
OpenXLR window ignores it as well, since it skips top-level fields it does
not know, and wears the skin in the classic arrangement.

### Your own arrangement

Arrange moves sections under any skin. Where it keeps your moves depends
on the skin:

- A skin without a layout, Material and every shipped skin included, uses
  the order you arranged, kept in `ui.json` as `sectionOrder`, exactly as
  before skins had layouts. A section dropped on another lands before or
  after it.
- A skin with a layout starts from its own tree. Each region in the tree
  is a place, named by its container's id and its index in a stack or a
  dock (`window:2`), or by its container's id, row and column in a grid
  (`desktop:1,0`). Your arrangement says which section sits in which
  place. Dropping a section on another trades their places, in the same
  container or across containers. In a stack, dropping a section on its
  neighbour still lands it before or after, which is the same as trading
  places with it; dropped on a section further away, the two trade places.
  The header stays in the place the skin gives it.
- Under a skin with a layout, your moves are kept in `ui.json` under
  `skinArrangements`, one entry for each skin and layout version, holding
  only the places you changed and the hidden sections you showed. The 32
  most recent skins are kept. They never change `sectionOrder`.
  **Reset sections** returns to the skin's own places and visibility and
  forgets only that skin's entry.
- Switching back to a skin without a layout brings your `sectionOrder`
  back, and switching to the layout skin again brings back what you
  arranged under it.

The tree, the gaps and the sizes always come from the skin. If a skin
update changes its tree so that your entry names a place or a section the
tree no longer has, the whole entry is dropped rather than half applied,
the window says so once in the note under the header, and the skin's own
arrangement is used. OpenXLR never rewrites a skin. Collapsed sections stay
collapsed whichever skin is worn, as before; a skin's `"collapsed"` only
sets where a section starts the first time, recorded in `ui.json` under
`skinLayoutsSeen`.

## What a skin cannot do

- It cannot carry markup, a template, a style or code. It picks from the
  appearances listed under Controls and tints them. Apart from placing
  the header and the five sections under [Layout](#layout), ordering the
  header's actions and hiding a section the user can bring back, there is
  no way for a skin to add a control, move one inside its section, resize
  the window, or change what a control does.
- It cannot change the mixer layout, the mixer, the routing or anything
  the daemon owns. It sets the values in this document and nothing else.
- It cannot reach outside its own folder, and it cannot make a network
  request or load a font file.
- It cannot skin a plugin's own editor. Those windows belong to the
  plugin, are drawn by the plugin's own toolkit, and OpenXLR only hosts
  them. The generated controls OpenXLR draws for a plugin do follow the
  skin.
- It cannot change the OpenDeck plugin's key art. The plugin draws its own
  images on the device; the Deck skin borrows that visual language for the
  window, not the other way round.
- It cannot hold a light and a dark version of itself, or follow the
  desktop's preference. A skin is one set of values. Only Material has a
  light and a dark palette, chosen by its mode. The toolkit values a skin
  leaves at `framework` are the ones that follow the desktop.
- It cannot skin the tray icon, the desktop notification or any window
  another application draws.

## Colour, contrast and testing

The test suite holds every shipped appearance to a few rules, and a skin
you intend to share is worth checking against the same ones by eye:

- text stays readable on the surface it lands on, and an indicator stays
  distinguishable from the strip it sits on;
- state is never carried by hue alone. A key that cannot be used keeps its
  colours and fades instead, which is what `Ox.Control.DisabledOpacity` is
  for: if your skin letters an off toggle and an unavailable action in the
  same red, set it below 1 so the two are still told apart;
- a control is never made harder to hit than the shipped appearance makes
  it. Sizes are bounded, but a fader cap of 4 pixels or a 2 pixel meter is
  within the bounds and still a bad idea;
- the names a screen reader reads come from the window, not from the skin,
  and a focused control still shows its focus adorner. Nothing a skin can
  set changes either, but a face with no contrast against the focus ring
  hides it in practice.

Walk the windows before you share a skin: the mixer, Options, the audio
flow window, the plugin controls window, the mixer layout window, the
desktop keys dialog, a confirmation dialog, a tooltip
and a dropdown list. The pointer-over, pressed, checked and disabled
states are separate tokens, and an appearance that only sets the resting
ones looks broken as soon as the pointer moves.

## Sharing and installing

There is no archive installer. A skin is a folder you copy into place:

```sh
unzip my-skin.zip -d ~/.local/share/openxlr/skins/
```

as long as the archive holds one folder with `skin.json` in it. The folder
name becomes the id, so pick one that is unlikely to collide and say in
your readme what it is. A system-wide install is the same folder under
`/usr/share/openxlr/skins/`, where every user on the machine sees it and
their own copy of the same id shadows it.

To stop using a skin, pick another one in Options. To remove it, delete
its folder and press Reload. A folder that is gone while its skin is the
chosen one leaves the window on Material, and `ui.json` keeps the name
until you choose something else, so putting the folder back brings it
straight back.

To reset the appearance completely, choose Material and System in
Options, or close the window and delete the `"skin"` and
`"appearanceMode"` lines from `~/.config/openxlr/ui.json`. Those lines are
the whole of the appearance's presence on your machine, and nothing about
the mixer, the daemon or your audio devices is kept in that file at all, so
neither route can disturb what is playing.

## Adding a token

For the record, since the format is only as complete as the window: a
value the window hard-codes cannot be skinned. If something you want to
change has no token here, that is a missing token rather than a limit of
the format, and it is a small change to the source. See
[AGENTS.md](../AGENTS.md) and open an issue or a pull request.
