#!/usr/bin/env python3
"""Verify text fade and click ordering through the NativeAOT ABI."""
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

with tempfile.TemporaryDirectory(prefix='aether-typewriter-') as cache:
    handle = C.c_void_p()
    def check(result):
        if result:
            raise RuntimeError((lib.aether_n2system_last_error(handle) or b'Unknown host error').decode())
    def tick(count=1):
        for _ in range(count):
            check(lib.aether_n2system_tick(handle, 1 / 60))
    def region(left=20, top=20, right=620, bottom=300):
        frame = Frame()
        check(lib.aether_n2system_frame(handle, C.byref(frame)))
        data = C.string_at(frame.pixels, frame.stride * frame.height)
        return b''.join(data[y * frame.stride + left * 4:y * frame.stride + right * 4] for y in range(top, bottom))
    def pixel(x, y):
        return tuple(region(x, y, x + 1, y + 1))
    def click():
        check(lib.aether_n2system_event(handle, 4, 1, 500, 300))
        tick()
        check(lib.aether_n2system_event(handle, 5, 1, 500, 300))
        tick()
    def brightness(data):
        return sum(data[i] + data[i + 1] + data[i + 2] for i in range(0, len(data), 4))

    check(lib.aether_n2system_create(str(root / 'aether_runtime/fixtures/typewriter_smoke').encode(),
                                  cache.encode(), 640, 360, C.byref(handle)))
    try:
        tick(4)
        early = brightness(region())
        tick(6)
        assert brightness(region()) > early, 'text did not fade in over time'
        click()
        revealed = region()
        assert brightness(revealed) > early, 'click did not reveal current sentence'
        tick(10)
        assert region() == revealed, 'revealing click also passed the inline halt'
        assert pixel(10, 350) == (0, 0, 0, 255), 'inline code executed too early'
        click()
        tick(160)
        assert pixel(10, 350) == (0, 0, 255, 255), 'inline code did not execute after the preceding text'
        tick(180)
        prior_page = region()
        assert pixel(630, 350) == (0, 0, 0, 255), 'WaitText completed before the blank page was read'
        tick(10)
        assert region() == prior_page, 'blank line advanced without input'
        click()
        assert region() != prior_page, 'blank line did not clear the previous page'
        click()
        assert pixel(630, 350) == (0, 0, 0, 255), 'revealing the final sentence completed WaitText'
        click()
        tick(2)
        assert pixel(630, 350) == (0, 128, 0, 255), 'final advance did not complete WaitText'
        print('Typewriter fade, reveal click, inline halt, ordered code, blank page and WaitText passed')
    finally:
        lib.aether_n2system_destroy(handle)
