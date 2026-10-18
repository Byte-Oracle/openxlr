# Example skins

Each folder here is a whole skin, ready to copy. `example` is a short palette
to start a skin of your own from. `broadcast` is the Broadcast skin OpenXLR
ships, byte for byte: its palette, its control appearances and a layout that
puts the inputs in a rack down the left, Monitor A and Stream as two banks of
upright faders beside it and the monitor output under them. It is the
reference for writing a layout. The test suite reads both on every build, so
they are always files the current OpenXLR accepts. JSON has no comments, so
the explanation lives here and in [docs/skins.md](../../skins.md).

Install a skin by copying its folder to
`~/.local/share/openxlr/skins/<id>/`, where the folder name is the skin's id.
Copy it under a new id so your edits do not hide the shipped skin of the same
name:

```sh
mkdir -p ~/.local/share/openxlr/skins/my-desk
curl -fLo ~/.local/share/openxlr/skins/my-desk/skin.json \
  https://raw.githubusercontent.com/emaspa/openxlr/main/docs/examples/skins/broadcast/skin.json
```

Then change `id` to `my-desk` and `name` to something of your own, open
Options, press Reload beside the skin picker and choose it. Edit, press
Reload, look. The colours are under `tokens` and the arrangement is under
`layout`; [Layout](../../skins.md#layout) lists every container, region,
template and part a layout can name. Anything OpenXLR cannot use is listed
under the picker, and a fault in `layout` drops only the layout, so the
colours stay while you fix it. Your own Arrange moves and bank picks are kept
per skin; **Reset this skin's arrangement** in Options goes back to the file.
