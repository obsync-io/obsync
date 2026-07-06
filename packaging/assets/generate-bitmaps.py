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
BLUE_DARK = (21, 18, 214)   # #1512D6 hover/dark accent
WHITE = (255, 255, 255)
# White at 60% opacity flattened onto BLUE (the bitmaps carry no alpha).
TAGLINE = tuple(round(b + 0.6 * (255 - b)) for b in BLUE)

FONT_DIR = r"C:\Windows\Fonts"
SEGOE_SEMIBOLD = os.path.join(FONT_DIR, "seguisb.ttf")
SEGOE_REGULAR = os.path.join(FONT_DIR, "segoeui.ttf")

# Shape work (disc, arc) is drawn at 4x and LANCZOS-downsampled for clean
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
    w, h = 493, 312
    img = Image.new("RGB", (w, h), WHITE)

    # Blue side panel with a subtle darker arc rising from the bottom, at 4x.
    panel = Image.new("RGB", (PANEL_W * SS, h * SS), BLUE)
    pd = ImageDraw.Draw(panel)
    cx, cy, radius = 85 * SS, 392 * SS, 160 * SS  # arc top at y=232 (1x)
    pd.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=BLUE_DARK)

    # Inverted mark, 96 px, centered in the panel's upper third.
    mark = invert_icon(icon).resize((96 * SS, 96 * SS), Image.LANCZOS)
    panel.paste(mark, ((PANEL_W * SS - 96 * SS) // 2, 40 * SS), mark)

    img.paste(panel.resize((PANEL_W, h), Image.LANCZOS), (0, 0))

    # Wordmark and tagline, drawn at 1x.
    d = ImageDraw.Draw(img)
    d.text((PANEL_W // 2, 162), "Obsync",
           font=ImageFont.truetype(SEGOE_SEMIBOLD, 34), fill=WHITE, anchor="mm")
    tagline_font = ImageFont.truetype(SEGOE_REGULAR, 14)
    d.text((PANEL_W // 2, 192), "SQL Server", font=tagline_font, fill=TAGLINE, anchor="mm")
    d.text((PANEL_W // 2, 210), "schema versioning", font=tagline_font, fill=TAGLINE, anchor="mm")
    return img


def build_banner(icon: Image.Image) -> Image.Image:
    w, h, rule = 493, 58, 2
    img = Image.new("RGB", (w, h), WHITE)

    # Original mark (blue disc on white), 40 px, right-aligned with 12 px
    # padding, vertically centered in the strip above the rule.
    mark = icon.resize((40 * SS, 40 * SS), Image.LANCZOS).resize((40, 40), Image.LANCZOS)
    img.paste(mark, (w - 12 - 40, (h - rule - 40) // 2), mark)

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
