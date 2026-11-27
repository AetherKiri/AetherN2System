#!/usr/bin/env python3
"""Verify SCREEN/VIDEO snapshots and passive Plain effects through the NativeAOT ABI."""
import ctypes as C
import pathlib
import sys
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent
library = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else root / 'aether_runtime/build/osx-arm64/aether_n2system.dylib'
lib = C.CDLL(str(library.resolve()))

class Frame(C.Structure):
    _fields_ = [('pixels', C.POINTER(C.c_uint8)), ('width', C.c_uint32), ('height', C.c_uint32), ('stride', C.c_uint32), ('sequence', C.c_uint64)]

lib.aether_n2system_create.argtypes = [C.c_char_p, C.c_char_p, C.c_uint32, C.c_uint32, C.POINTER(C.c_void_p)]
lib.aether_n2system_last_error.argtypes = [C.c_void_p]
lib.aether_n2system_last_error.restype = C.c_char_p
lib.aether_n2system_tick.argtypes = [C.c_void_p, C.c_double]
lib.aether_n2system_frame.argtypes = [C.c_void_p, C.POINTER(Frame)]
lib.aether_n2system_event.argtypes = [C.c_void_p, C.c_uint32, C.c_int32, C.c_float, C.c_float]
lib.aether_n2system_destroy.argtypes = [C.c_void_p]

with tempfile.TemporaryDirectory(prefix='aether-screen-') as cache:
    for width, height in ((320, 180), (640, 360), (640, 480), (1280, 720), (641, 481), (853, 479)):
        handle = C.c_void_p()
        def check(result):
            if result:
                raise RuntimeError((lib.aether_n2system_last_error(handle) or b'Unknown host error').decode())
        def tick(count=1):
            for _ in range(count):
                check(lib.aether_n2system_tick(handle, 1 / 60))
        def pixels():
            frame = Frame()
            check(lib.aether_n2system_frame(handle, C.byref(frame)))
            assert (frame.width, frame.height) == (width, height)
            data = C.string_at(frame.pixels, frame.stride * frame.height)
            scale = min(width / 320, height / 180)
            left, top = (width - 320 * scale) / 2, (height - 180 * scale) / 2
            def sample(x, y):
                offset = y * frame.stride + x * 4
                return tuple(data[offset:offset + 4])
            # Sample inside the game viewport, including when its edges are fractional.
            result = tuple(sample(int(left + x * scale), int(top + y * scale))
                           for x, y in ((80, 45), (240, 45), (80, 135), (240, 135)))
            if top >= 2:
                assert sample(width // 2, 0) == (0, 0, 0, 255), 'top letterbox changed'
            if left >= 2:
                assert sample(0, height // 2) == (0, 0, 0, 255), 'side letterbox changed'
            return result
        def advance():
            check(lib.aether_n2system_event(handle, 1, 40, 0, 0))
            tick()
            check(lib.aether_n2system_event(handle, 2, 40, 0, 0))
            tick(10)

        check(lib.aether_n2system_create(str(root / 'aether_runtime/fixtures/screen_capture').encode(),
                                      str(pathlib.Path(cache) / str(width)).encode(), width, height, C.byref(handle)))
        try:
            tick(10)
            expected = ((255, 0, 0, 255),) * 3 + ((0, 0, 255, 255),)
            assert pixels() == expected, ('original frame', pixels())
            advance()
            assert pixels() == ((0, 128, 0, 255),) * 4, ('next frame', pixels())
            for source in ('SCREEN image clone', 'VIDEO image clone', 'direct SCREEN', 'direct VIDEO'):
                advance()
                assert pixels() == expected, (source, pixels())
                tick(30)
                assert pixels() == expected, (source + ' changed after later frames', pixels())
            advance()
            cropped = ((0, 0, 255, 255),) + ((0, 128, 0, 255),) * 3
            assert pixels() == cropped, ('design-coordinate source crop', pixels())
            tick(30)
            assert pixels() == cropped, ('deleted source image crop ownership', pixels())
            advance()
            assert pixels() == ((0, 128, 0, 255),) * 4, ('snapshot deletion', pixels())
            advance()
            assert pixels() == expected, ('Plain source frame', pixels())
            advance()
            assert pixels() == expected, ('Passive snapshot and locked deletion', pixels())
            tick(30)
            assert pixels() == expected, ('Passive changed after source deletion', pixels())
            advance()
            assert pixels() == ((0, 128, 0, 255),) * 4, ('unlocked Plain deletion', pixels())
            advance()
            assert pixels() == expected, ('Plain crop source frame', pixels())
            advance()
            assert pixels() == cropped, ('Passive design crop moved to origin', pixels())
            tick(30)
            assert pixels() == cropped, ('Passive crop changed after later frames', pixels())
            advance()
            faded = pixels()
            assert faded[:3] == ((0, 128, 0, 255),) * 3, ('Passive move background', faded)
            assert all(abs(actual - target) <= 1 for actual, target in zip(faded[3], (0, 64, 128, 255))), ('Passive move and fade', faded)
            advance()
            assert pixels() == ((0, 128, 0, 255),) * 4, ('Passive crop deletion', pixels())
            advance()
            green, red = (0, 128, 0, 255), (255, 0, 0, 255)
            assert pixels() == (green, red, green, red), ('Draw captures only the lower layers', pixels())
            advance()
            assert pixels() == (red, green, red, green), ('Surface survives detaching and moving', pixels())
            advance()
            assert pixels() == (green,) * 4, ('Surface deletion', pixels())
            print(f'{width}x{height}: SCREEN/VIDEO snapshots, image lifetime, Passive freeze/crop/move/fade/lock and scaling passed')
        finally:
            lib.aether_n2system_destroy(handle)
