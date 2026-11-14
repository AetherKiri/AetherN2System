#!/usr/bin/env python3
"""Verify text pixels, dimensions, page advances and inline code through the NativeAOT ABI."""
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

with tempfile.TemporaryDirectory(prefix='aether-text-') as cache:
    handle = C.c_void_p()
    def check(result):
        if result:
            raise RuntimeError((lib.aether_n2system_last_error(handle) or b'Unknown host error').decode())
    def tick(count=1):
        for _ in range(count):
            check(lib.aether_n2system_tick(handle, 1 / 60))
    def frame():
        result = Frame()
        check(lib.aether_n2system_frame(handle, C.byref(result)))
        return C.string_at(result.pixels, result.stride * result.height), result.stride
    def pixel(x, y):
        data, stride = frame()
        offset = y * stride + x * 4
        return tuple(data[offset:offset + 4])
    def region(left, top, right, bottom):
        data, stride = frame()
        return b''.join(data[y * stride + left * 4:y * stride + right * 4] for y in range(top, bottom))
    def colored_pixels(data):
        return sum(any(data[i:i + 3]) for i in range(0, len(data), 4))
    def advance():
        check(lib.aether_n2system_event(handle, 1, 40, 0, 0))
        tick()
        check(lib.aether_n2system_event(handle, 2, 40, 0, 0))
        tick(30)

    check(lib.aether_n2system_create(str(root / 'aether_runtime/fixtures/text_smoke').encode(), cache.encode(), 640, 360, C.byref(handle)))
    try:
        tick(60)
        assert pixel(630, 10) == (0, 128, 0, 255), ('text dimensions failed', pixel(630, 10))
        assert colored_pixels(region(20, 10, 600, 125)) > 200, 'static text was not rendered'
        first = region(20, 140, 600, 330)
        assert colored_pixels(first) > 100, 'dialogue was not rendered'
        advance()
        continued = region(20, 140, 600, 330)
        assert colored_pixels(continued) > colored_pixels(first), 'inline halt did not append text'
        advance()
        second = region(20, 140, 600, 330)
        assert second != continued and any(px[0] > px[1] * 1.5 for px in (tuple(second[i:i + 3]) for i in range(0, len(second), 4))), 'font color markup failed after blank line'
        advance()
        assert pixel(10, 350) == (0, 0, 255, 255), 'inline code entity disappeared after execution'
        third = region(20, 140, 600, 330)
        assert third != second and colored_pixels(third) > colored_pixels(second), 'inline code or following text did not continue'
        assert pixel(630, 350) != (0, 128, 0, 255), 'WaitText returned before final advance'
        advance()
        assert pixel(630, 350) == (0, 128, 0, 255), 'WaitText did not resume'
        print('Text RGBA pixels, measured bounds, ruby/font markup, inline halt, page clear, inline code and WaitText passed')
    finally:
        lib.aether_n2system_destroy(handle)
