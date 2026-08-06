"""Draws the tutorial's arrows and Persian labels onto the panel screenshots.

The screenshots are captured from the running panel and saved as static HTML first, so nothing here
needs credentials — see docs/tutorial/README.md for how they are produced.

Three things make this less trivial than "write some text on an image":

* Persian is a joined script read right to left, and neither PIL nor a TrueType font does that on
  its own. The text has to be reshaped into its presentation forms and then reordered, or it comes
  out as disconnected letters in the wrong order — which is worse than no label, because it looks
  like the product is broken rather than the documentation.

* A label has to stay readable over whatever pixels it lands on. Every one is drawn on a filled
  plate with a border, so a caption over a dark chart and the same caption over white space are
  equally legible.

* The panel shows real secrets on real pages — a webhook secret, an object-storage access key, the
  address somebody's certificates are registered to. Documentation is the one place those must not
  travel, so every screenshot declares the regions to paint over before anything is drawn on it.

Run it after re-capturing:  python scripts/annotate-tutorial.py
"""
import json
import math
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
REDACTED = (203, 213, 225)
REDACTED_INK = (100, 116, 139)


def font(size: int) -> ImageFont.FreeTypeFont:
    for path in FONT_CANDIDATES:
        if pathlib.Path(path).exists():
            return ImageFont.truetype(path, size)
    raise SystemExit("No font with Persian coverage was found.")


def persian(text: str) -> str:
    """Joined and reordered, which is what makes it readable rather than a row of stray letters."""
    return get_display(arabic_reshaper.reshape(text))


def redact(draw: ImageDraw.ImageDraw, region, f):
    """Paints over something that must not leave the server.

    Deliberately visible rather than blurred: a reader should be able to tell that a value was
    removed on purpose, or they will go looking for the key that "should" be there.
    """
    x, y, w, h = region
    draw.rounded_rectangle([x, y, x + w, y + h], radius=4, fill=REDACTED)
    draw.text((x + 8, y + h / 2 - 8), "• • • • •", font=f, fill=REDACTED_INK)


def arrow(draw: ImageDraw.ImageDraw, start, end, colour=ACCENT, width=3):
    """A line with a solid head, pointing at the thing being talked about."""
    draw.line([start, end], fill=colour, width=width)

    # The head is drawn from the direction of travel so it points along the line rather than at a
    # fixed angle — an arrow whose head does not match its shaft reads as two marks, not one.
    angle = math.atan2(end[1] - start[1], end[0] - start[0])
    size = 12
    draw.polygon(
        [
            end,
            (end[0] - size * math.cos(angle - 0.42), end[1] - size * math.sin(angle - 0.42)),
            (end[0] - size * math.cos(angle + 0.42), end[1] - size * math.sin(angle + 0.42)),
        ],
        fill=colour,
    )


PAD = 9
BADGE = 24


def measure(draw: ImageDraw.ImageDraw, text: str, f):
    """The plate a caption will occupy, so an arrow can be aimed before anything is drawn."""
    box = draw.textbbox((0, 0), persian(text), font=f)
    tw, th = box[2] - box[0], box[3] - box[1]
    return tw + PAD * 3 + BADGE, max(th + PAD * 2, BADGE + PAD)


def label(draw: ImageDraw.ImageDraw, xy, number: int, text: str, f, fnum):
    """A numbered plate. The number ties the mark to the same number in the prose beneath."""
    shaped = persian(text)
    pad, badge = PAD, BADGE
    box = draw.textbbox((0, 0), shaped, font=f)
    tw, th = box[2] - box[0], box[3] - box[1]

    w, h = measure(draw, text, f)
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
    f, fnum, fredact = font(15), font(14), font(13)
    written, missing = 0, []

    for name, page in notes.items():
        source = IMG / f"{name}.png"
        if not source.exists():
            missing.append(name)
            continue

        image = Image.open(source).convert("RGB")
        draw = ImageDraw.Draw(image)

        # Before anything else, so a mark can never be drawn under a value and then painted over.
        for region in page.get("redact", []):
            redact(draw, region, fredact)

        # Arrows first, plates second. They are interleaved on a page — one mark's arrow runs
        # straight across the next mark's caption — and a line drawn over a caption is the one thing
        # that makes it unreadable.
        marks = page.get("marks", [])
        sizes = [measure(draw, m["text"], f) for m in marks]

        for (lx, ly), (w, h), mark in zip((m["label"] for m in marks), sizes, marks):
            if "point" not in mark:
                continue
            tx, ty = mark["point"]
            # From the edge of the plate nearest the target, so the arrow never crosses its own
            # label.
            anchor = (lx + w, ly + h / 2) if tx > lx else (lx, ly + h / 2)
            arrow(draw, anchor, (tx, ty))

        for i, mark in enumerate(marks, start=1):
            label(draw, mark["label"], i, mark["text"], f, fnum)

        out = IMG / f"{name}.annotated.png"
        image.save(out, optimize=True)
        written += 1
        print(f"  {out.name}  ({len(page.get('marks', []))} marks,"
              f" {len(page.get('redact', []))} redacted)")

    if missing:
        print("\nno screenshot for: " + ", ".join(missing))
    print(f"\n{written} annotated image(s).")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
