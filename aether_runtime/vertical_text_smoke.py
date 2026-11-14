#!/usr/bin/env python3
"""Check vertical dialogue geometry and font restoration through the native ABI."""
import argparse
import ctypes as C
import json
from pathlib import Path
import struct
import tempfile
import time
import zlib

ROOT = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("library", type=Path)
parser.add_argument("--output", type=Path, help="Keep RGBA screenshots and measured bounds here")
args = parser.parse_args()
output = (args.output or Path(tempfile.mkdtemp(prefix="aether-vertical-text-"))).resolve()
output.mkdir(parents=True, exist_ok=True)
lib = C.CDLL(str(args.library.resolve()))


class Frame(C.Structure):
    _fields_ = [("pixels", C.POINTER(C.c_uint8)), ("width", C.c_uint32),
                ("height", C.c_uint32), ("stride", C.c_uint32), ("sequence", C.c_uint64)]


lib.aether_n2system_create.argtypes = [C.c_char_p, C.c_char_p, C.c_uint32, C.c_uint32, C.POINTER(C.c_void_p)]
lib.aether_n2system_last_error.argtypes = [C.c_void_p]
lib.aether_n2system_last_error.restype = C.c_char_p
lib.aether_n2system_tick.argtypes = [C.c_void_p, C.c_double]
lib.aether_n2system_frame.argtypes = [C.c_void_p, C.POINTER(Frame)]
lib.aether_n2system_event.argtypes = [C.c_void_p, C.c_uint32, C.c_int32, C.c_float, C.c_float]
lib.aether_n2system_destroy.argtypes = [C.c_void_p]
handle = C.c_void_p()


def check(result):
    if result:
        raise RuntimeError((lib.aether_n2system_last_error(handle) or b"Unknown host error").decode())


def tick():
    check(lib.aether_n2system_tick(handle, 1 / 60))


def frame():
    info = Frame()
    check(lib.aether_n2system_frame(handle, C.byref(info)))
    assert (info.width, info.height) == (800, 600), "Unexpected fixture surface size"
    data = C.string_at(info.pixels, info.stride * info.height)
    return b"".join(data[y * info.stride:y * info.stride + 3200] for y in range(600))


def mask(data, color=None):
    points = set()
    for y in range(10, 600):
        for x in range(800):
            i = (y * 800 + x) * 4
            r, g, b = data[i:i + 3]
            if color == "red":
                selected = r > 80 and r > g * 2 + 20 and r > b * 2 + 20
            elif color == "green":
                selected = g > 40 and g > r * 2 + 20 and g > b * 2 + 20
            elif color == "blue":
                selected = b > 80 and b > r * 2 + 20 and b > g * 2 + 20
            elif color == "gold":
                selected = r > 100 and g > 100 and b < 100
            else:
                selected = max(r, g, b) > 40
            if selected:
                points.add((x, y))
    return points


def bounds(points):
    assert points, "Expected nonempty text pixels"
    xs, ys = zip(*points)
    return min(xs), min(ys), max(xs) + 1, max(ys) + 1


def center(points):
    x0, y0, x1, y1 = bounds(points)
    return (x0 + x1) / 2, (y0 + y1) / 2


def normalize(points):
    x0, y0, _, _ = bounds(points)
    return {(x - x0, y - y0) for x, y in points}


def shape_score(first, second):
    first, second = normalize(first), normalize(second)
    return max(len(first & moved) / len(first | moved)
               for dx in range(-2, 3) for dy in range(-2, 3)
               for moved in [{(x + dx, y + dy) for x, y in second}])


def png(path, data):
    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))
    scanlines = b"".join(b"\0" + data[y * 3200:(y + 1) * 3200] for y in range(600))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 800, 600, 8, 6, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(scanlines)) + chunk(b"IEND", b""))


def stage(name, marker):
    previous = None
    stable = 0
    for _ in range(120):
        tick()
        data = frame()
        if tuple(data[4 * (800 + 1):4 * (800 + 1) + 3]) != marker:
            time.sleep(.005)
            continue
        # Font uploads can finish after the phase marker first appears.
        if data == previous:
            stable += 1
        else:
            stable = 0
        previous = data
        if stable >= 3 and len(mask(data, "red")) > 20:
            png(output / (name + ".png"), data)
            return data
        time.sleep(.005)
    raise AssertionError("The fixture did not settle at stage " + name)


def advance():
    check(lib.aether_n2system_event(handle, 1, 40, 0, 0))
    tick()
    check(lib.aether_n2system_event(handle, 2, 40, 0, 0))
    tick()


with tempfile.TemporaryDirectory(prefix="aether-vertical-cache-") as cache:
    check(lib.aether_n2system_create(str(ROOT / "aether_runtime/fixtures/vertical_text").encode(),
                                  cache.encode(), 800, 600, C.byref(handle)))
    try:
        horizontal = stage("01-horizontal-control", (255, 0, 0))
        advance()
        vertical = stage("02-vertical", (0, 255, 0))
        advance()
        atfont = stage("03-at-font-control", (0, 0, 255))
        advance()
        restored = stage("04-horizontal-restored", (255, 255, 255))

        red, green, blue = (mask(vertical, color) for color in ("red", "green", "blue"))
        red_center, green_center, blue_center = center(red), center(green), center(blue)
        text_bounds = bounds(mask(vertical))
        x0, y0, x1, y1 = text_bounds
        assert 60 <= x0 < x1 <= 750 and 56 <= y0 < y1 <= 544, ("Text escaped BOX2 inner rectangle", text_bounds)
        assert green_center[1] > red_center[1] + 12 and abs(green_center[0] - red_center[0]) < 5, ("Glyphs did not flow down", red_center, green_center)
        assert blue_center[0] < red_center[0] - 20, ("Wrapped columns did not move right to left", red_center, blue_center)

        reference = mask(horizontal, "red")
        upright = shape_score(reference, red)
        rotated = shape_score({(-y, x) for x, y in reference}, red)
        assert upright >= .75 and upright > rotated + .12, ("CJK glyph is sideways", upright, rotated)
        # Exclude the phase marker from pixel identity comparisons.
        assert vertical[10 * 3200:] == atfont[10 * 3200:], "Orientation API differs from the @ font reference"
        assert horizontal[10 * 3200:] == restored[10 * 3200:], "Returning to horizontal leaked orientation, size, or color"
        assert len(mask(restored, "gold")) > 20, "Configured default font color was not retained"
        metrics = {"inner_rectangle": [60, 56, 750, 544], "text_bounds": text_bounds,
                   "first_glyph_center": red_center, "second_glyph_center": green_center,
                   "wrapped_last_glyph_center": blue_center, "upright_iou": upright,
                   "sideways_iou": rotated, "at_font_reference_identical": True,
                   "horizontal_restored_identical": True}
        (output / "metrics.json").write_text(json.dumps(metrics, indent=2) + "\n")
        print(json.dumps(metrics))
        print("Vertical BOX bounds, downward flow, right-to-left wrapping, upright CJK, @ font reference and horizontal restoration passed; artifacts: " + str(output))
    finally:
        lib.aether_n2system_destroy(handle)
