# The hologram-fan `.bin` format

Reverse-engineered in October 2026 from ten `.bin` files that came on the memory card of a spinning-LED
"3D hologram" fan whose vendor software was lost. The fan has a blade with 48 RGB LEDs and plays every
`.bin` in the root of its micro-SD card. HoloFan's output plays on that fan.

`tools/holo_preview.py` decodes a file with exactly the layout below; use it to check whether your fan's
files are the same format.

## File

```
file = header + N frames + 100,000 zero bytes
frame = 28,600 bytes = 300 lines × 90 bytes of picture + 1,600 bytes of audio
```

- **20 frames per second.** N is always a multiple of 20 (whole seconds) in the originals, so the end is
  padded with black, silent frames.
- The 100,000 zero bytes at the end are in every original.
- So `(file size − 100,000) mod 28,600` is the header length and `(file size − 100,000) div 28,600` is N.

## Picture

The picture is stored in polar form, the way the blade draws it:

- Each **line** is one angular position of the blade, **300 per turn**. Line 0 is at 3 o'clock and the
  following lines go **counter-clockwise**. (The originals end with a logo whose text only reads correctly
  this way; the other direction mirrors it upside down.)
- Each line is 720 bits = **5 bit planes**, most significant first. Each plane has **48 LEDs × 3 bits in
  the order B, G, R**. **LED 0 is the outermost** one and LED 47 is at the center. Bits are packed most
  significant first in each byte.
- So each color channel has 5 bits (0–31): `value = Σ plane_bit[p] << (4 − p)`.

```
line (90 bytes)
├─ plane 0 (bit 4), 18 bytes: LED0 B G R | LED1 B G R | … | LED47 B G R
├─ plane 1 (bit 3), 18 bytes
├─ plane 2 (bit 2), 18 bytes
├─ plane 3 (bit 1), 18 bytes
└─ plane 4 (bit 0), 18 bytes
```

The originals look like a center square crop of the video (an inscribed circle) with boosted contrast:
the Netflix red (229, 9, 20) is stored as (30, 0, 0) and many pixels clip at 31. HoloFan's "Vivas" colors
use a 1.25× contrast around mid-gray to look similar.

## Audio

Unsigned 8-bit PCM, mono, **32,000 Hz** (1,600 samples × 20 frames). Silence is 128.

## Header

```
EE 37  b2  b3  keystream…
```

- `EE 37` in every file.
- `b3 = k XOR C0`, where `k` is a per-file key; `b2` also changes per file.
- Then a fixed byte sequence XOR `k`, with a period of 510 bytes. Decoded, it looks like a shuffled CRC-16
  (Modbus) table.
- The header ends where that sequence stops matching. Its length changes per file: 124 to 896 bytes in the
  samples, always a multiple of 4.

No rule linking `b2`/`k` to the header length was found, and they are not a checksum of the content
(sum, XOR, frame count and length were all checked). HoloFan therefore writes, unchanged, the 292-byte
header of one original file that the fan plays. The fan accepts it.

| original | b2 | k  | header bytes |
|----------|----|----|-------------:|
| 1        | 58 | 42 | 312 |
| 2        | 40 | 9D | 896 |
| 3        | 8C | 96 | 312 |
| 4        | 11 | 43 | 496 |
| 5        | 7F | A8 | 868 |
| 6        | DE | D1 | 524 |
| 7        | 06 | 33 | 584 |
| 8        | 62 | 07 | 696 |
| 9        | 8A | 55 | 124 |
| 10       | 22 | 34 | 292 |

## File names

All original names are at most 10 bytes in UTF-8 (`O Poder do`, `A Vida é ` — an accented letter takes
two bytes). It's not known whether the fan or only the old software needs that, so HoloFan keeps names at
10 bytes.
