#!/usr/bin/env python3
"""Check sparse animation, sequence waits, STILL and loop interruption on the native GPU host."""
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

red, blue, green, yellow, black = ((255, 0, 0, 255), (0, 0, 255, 255), (0, 128, 0, 255), (255, 255, 0, 255), (0, 0, 0, 255))
with tempfile.TemporaryDirectory(prefix='aether-frame-sequence-') as cache:
    for width, height in ((320, 180), (641, 481)):
        handle = C.c_void_p()
        def check(result):
            if result:
                raise RuntimeError((lib.aether_n2system_last_error(handle) or b'Unknown host error').decode())
        def tick(seconds=0):
            check(lib.aether_n2system_tick(handle, seconds))
        def pixels():
            frame = Frame()
            check(lib.aether_n2system_frame(handle, C.byref(frame)))
            data = C.string_at(frame.pixels, frame.stride * frame.height)
            scale = min(width / 320, height / 180)
            left, top = (width - 320 * scale) / 2, (height - 180 * scale) / 2
            result = []
            for x in (80, 240):
                offset = int(top + 90 * scale) * frame.stride + int(left + x * scale) * 4
                result.append(tuple(data[offset:offset + 4]))
            return tuple(result)
        def wait_for(expected):
            # Texture decoding runs asynchronously; zero-delta frames resolve it
            # without consuming authored frame intervals.
            for _ in range(300):
                tick()
                if pixels() == expected:
                    tick()
                    tick()
                    return
            raise AssertionError(('frame did not become ready', expected, pixels()))
        def advance():
            check(lib.aether_n2system_event(handle, 1, 40, 0, 0))
            tick()
            check(lib.aether_n2system_event(handle, 2, 40, 0, 0))
            tick()

        check(lib.aether_n2system_create(str(root / 'aether_runtime/fixtures/frame_sequence').encode(),
                                      str(pathlib.Path(cache) / str(width)).encode(), width, height, C.byref(handle)))
        try:
            wait_for((red, red))
            tick(.065)
            assert pixels() == (red, red), ('START interval', pixels())
            tick(.001)
            assert pixels() == (blue, red), ('first frame and synchronous wait', pixels())
            tick(.066)
            assert pixels() == (blue, red), ('missing frame did not retain prior image', pixels())
            tick(.066)
            wait_for((yellow, green))
            advance()
            wait_for((red, red))
            tick(.033)
            assert pixels() == (blue, red), ('loop did not advance', pixels())
            advance()
            wait_for((green, green))
            advance()
            wait_for((red, red))
            tick(.033)
            wait_for((black, green))
            print(f'{width}x{height}: sparse frame timing, synchronous wait, STILL, loop input and slot deletion passed')
        finally:
            lib.aether_n2system_destroy(handle)
