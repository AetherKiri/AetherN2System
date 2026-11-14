#!/usr/bin/env python3
"""Verify SCREEN/VIDEO snapshots and passive Plain effects through the NativeAOT ABI."""
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
    for width, height in ((320, 180),):
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
        check(lib.aether_n2system_create(str(root / 'aether_runtime/fixtures/plain_blur').encode(),
            str(pathlib.Path(cache) / str(width)).encode(), width, height, C.byref(handle)))
        try:
            deadline = time.monotonic() + 5
            while True:
                tick()
                initial = [sample(x, 60)[0] for x in (100, 101, 102, 103)]
                if max(initial) - min(initial) > 150 and sample(100, 60)[1] < 20:
                    break
                if time.monotonic() >= deadline:
                    raise AssertionError('initial blur pattern did not finish loading')
                time.sleep(.01)
            before = [sample(x, 60)[0] for x in (100, 101, 102, 103)]
            assert max(before) - min(before) > 150, before
            advance()
            blurred = [sample(x, 60)[0] for x in (100, 101, 102, 103)]
            assert max(blurred) - min(blurred) < 12, blurred
            assert all(70 < value < 180 for value in blurred), blurred
            tick(10)
            assert [sample(x, 60)[0] for x in (100, 101, 102, 103)] == blurred
            advance()
            restored = [sample(x, 60)[0] for x in (100, 101, 102, 103)]
            assert restored == before, (before, restored)
            advance()
            crop = [sample(x, 60)[0] for x in (40, 41, 42, 43)]
            assert max(crop) - min(crop) > 100, crop
            assert sample(200, 60)[:3] == (0, 0, 0)
            advance()
            blurred_crop = [sample(x, 60)[0] for x in (40, 41, 42, 43)]
            assert max(blurred_crop) - min(blurred_crop) < 12, blurred_crop
            assert all(70 < value < 180 for value in blurred_crop), blurred_crop
            assert sample(200, 60)[:3] == (0, 0, 0)
            advance()
            assert [sample(x, 60)[0] for x in (40, 41, 42, 43)] == crop
            advance()
            strongest = [sample(x, 60)[0] for x in (40, 41, 42, 43)]
            assert max(strongest) - min(strongest) < 12, strongest
            # Every native box-blur pass attenuates both color and alpha.
            # The strongest 32-pass shade is visibly darker than MEDIUM.
            assert all(50 < value < 100 for value in strongest), strongest
            advance()
            assert [sample(x, 60)[0] for x in (40, 41, 42, 43)] == strongest, 'NORMAL must retain the active shade'
            advance()
            assert [sample(x, 60)[0] for x in (40, 41, 42, 43)] == crop
            print(f'{width}x{height}: passive/cropped GPU blur, NOMORE32, NORMAL retention and null restoration passed')
        finally:
            lib.aether_n2system_destroy(handle)
