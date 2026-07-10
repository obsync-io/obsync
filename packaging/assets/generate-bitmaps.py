"""Generates the branded WiX installer bitmaps from the app icon.

Outputs (both 24-bit RGB BMP -- MSI dialogs require classic BMP, no alpha):

  dialog.bmp  493x312  welcome/finish page background. WixUI stretches it across
                       the whole dialog and draws the page text over the RIGHT
                       side (text controls start at ~180 px), so only the left
                       170 px panel is painted brand blue; the rest stays white.
  banner.bmp  493x58   top strip of the inner pages. WixUI draws the dialog
                       title over the left side, so the left ~350 px stay clean
                       white; the icon mark sits right-aligned above a 2 px
                       brand-blue rule along the bottom edge.

The icon mark (Obsync_Icon.png) is a #1B17FF disc with a white swoosh cutout.
That blue is exactly the panel background, so for the dialog panel the mark is
recolored to its inverse (white disc, brand-blue swoosh) -- the swoosh then
reads as a cutout against the panel, mirroring how the original mark reads on
white. The banner uses the mark unmodified on white.

Both bitmaps are flat fields with no gradients (which band in 24-bit BMP) and
no timestamps or other varying data -- output is deterministic byte-for-byte.

Run:  python packaging/assets/generate-bitmaps.py
Requires Pillow. Paths resolve relative to this file; output lands beside it.
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
ICON_PATH = os.path.join(REPO, "src", "Obsync.App", "Assets", "Obsync_Icon.png")

BLUE = (27, 23, 255)        # #1B17FF brand accent (also the icon disc color)
WHITE = (255, 255, 255)
# White at 78% opacity flattened onto BLUE (the bitmaps carry no alpha):
# bright enough to read as body text on the panel, dim enough to rank below
# the wordmark.
TAGLINE = tuple(round(b + 0.78 * (255 - b)) for b in BLUE)

FONT_DIR = r"C:\Windows\Fonts"
SEGOE_SEMIBOLD = os.path.join(FONT_DIR, "seguisb.ttf")
SEGOE_REGULAR = os.path.join(FONT_DIR, "segoeui.ttf")

# Shape work (disc, swoosh) is drawn at 4x and LANCZOS-downsampled for clean
# anti-aliased edges; text is drawn at 1x where Pillow's rasterizer is crisp.
SS = 4

PANEL_W = 170  # blue panel width; WixUI page text starts at ~180 px


def load_icon() -> Image.Image:
    return Image.open(ICON_PATH).convert("RGBA")


def invert_icon(icon: Image.Image) -> Image.Image:
    """Swap the mark's colors: blue disc / white swoosh -> white disc / blue swoosh.

    The red channel separates the two colors (blue disc R=27, white swoosh
    R=255); using it as a blend mask keeps the anti-aliased edges smooth.
    """
    r, _g, _b, a = icon.split()
    lo, hi = BLUE[0], 255
    mask = r.point(lambda v: max(0, min(255, round((v - lo) * 255 / (hi - lo)))))
    solid_blue = Image.new("RGB", icon.size, BLUE)
    solid_white = Image.new("RGB", icon.size, WHITE)
    inverted = Image.composite(solid_blue, solid_white, mask)
    inverted.putalpha(a)
    return inverted


def build_dialog(icon: Image.Image) -> Image.Image:
    """Solid brand-blue side panel: mark, wordmark, tagline, generous space.

    One centered column with a clear hierarchy -- mark (92 px), wordmark,
    a short accent rule, two-line tagline -- and nothing else; the empty
    lower third is deliberate negative space, matching the app's whitespace.
    """
    w, h = 493, 312
    img = Image.new("RGB", (w, h), WHITE)
    ImageDraw.Draw(img).rectangle((0, 0, PANEL_W - 1, h - 1), fill=BLUE)

    # Inverted mark (white disc, blue swoosh), 92 px, centered, upper third.
    mark = invert_icon(icon).resize((92 * SS, 92 * SS), Image.LANCZOS)
    mark = mark.resize((92, 92), Image.LANCZOS)
    img.paste(mark, ((PANEL_W - 92) // 2, 52), mark)

    d = ImageDraw.Draw(img)
    cx = PANEL_W // 2

    # Wordmark and tagline, drawn at 1x.
    d.text((cx, 186), "Obsync",
           font=ImageFont.truetype(SEGOE_SEMIBOLD, 32), fill=WHITE, anchor="mm")

    # Hairline separator: short, centered, faint -- ties wordmark to tagline.
    d.rectangle((cx - 14, 212, cx + 14, 213), fill=TAGLINE)

    tagline_font = ImageFont.truetype(SEGOE_REGULAR, 14)
    d.text((cx, 232), "SQL Server", font=tagline_font, fill=TAGLINE, anchor="mm")
    d.text((cx, 251), "schema versioning", font=tagline_font, fill=TAGLINE, anchor="mm")
    return img


def build_banner(icon: Image.Image) -> Image.Image:
    """Minimal white strip: small right-aligned mark over a 2 px accent rule."""
    w, h, rule = 493, 58, 2
    img = Image.new("RGB", (w, h), WHITE)

    # Original mark (blue disc on white), 32 px, right-aligned with 16 px
    # padding, vertically centered in the strip above the rule.
    mark = icon.resize((32 * SS, 32 * SS), Image.LANCZOS).resize((32, 32), Image.LANCZOS)
    img.paste(mark, (w - 16 - 32, (h - rule - 32) // 2), mark)

    ImageDraw.Draw(img).rectangle((0, h - rule, w, h), fill=BLUE)
    return img


def main() -> None:
    icon = load_icon()
    for name, image in (("dialog.bmp", build_dialog(icon)),
                        ("banner.bmp", build_banner(icon))):
        path = os.path.join(HERE, name)
        image.convert("RGB").save(path, "BMP")
        print(f"wrote {path} ({image.size[0]}x{image.size[1]})")


if __name__ == "__main__":
    main()
