#!/usr/bin/env python3
"""Check composited scenes and native cumulative transition timing."""
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

with tempfile.TemporaryDirectory(prefix='aether-legacy-transition-') as cache:
    for width, height in ((320, 180), (640, 480), (853, 479)):
        handle = C.c_void_p()
        def check(result):
            if result:
                raise RuntimeError((lib.aether_n2system_last_error(handle) or b'Unknown host error').decode())
        def tick(count=1, seconds=0):
            for _ in range(count):
                check(lib.aether_n2system_tick(handle, seconds))
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
        check(lib.aether_n2system_create(str(root / 'aether_runtime/fixtures/legacy_transition').encode(),
            str(pathlib.Path(cache) / str(width)).encode(), width, height, C.byref(handle)))
        try:
            # Asset loading is asynchronous; only start the authored clock once
            # a complete initial scene has been presented.
            deadline = time.monotonic() + 5
            while True:
                tick()
                if sample(40, 40) == (255, 0, 0, 255) and sample(240, 40) == (255, 0, 0, 255):
                    tick(3)
                    if sample(40, 40) == (255, 0, 0, 255): break
                if time.monotonic() >= deadline: raise AssertionError('Initial scene did not load')
                time.sleep(.005)
            # k=2,4,6,8: weights for mask byte0 are8,16,24,32.
            tick(seconds=.032)
            coverage = 1 - (248/256) * (240/256) * (232/256) * (224/256)
            red = round(255 * (1 - coverage))
            near(sample(40, 40), (red, 0, round(255 * coverage), 255))
            # At overlap there is one mix of the composite green actor. Mixing
            # background and actor independently would leave a blue component.
            near(sample(120, 40), (red, round(128 * coverage), 0, 255))
            near(sample(240, 40), (255, 0, 0, 255))
            tick(seconds=.992)
            tick(2)
            near(sample(40, 40), (0, 0, 255, 255))
            near(sample(120, 40), (0, 128, 0, 255))
            near(sample(240, 40), (0, 0, 255, 255))
            near(sample(10, 170), (0, 128, 0, 255))
            advance(count=3)
            # Uniform cumulative transition: second native tick weighs2/256.
            tick(seconds=.008)
            near(sample(40, 40), (253, 0, 2, 255))
            tick(seconds=1.016)
            tick(2)
            near(sample(40, 40), (0, 0, 255, 255))
            near(sample(10, 170), (0, 128, 0, 255))
            advance(count=3)
            near(sample(40, 40), (255, 0, 0, 255))
            advance(count=3)
            tick(seconds=.032)
            before_left = sample(120, 40)
            before_right = sample(200, 40)
            assert before_left[1] > 0 and before_right[1] == 0
            advance(count=1)
            tick(seconds=.032)
            after_left = sample(120, 40)
            after_right = sample(200, 40)
            # The actor moves while fading. Feedback retains some earlier green
            # on the left and introduces green at the new right-hand position.
            # A static capture has no green at the new position; replacing the
            # history with the latest scene loses the green on the left.
            assert 0 < after_left[1] < before_left[1], (before_left, after_left)
            assert after_right[1] > before_right[1], (before_right, after_right)
            tick(seconds=1)
            tick(2)
            near(sample(120, 40), (0, 0, 255, 255))
            near(sample(200, 40), (0, 128, 0, 255))
            near(sample(10, 170), (0, 128, 0, 255))
            print(f'{width}x{height}: composited scene, cumulative mask/uniform timing, moving feedback and wait passed')
        finally:
            lib.aether_n2system_destroy(handle)
