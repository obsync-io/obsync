"""Generates the branded WiX installer bitmaps from the app icon.

Outputs (both 24-bit RGB BMP -- MSI's Bitmap control does no alpha compositing,
so these are flattened):

  dialog.bmp  493x312  welcome/finish page background. WixUI stretches it across
                       the whole dialog and draws the page text over the RIGHT
                       side (text controls start at 135 units = ~180 px), so only
                       the left 170 px is the brand panel; the rest stays white.
  banner.bmp  493x58   top strip of the inner pages. WixUI draws the dialog title
                       over the left side, so the left ~350 px stay clean white;
                       the mark sits right-aligned above a hairline rule.

DESIGN NOTES -- why this looks the way it does
----------------------------------------------
The first version of these bitmaps flooded the panel with flat #1B17FF. Measured,
that made the accent 29% of the welcome window at full saturation, against an app
whose own Colors.xaml describes its palette as "calm enterprise": #F8FAFC ground,
#111827 ink, #6B7280 muted, with #1B17FF labelled *AccentColor*. Using an accent
as a ground is what made the installer read as dated next to the app it installs.

So the panel is now a deep vertical gradient that keeps the brand hue at the top,
where the mark sits, and settles into near-black indigo at the bottom. Two very
low-opacity arcs echo the swoosh in the mark and give the field some depth. The
content is left-aligned on a margin rather than centred in the column, and the
mark is 56 px rather than 92 -- the old one was large enough to be the only thing
on the page.

A note on gradients: the previous generator avoided them because they "band in
24-bit BMP". That is an 8-bit-palette problem. 24-bit BMP is truecolour, and the
range used here spans far more levels than the 312 rows it is drawn over, so the
result is smooth. Verified by counting distinct colours in the output.

The mark (Obsync_Icon.png) is a #1B17FF disc with a white swoosh cutout. That
blue is close to the top of the panel gradient, so for the panel the mark is
recoloured to its inverse (white disc, brand swoosh) -- the swoosh then reads as
a cutout, mirroring how the original reads on white. The banner uses the mark
unmodified on white.

Output is deterministic byte-for-byte: no timestamps, no randomness.

Run:  python packaging/assets/generate-bitmaps.py
Requires Pillow. Paths resolve relative to this file; output lands beside it.
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
ICON_PATH = os.path.join(REPO, "src", "Obsync.App", "Assets", "Obsync_Icon.png")

# --- palette, taken from src/Obsync.App/Themes/Colors.xaml -------------------
BLUE = (27, 23, 255)          # #1B17FF AccentColor (also the icon disc colour)
WHITE = (255, 255, 255)
BORDER = (229, 231, 235)      # #E5E7EB BorderColor -- the banner hairline

# The panel gradient. Top stays close to the brand so the mark sits in its own
# colour; the bottom is a deep indigo ink that gives the field weight without
# another saturated block.
PANEL_TOP = (42, 37, 214)     # #2A25D6
PANEL_BOTTOM = (13, 16, 48)   # #0D1030

FONT_DIR = r"C:\Windows\Fonts"
SEGOE_SEMIBOLD = os.path.join(FONT_DIR, "seguisb.ttf")
SEGOE_REGULAR = os.path.join(FONT_DIR, "segoeui.ttf")
SEGOE_LIGHT = os.path.join(FONT_DIR, "segoeuil.ttf")

# Shape work is drawn at 4x and LANCZOS-downsampled for clean anti-aliased
# edges; text is drawn at 1x, where Pillow's hinted rasteriser is crisper.
SS = 4

PANEL_W = 170   # brand panel width; WixUI page text starts at ~180 px
MARGIN = 26     # left margin for panel content


def load_icon() -> Image.Image:
    return Image.open(ICON_PATH).convert("RGBA")


def invert_icon(icon: Image.Image) -> Image.Image:
    """Swap the mark's colours: blue disc / white swoosh -> white disc / blue swoosh.

    The red channel separates the two (disc R=27, swoosh R=255); using it as a
    blend mask keeps the anti-aliased edges smooth.
    """
    r, _g, _b, a = icon.split()
    lo, hi = BLUE[0], 255
    mask = r.point(lambda v: max(0, min(255, round((v - lo) * 255 / (hi - lo)))))
    inverted = Image.composite(
        Image.new("RGB", icon.size, BLUE), Image.new("RGB", icon.size, WHITE), mask)
    inverted.putalpha(a)
    return inverted


def scaled(icon: Image.Image, size: int) -> Image.Image:
    """Downsample the 2000 px source to `size` in two LANCZOS steps."""
    return icon.resize((size * SS, size * SS), Image.LANCZOS).resize((size, size), Image.LANCZOS)


def vertical_gradient(width: int, height: int, top: tuple, bottom: tuple) -> Image.Image:
    grad = Image.new("RGB", (width, height))
    draw = ImageDraw.Draw(grad)
    for y in range(height):
        t = y / (height - 1)
        draw.line(
            [(0, y), (width, y)],
            fill=tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    return grad


def add_depth(panel: Image.Image) -> Image.Image:
    """A soft glow behind the mark and two faint arcs echoing the swoosh.

    Both are drawn on a separate layer at very low opacity and composited, so the
    panel reads as a surface with light on it rather than a printed rectangle.
    Kept subtle deliberately -- at higher opacity this becomes noise, which is a
    different way of looking cheap.
    """
    w, h = panel.size

    glow = Image.new("L", (w * SS, h * SS), 0)
    ImageDraw.Draw(glow).ellipse(
        (int(-40 * SS), int(-30 * SS), int(200 * SS), int(190 * SS)), fill=42)
    glow = glow.resize((w, h), Image.LANCZOS).filter(ImageFilter.GaussianBlur(18))
    panel = Image.composite(Image.new("RGB", (w, h), WHITE), panel, glow)

    arcs = Image.new("L", (w * SS, h * SS), 0)
    d = ImageDraw.Draw(arcs)
    for radius, alpha in ((150, 16), (215, 11)):
        d.ellipse(
            (int((MARGIN - radius) * SS), int((h - 60 - radius) * SS),
             int((MARGIN + radius) * SS), int((h - 60 + radius) * SS)),
            outline=alpha, width=int(1.5 * SS))
    arcs = arcs.resize((w, h), Image.LANCZOS)
    return Image.composite(Image.new("RGB", (w, h), WHITE), panel, arcs)


def build_dialog(icon: Image.Image) -> Image.Image:
    """Deep gradient brand panel: mark, wordmark, rule, tagline, then open space."""
    w, h = 493, 312
    img = Image.new("RGB", (w, h), WHITE)

    panel = add_depth(vertical_gradient(PANEL_W, h, PANEL_TOP, PANEL_BOTTOM))
    img.paste(panel, (0, 0))

    mark = invert_icon(icon)
    mark = scaled(mark, 56)
    img.paste(mark, (MARGIN, 40), mark)

    d = ImageDraw.Draw(img)
    d.text((MARGIN, 118), "Obsync",
           font=ImageFont.truetype(SEGOE_SEMIBOLD, 26), fill=WHITE, anchor="lm")

    # Short accent rule, at partial opacity against the gradient beneath it.
    rule = Image.new("L", (PANEL_W, h), 0)
    ImageDraw.Draw(rule).rectangle((MARGIN, 140, MARGIN + 22, 141), fill=150)
    img.paste(Image.composite(
        Image.new("RGB", (PANEL_W, h), WHITE), img.crop((0, 0, PANEL_W, h)), rule), (0, 0))

    d = ImageDraw.Draw(img)
    tagline = ImageFont.truetype(SEGOE_REGULAR, 12)
    for i, line in enumerate(("SQL Server", "schema versioning")):
        d.text((MARGIN, 162 + i * 17), line, font=tagline, fill=(190, 192, 225), anchor="lm")

    return img


def build_banner(icon: Image.Image) -> Image.Image:
    """Minimal white strip: small right-aligned mark over a neutral hairline.

    The rule used to be 2 px of full-saturation #1B17FF, which drew a hard bar
    under every page header -- and the system's own etched BannerLine control
    draws immediately below it, so it read as two competing separators. A single
    hairline in the app's border colour lets the two merge into one quiet edge,
    and the brand now comes from the coloured page title instead.
    """
    w, h, rule = 493, 58, 1
    img = Image.new("RGB", (w, h), WHITE)

    mark = scaled(icon, 28)
    img.paste(mark, (w - 18 - 28, (h - rule - 28) // 2), mark)

    ImageDraw.Draw(img).rectangle((0, h - rule, w, h), fill=BORDER)
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
