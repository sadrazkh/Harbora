"""Draws the tutorial's arrows and Persian labels onto the panel screenshots.

The screenshots themselves are captured from the running panel and saved as static HTML first, so
nothing here needs credentials — see docs/tutorial/README.md for how they are produced.

Two things make this less trivial than "write some text on an image":

* Persian is a joined script read right to left, and neither PIL nor a TrueType font does that on
  its own. The text has to be reshaped into its presentation forms and then reordered, or it comes
  out as disconnected letters in the wrong order — which is worse than no label, because it looks
  like the product is broken rather than the documentation.

* A label has to stay readable over whatever pixels it lands on. Every one is drawn on a filled
  plate with a border, so a caption over a dark chart and the same caption over white space are
  equally legible.

Run it after re-capturing:  python scripts/annotate-tutorial.py
"""
import json
import pathlib
import sys

from PIL import Image, ImageDraw, ImageFont

try:
    import arabic_reshaper
    from bidi.algorithm import get_display
except ImportError:
    print("Needs: pip install --user arabic_reshaper python-bidi")
    sys.exit(1)

ROOT = pathlib.Path(__file__).resolve().parents[1]
IMG = ROOT / "docs" / "tutorial" / "img"
NOTES = ROOT / "docs" / "tutorial" / "annotations.json"

# Tahoma carries the Persian range and ships with Windows; DejaVu is the fallback on a build agent.
FONT_CANDIDATES = [
    "C:/Windows/Fonts/tahoma.ttf",
    "C:/Windows/Fonts/segoeui.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
]

INK = (17, 24, 39)
PLATE = (255, 255, 255)
ACCENT = (109, 40, 217)      # the panel's own accent, so the marks read as part of it
ACCENT_SOFT = (237, 233, 254)
SHADOW = (0, 0, 0, 40)


def font(size: int) -> ImageFont.FreeTypeFont:
    for path in FONT_CANDIDATES:
        if pathlib.Path(path).exists():
            return ImageFont.truetype(path, size)
    raise SystemExit("No font with Persian coverage was found.")


def persian(text: str) -> str:
    """Joined and reordered, which is what makes it readable rather than a row of stray letters."""
    return get_display(arabic_reshaper.reshape(text))


def arrow(draw: ImageDraw.ImageDraw, start, end, colour=ACCENT, width=3):
    """A line with a solid head, pointing at the thing being talked about."""
    draw.line([start, end], fill=colour, width=width)

    # The head is drawn from the direction of travel so it points along the line rather than at a
    # fixed angle — an arrow whose head does not match its shaft reads as two marks, not one.
    import math
    angle = math.atan2(end[1] - start[1], end[0] - start[0])
    size = 11
    draw.polygon(
        [
            end,
            (end[0] - size * math.cos(angle - 0.42), end[1] - size * math.sin(angle - 0.42)),
            (end[0] - size * math.cos(angle + 0.42), end[1] - size * math.sin(angle + 0.42)),
        ],
        fill=colour,
    )


def label(draw: ImageDraw.ImageDraw, xy, number: int, text: str, f, fnum):
    """A numbered plate. The number ties the mark to the same number in the prose beneath."""
    shaped = persian(text)
    pad = 9
    box = draw.textbbox((0, 0), shaped, font=f)
    tw, th = box[2] - box[0], box[3] - box[1]

    badge = 24
    w = tw + pad * 3 + badge
    h = max(th + pad * 2, badge + pad)
    x, y = xy

    draw.rounded_rectangle([x, y, x + w, y + h], radius=8, fill=PLATE, outline=ACCENT, width=2)
    draw.ellipse([x + pad - 3, y + (h - badge) // 2, x + pad - 3 + badge, y + (h - badge) // 2 + badge],
                 fill=ACCENT)

    num = str(number)
    nbox = draw.textbbox((0, 0), num, font=fnum)
    draw.text(
        (x + pad - 3 + (badge - (nbox[2] - nbox[0])) / 2 - nbox[0],
         y + (h - badge) // 2 + (badge - (nbox[3] - nbox[1])) / 2 - nbox[1]),
        num, font=fnum, fill=PLATE)

    draw.text((x + pad * 2 + badge, y + (h - th) / 2 - box[1]), shaped, font=f, fill=INK)
    return w, h


def main():
    if not NOTES.exists():
        print(f"No annotations at {NOTES}")
        return 1

    notes = json.loads(NOTES.read_text(encoding="utf-8"))
    f, fnum = font(15), font(14)
    written = 0

    for name, marks in notes.items():
        source = IMG / f"{name}.png"
        if not source.exists():
            print(f"  missing screenshot: {name}.png")
            continue

        image = Image.open(source).convert("RGB")
        draw = ImageDraw.Draw(image)

        for i, mark in enumerate(marks, start=1):
            lx, ly = mark["label"]
            tx, ty = mark["point"]
            w, h = label(draw, (lx, ly), i, mark["text"], f, fnum)

            # From the edge of the plate nearest the target, so the arrow never crosses its own
            # label.
            anchor = (lx + w, ly + h / 2) if tx > lx else (lx, ly + h / 2)
            arrow(draw, anchor, (tx, ty))

        out = IMG / f"{name}.annotated.png"
        image.save(out, optimize=True)
        written += 1
        print(f"  {out.name}  ({len(marks)} marks)")

    print(f"\n{written} annotated image(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
