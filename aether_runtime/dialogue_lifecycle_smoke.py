#!/usr/bin/env python3
"""Check prepared/entered/retired dialogue pages through rendered NativeAOT frames."""
import ctypes as C
import pathlib
import sys
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent
lib = C.CDLL(str(pathlib.Path(sys.argv[1]).resolve()))


class Frame(C.Structure):
    _fields_ = [('pixels', C.POINTER(C.c_uint8)), ('width', C.c_uint32),
                ('height', C.c_uint32), ('stride', C.c_uint32), ('sequence', C.c_uint64)]


lib.aether_n2system_create.argtypes = [C.c_char_p, C.c_char_p, C.c_uint32, C.c_uint32, C.POINTER(C.c_void_p)]
lib.aether_n2system_last_error.argtypes = [C.c_void_p]
lib.aether_n2system_last_error.restype = C.c_char_p
lib.aether_n2system_tick.argtypes = [C.c_void_p, C.c_double]
lib.aether_n2system_frame.argtypes = [C.c_void_p, C.POINTER(Frame)]
lib.aether_n2system_event.argtypes = [C.c_void_p, C.c_uint32, C.c_int32, C.c_float, C.c_float]
lib.aether_n2system_destroy.argtypes = [C.c_void_p]

with tempfile.TemporaryDirectory(prefix='aether-dialogue-lifecycle-') as cache:
    handle = C.c_void_p()

    def check(result):
        if result:
            raise RuntimeError((lib.aether_n2system_last_error(handle) or b'Unknown error').decode())

    def tick(count=1):
        for _ in range(count):
            check(lib.aether_n2system_tick(handle, 1 / 60))

    def region(left=20, top=20, right=620, bottom=300):
        frame = Frame()
        check(lib.aether_n2system_frame(handle, C.byref(frame)))
        data = C.string_at(frame.pixels, frame.stride * frame.height)
        return b''.join(data[y * frame.stride + left * 4:y * frame.stride + right * 4] for y in range(top, bottom))

    def phase():
        return tuple(region(10, 350, 11, 351))

    def click():
        check(lib.aether_n2system_event(handle, 4, 1, 500, 300))
        tick()
        check(lib.aether_n2system_event(handle, 5, 1, 500, 300))
        tick(2)

    check(lib.aether_n2system_create(str(root / 'aether_runtime/fixtures/dialogue_lifecycle').encode(),
                                   cache.encode(), 640, 360, C.byref(handle)))
    try:
        tick(30)
        assert phase() == (255, 0, 0, 255), 'WaitText blocked before Enter'
        assert set(region()[::4]) == {0}, 'loaded page appeared before its window'
        click()
        for _ in range(20):
            tick()
            # The backdrop is uniform even during its fade; any bright glyph breaks this.
            assert max(region()[::4]) <= 32, 'text appeared during the window fade'
        assert phase() == (0, 128, 0, 255), 'window fade did not finish'
        backdrop = region()
        click()
        tick(5)
        assert max(region()[::4]) > 100, 'Enter did not start reading'
        assert phase() == (0, 128, 0, 255), 'active WaitText returned before input'
        click()
        tick(5)
        assert phase() == (0, 0, 255, 255), 'deferred Disused did not release WaitText'
        assert region() == backdrop, 'Disused retained completed text'
        click()
        tick(5)
        assert max(region()[::4]) > 100, 'second page did not enter'
        click()
        tick(5)
        assert phase() == (255, 255, 255, 255), 'completed page did not retire'
        assert region() == backdrop, 'immediate Disused retained completed text'
        print('Prepared WaitText, hidden load, window fade, Enter, active wait and both Disused paths passed')
    finally:
        lib.aether_n2system_destroy(handle)
