#!/usr/bin/env python3
"""Export the built-in channel and mix icons for the Deck client.

The table lives in src/OpenXLR.Core/Mixing/LayoutIcons.cs, which the daemon,
the window and the terminal mixer compile; this writes the same names, text
characters and path data to the plugin so a key draws what the window draws.
"""
import argparse
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / 'src/OpenXLR.Core/Mixing/LayoutIcons.cs'
OUTPUT = ROOT / 'plugin/com.emaspa.openxlr.sdPlugin/icons.json'
NAME = re.compile(r'^[a-z][a-z0-9-]{0,31}$')
PATH = re.compile(r'^[MmLlHhVvCcSsQqTtAaZz0-9eE.,+\- ]+$')


def generate():
    source = SOURCE.read_text(encoding='utf-8')
    glyphs = re.search(r'Glyphs \{ get; \} = \[(.*?)\];', source, re.S)
    if not glyphs:
        raise ValueError('LayoutIcons.Glyphs not found')
    table = {
        'glyphs': re.findall(r'"([^"]+)"', glyphs[1]),
        'icons': {},
    }
    for name, text, path in re.findall(r'new\("([^"]+)", "([^"]+)", "([^"]+)"\)', source):
        if not NAME.match(name) or not PATH.match(path) or len(path) > 1500 or len(text) != 1:
            raise ValueError(f'{name}: not a usable icon entry')
        if name in table['icons']:
            raise ValueError(f'{name}: listed twice')
        table['icons'][name] = {'text': text, 'path': path}
    if len(table['icons']) < 20:
        raise ValueError('the icon table looks incomplete; check LayoutIcons.cs')
    return json.dumps(table, indent=2, ensure_ascii=False) + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    generated = generate()
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding='utf-8') != generated:
            sys.exit('Deck icons are out of date; run python3 tools/deck-icons.py')
        print('Deck icons match LayoutIcons.cs')
    else:
        OUTPUT.write_text(generated, encoding='utf-8')


if __name__ == '__main__':
    main()
