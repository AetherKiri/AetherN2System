#!/usr/bin/env python3
"""Verify shared screen masks, alpha and transition completion through the native ABI."""
import ctypes as C
import pathlib
import sys
import tempfile
import time

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

with tempfile.TemporaryDirectory(prefix='aether-mask-transition-') as cache:
    for width, height in ((320, 180), (640, 480), (853, 479)):
        handle = C.c_void_p()
        def check(result):
            if result:
                raise RuntimeError((lib.aether_n2system_last_error(handle) or b'Unknown host error').decode())
        def tick(count=1):
            for _ in range(count):
                check(lib.aether_n2system_tick(handle, 1 / 60))
        def sample(x, y):
            frame = Frame()
            check(lib.aether_n2system_frame(handle, C.byref(frame)))
            scale = min(width / 320, height / 180)
            px, py = int((width - 320 * scale) / 2 + x * scale), int((height - 180 * scale) / 2 + y * scale)
            offset = py * frame.stride + px * 4
            return tuple(C.string_at(C.addressof(frame.pixels.contents) + offset, 4))
        def near(actual, expected):
            assert all(abs(a - b) <= 2 for a, b in zip(actual, expected)), (actual, expected)
        def advance(count=30):
            check(lib.aether_n2system_event(handle, 1, 40, 0, 0))
            tick()
            check(lib.aether_n2system_event(handle, 2, 40, 0, 0))
            tick(count)
        check(lib.aether_n2system_create(str(root / 'aether_runtime/fixtures/mask_transition').encode(),
            str(pathlib.Path(cache) / str(width)).encode(), width, height, C.byref(handle)))
        try:
            # Resolve worker-thread asset decoding without advancing authored time.
            points = ((40, 20), (120, 60), (200, 60), (120, 120), (200, 120), (310, 170))
            expected = ((255, 0, 0, 255), (0, 0, 255, 255), (0, 0, 0, 255),
                        (0, 128, 127, 255), (0, 0, 0, 255), (0, 0, 0, 255))
            deadline = time.monotonic() + 5
            while True:
                check(lib.aether_n2system_tick(handle, 0))
                actual = tuple(sample(x, y) for x, y in points)
                if all(all(abs(a - b) <= 2 for a, b in zip(color, target))
                       for color, target in zip(actual, expected)):
                    break
                if time.monotonic() >= deadline:
                    raise AssertionError(('initial mask scene did not finish loading', actual, expected))
                time.sleep(.005)
            near(sample(40, 20), (255, 0, 0, 255))
            near(sample(120, 60), (0, 0, 255, 255))
            near(sample(200, 60), (0, 0, 0, 255))
            near(sample(120, 120), (0, 128, 127, 255))
            near(sample(200, 120), (0, 0, 0, 255))
            near(sample(310, 170), (0, 0, 0, 255))
            advance(5)
            near(sample(310, 170), (0, 0, 0, 255))
            tick(25)
            near(sample(200, 60), (0, 0, 255, 255))
            near(sample(200, 120), (0, 128, 127, 255))
            near(sample(310, 170), (0, 128, 0, 255))
            advance()
            near(sample(40, 20), (0, 0, 0, 255))
            near(sample(120, 120), (0, 0, 0, 255))
            tick(20)
            near(sample(120, 120), (0, 0, 0, 255))
            print(f'{width}x{height}: shared screen mask, premultiplied alpha, wait and retained final state passed')
        finally:
            lib.aether_n2system_destroy(handle)
