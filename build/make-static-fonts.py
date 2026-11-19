"""Cuts the static fonts of the Windows app from the web console's variable fonts.

Windows Server 2016 (build 14393) cannot measure text in a variable font: WPF hangs in DirectWrite before the first window
shows (design 2026-11-19-update-page-after-restart.md, amendment of 2026-11-20). The app therefore embeds one static file
per weight the theme uses, cut from web/public/fonts at the same font version.

    python3 -m venv /tmp/ft && /tmp/ft/bin/pip install fonttools
    /tmp/ft/bin/python build/make-static-fonts.py
"""
from pathlib import Path

from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

REPO = Path(__file__).resolve().parent.parent
SOURCE = REPO / "web" / "public" / "fonts"
TARGET = REPO / "app" / "HiveBDC.App" / "Assets" / "Fonts"
WEIGHTS = {400: "Regular", 500: "Medium", 600: "SemiBold", 700: "Bold"}
# DM Sans keeps the optical size of 14 px text, the size the Windows app has used since 1.10.0.
FAMILIES = [("dm-sans.ttf", "DM Sans", "DMSans", {"opsz": 14}), ("manrope.ttf", "Manrope", "Manrope", {})]
DROPPED_NAMES = (21, 22, 25)


def cut(source: Path, family: str, file_stem: str, pinned: dict, weight: int) -> Path:
    style = WEIGHTS[weight]
    font = instantiateVariableFont(TTFont(source), {**pinned, "wght": weight}, static=True)
    for table in ("STAT", "fvar", "gvar", "avar", "HVAR", "MVAR", "cvar"):
        if table in font:
            del font[table]
    ribbi = style in ("Regular", "Bold")
    version = font["name"].getDebugName(5)
    names = {
        1: family if ribbi else f"{family} {style}",
        2: style if ribbi else "Regular",
        3: f"{version};{file_stem}-{style}",
        4: f"{family} {style}",
        6: f"{file_stem}-{style}",
        16: family,
        17: style,
    }
    table = font["name"]
    table.names = [n for n in table.names if n.nameID not in DROPPED_NAMES and n.nameID not in names]
    for name_id, text in names.items():
        table.setName(text, name_id, 3, 1, 0x409)
        table.setName(text, name_id, 1, 0, 0)
    os2 = font["OS/2"]
    os2.usWeightClass = weight
    os2.fsSelection &= ~((1 << 0) | (1 << 5) | (1 << 6))
    os2.fsSelection |= (1 << 5) if style == "Bold" else (1 << 6) if style == "Regular" else 0
    font["head"].macStyle = 1 if style == "Bold" else 0
    target = TARGET / f"{file_stem}-{style}.ttf"
    font.save(target)
    return target


def main() -> None:
    for source_name, family, file_stem, pinned in FAMILIES:
        for weight in WEIGHTS:
            print(cut(SOURCE / source_name, family, file_stem, pinned, weight).relative_to(REPO))


if __name__ == "__main__":
    main()
