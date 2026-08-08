# KnobKey — brand guide

**Proposed display name:** KnobKey
**Tagline:** *A knob that speaks USB.*

## Why this name

The product in one compound: a rotary knob that shows up as a keyboard (its default mode sends
F-keys). **KnobKey** is short, memorable, and honest about the trick that makes the device
plug-and-play.

**Alternates considered:** *PicoDial* (names the MCU, not the experience), *TwistKey* (close
second), *Encoderface* (no).

## The mark

A knob with its indicator at twelve o'clock, and the USB trident growing out of its shaft — the
whole hardware story in two glyphs. The raspberry indicator tips a hat to the Pi Pico inside.

## Palette

| Color | Hex | Role |
|---|---|---|
| Gunmetal | `#37474F` | Background / primary brand color |
| Chalk | `#ECEFF1` | Knob, trident, text on dark |
| Raspberry | `#D6246E` | Indicator, accents (Pico nod) |

## Voice

Maker voice: modes, mappings, detents. Firmware table docs already do this well — the brand just
gives the project a face for the README and a future config app's window icon.

## Files in this directory

| File | Use |
|---|---|
| `logo.svg` | Full lockup (mark + wordmark + tagline) for README headers and docs |
| `favicon.svg` | Square app mark, scales from 16px to full size |
| `favicon.ico` | Legacy multi-size favicon (16/32/48) for browsers that want `.ico` |
| `favicon-32.png` | 32px PNG favicon |
| `apple-touch-icon.png` | 180px iOS home-screen icon |
| `icon-512.png` | Large raster for app manifests, social cards, stores |

### Wiring the favicon into a web page

```html
<link rel="icon" href="/branding/favicon.svg" type="image/svg+xml">
<link rel="icon" href="/branding/favicon.ico" sizes="16x16 32x32 48x48">
<link rel="apple-touch-icon" href="/branding/apple-touch-icon.png">
```

### README header

```markdown
<p align="center"><img src="branding/logo.svg" alt="KnobKey" width="520"></p>
```

## Typography

Wordmark: **Montserrat Bold** (falls back to Segoe UI / system sans). Body text: the platform
default sans. For code-adjacent surfaces, any monospace at hand — the brand doesn't pin one.

The logo's wordmark is live SVG text, so it renders with whatever sans is installed; if you want
it pixel-identical everywhere, convert the text to outlines in any SVG editor and re-save.

## Dark and light backgrounds

The tile carries its own background, so both `logo.svg` and `favicon.svg` work unchanged on
light or dark pages. The wordmark in `logo.svg` is dark ink — on a dark page, either rely on the
tile alone (use `favicon.svg`) or restyle the two `<text>` fills to `#F0F2F5`.

---
*Generated as a proposal — names, colors, and marks are suggestions to accept, tweak, or reject.*
