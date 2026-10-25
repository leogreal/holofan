"""Preview a hologram-fan .bin file: render frames as PNG images and extract the audio as WAV.

    pip install numpy pillow
    python holo_preview.py VIDEO.bin                  # frame at 10%, 50% and 90% -> VIDEO_preview.png
    python holo_preview.py VIDEO.bin --at 0.25 --wav  # one frame, plus VIDEO.wav

See docs/FORMAT.md for the layout this decodes.
"""
import argparse
import os
import wave

import numpy as np
from PIL import Image

LINES, LEDS, PLANES = 300, 48, 5
IMAGE_BYTES = LINES * LEDS * 3 * PLANES // 8   # 27,000
AUDIO_BYTES = 1600
FRAME_BYTES = IMAGE_BYTES + AUDIO_BYTES        # 28,600
TRAILER = 100_000
SAMPLE_RATE = 32_000


def open_bin(path):
    data = np.fromfile(path, dtype=np.uint8)
    if data[0] != 0xEE or data[1] != 0x37:
        raise SystemExit(f"{path}: does not start with EE 37, not this format")
    body = len(data) - TRAILER
    header = body % FRAME_BYTES
    frames = body // FRAME_BYTES
    return data, header, frames


def decode_frame(buf):
    """28,600 bytes -> (RGB values 0..31 as [line, led, 3], audio bytes)."""
    bits = np.unpackbits(buf[:IMAGE_BYTES]).reshape(LINES, PLANES, LEDS, 3)
    weights = (1 << np.arange(PLANES - 1, -1, -1)).reshape(1, PLANES, 1, 1)
    bgr = (bits * weights).sum(axis=1)
    return bgr[..., ::-1].astype(np.uint8), buf[IMAGE_BYTES:]


def to_circle(rgb5, size=400):
    """Polar -> picture. Line 0 at 3 o'clock, counter-clockwise; LED 0 is the outer one."""
    y, x = np.mgrid[0:size, 0:size]
    c = (size - 1) / 2
    dx, dy = x - c, y - c
    r = np.hypot(dx, dy) / (size / 2)
    theta = (np.arctan2(-dy, dx) / (2 * np.pi)) % 1.0
    line = np.clip((theta * LINES).astype(int), 0, LINES - 1)
    led = np.clip(((1 - r) * LEDS).astype(int), 0, LEDS - 1)
    img = (rgb5[line, led].astype(np.uint16) * 255 // 31).astype(np.uint8)
    img[r > 1] = 0
    return Image.fromarray(img)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bin")
    ap.add_argument("--at", type=float, action="append", help="position 0..1 (repeatable)")
    ap.add_argument("--size", type=int, default=400)
    ap.add_argument("--wav", action="store_true", help="also write the audio as a WAV file")
    args = ap.parse_args()

    data, header, frames = open_bin(args.bin)
    print(f"header {header} bytes, {frames} frames ({frames / 20:.1f} s at 20 fps)")
    stem = os.path.splitext(args.bin)[0]

    tiles = []
    for at in args.at or [0.1, 0.5, 0.9]:
        n = min(frames - 1, int(at * frames))
        rgb5, _ = decode_frame(data[header + n * FRAME_BYTES: header + (n + 1) * FRAME_BYTES])
        tiles.append(to_circle(rgb5, args.size))
    sheet = Image.new("RGB", (len(tiles) * (args.size + 8), args.size), (32, 32, 32))
    for i, t in enumerate(tiles):
        sheet.paste(t, (i * (args.size + 8), 0))
    sheet.save(stem + "_preview.png")
    print("wrote", stem + "_preview.png")

    if args.wav:
        body = data[header: header + frames * FRAME_BYTES].reshape(frames, FRAME_BYTES)
        with wave.open(stem + ".wav", "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(1)  # 8-bit WAV is unsigned, same as the file
            w.setframerate(SAMPLE_RATE)
            w.writeframes(body[:, IMAGE_BYTES:].tobytes())
        print("wrote", stem + ".wav")


if __name__ == "__main__":
    main()
