"""Draws the application icon. Build-time only; nothing here ships.

Ported from NetControl's tools/icon.py, which was ported from File Compare's.
The family shares a mark as well as a stylesheet: a dark rounded tile, light
shapes on it, one stroke of the accent.

LogicControl is a rung of ladder: the two power rails, a rung between them
with a contact on it, and the coil it drives drawn in the accent - logic,
in the one shape every controls engineer reads without thinking. A second,
dimmer rung below says "a routine" rather than "a rung". The accent is violet,
LogicControl's own default (Theme.DefaultAccent), so it sits beside Redline
PDF's red, the DWG viewer's blue and NetControl's cyan without sharing a
colour. At 16px it is two bars, a line and a violet ring, which is still that.

Pillow rather than anything .NET on purpose: this is a chore, not a build
step. Both outputs are committed, so a checkout builds the app and the
installer without Python.

    pip install pillow
    python tools/icon.py

Writes `src/LogicControl.App/Assets/LogicControl.ico` (the sizes Windows asks
for, embedded in the exe and used by the installer) and `assets/icon.png`
(256px, for the README).
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw

CANVAS = 1024
SCALE = CANVAS // 256

TILE_TOP = (32, 36, 44)
TILE_BOTTOM = (16, 18, 22)
CARD = (233, 237, 244)
CARD_DIM = (150, 158, 172)
ACCENT = (154, 122, 255)

ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


def _px(value: float) -> int:
    return int(round(value * SCALE))


def draw() -> Image.Image:
    image = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))

    gradient = Image.new("RGB", (1, CANVAS))
    for y in range(CANVAS):
        ratio = y / (CANVAS - 1)
        gradient.putpixel((0, y), tuple(
            int(round(top + (bottom - top) * ratio))
            for top, bottom in zip(TILE_TOP, TILE_BOTTOM)
        ))
    gradient = gradient.resize((CANVAS, CANVAS))
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, CANVAS - 1, CANVAS - 1), radius=_px(58), fill=255)
    image.paste(gradient, (0, 0), mask)

    canvas = ImageDraw.Draw(image)

    # The rails.
    rail = _px(14)
    left, right = _px(44), CANVAS - _px(44)
    top, bottom = _px(46), CANVAS - _px(46)
    canvas.rounded_rectangle((left - rail // 2, top, left + rail // 2, bottom), radius=rail // 2, fill=CARD)
    canvas.rounded_rectangle((right - rail // 2, top, right + rail // 2, bottom), radius=rail // 2, fill=CARD)

    def rung(y: int, colour_wire, colour_contact, colour_coil, weight: int) -> None:
        w = weight
        contact_x = _px(96)
        contact_gap = _px(26)
        contact_h = _px(46)
        coil_cx = _px(168)
        coil_r = _px(25)

        # Wire: rail to contact, contact to coil, coil to rail.
        canvas.rectangle((left, y - w // 2, contact_x, y + w // 2), fill=colour_wire)
        canvas.rectangle((contact_x + contact_gap, y - w // 2, coil_cx - coil_r, y + w // 2), fill=colour_wire)
        canvas.rectangle((coil_cx + coil_r, y - w // 2, right, y + w // 2), fill=colour_wire)

        # The contact: two upright bars.
        bar = int(w * 1.6)
        for x in (contact_x, contact_x + contact_gap):
            canvas.rounded_rectangle((x - bar // 2, y - contact_h // 2, x + bar // 2, y + contact_h // 2),
                                     radius=bar // 2, fill=colour_contact)

        # The coil: a ring, the two arcs of ( ) closed into one so it reads at 16px.
        canvas.ellipse((coil_cx - coil_r, y - coil_r, coil_cx + coil_r, y + coil_r),
                       outline=colour_coil, width=int(w * 1.6))

    rung(_px(100), CARD, CARD, ACCENT, _px(9))
    rung(_px(172), CARD_DIM, CARD_DIM, CARD_DIM, _px(7))
    return image


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    master = draw()
    small = master.resize((256, 256), Image.LANCZOS)

    ico = os.path.join(root, "src", "LogicControl.App", "Assets", "LogicControl.ico")
    os.makedirs(os.path.dirname(ico), exist_ok=True)
    small.save(ico, format="ICO", sizes=[(s, s) for s in ICO_SIZES])

    png = os.path.join(root, "assets", "icon.png")
    os.makedirs(os.path.dirname(png), exist_ok=True)
    small.save(png, format="PNG")

    print(f"wrote {ico}")
    print(f"wrote {png}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
