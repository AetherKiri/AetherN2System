#!/usr/bin/env python3
"""Exercise the real NativeAOT C ABI against original manual tests and a plain NPA."""
import ctypes as C, pathlib, sys, tempfile, struct, json, shutil, time
root=pathlib.Path(__file__).resolve().parent.parent
lib=C.CDLL(str(pathlib.Path(sys.argv[1] if len(sys.argv)>1 else root/'aether_runtime/build/osx-arm64/aether_n2system.dylib').resolve()))
class Frame(C.Structure): _fields_=[('pixels',C.POINTER(C.c_uint8)),('width',C.c_uint32),('height',C.c_uint32),('stride',C.c_uint32),('sequence',C.c_uint64)]
lib.aether_n2system_create.argtypes=[C.c_char_p,C.c_char_p,C.c_uint32,C.c_uint32,C.POINTER(C.c_void_p)]
lib.aether_n2system_last_error.argtypes=[C.c_void_p];lib.aether_n2system_last_error.restype=C.c_char_p
lib.aether_n2system_tick.argtypes=[C.c_void_p,C.c_double]
lib.aether_n2system_frame.argtypes=[C.c_void_p,C.POINTER(Frame)]
lib.aether_n2system_event.argtypes=[C.c_void_p,C.c_uint32,C.c_int32,C.c_float,C.c_float]
lib.aether_n2system_pause.argtypes=[C.c_void_p,C.c_int]
lib.aether_n2system_status.argtypes=[C.c_void_p]
lib.aether_n2system_destroy.argtypes=[C.c_void_p]
def plain_npa(destination,files):
    index=bytearray();payload=bytearray()
    for i,(name,data) in enumerate(files.items()):
        encoded=bytes((value-((0xfc*j-i)&255))&255 for j,value in enumerate(name.encode('shift_jis')))
        index+=struct.pack('<I',len(encoded))+encoded+struct.pack('<BIIII',2,0,len(payload),len(data),len(data));payload+=data
    header=b'NPA\x01\0\0\0'+struct.pack('<IIBBIIIQI',0,0,0,0,len(files),0,len(files),0,len(index))
    destination.write_bytes(header+index+payload)
with tempfile.TemporaryDirectory() as temporary:
    temporary=pathlib.Path(temporary);packed=temporary/'packed';packed.mkdir()
    fixture=root/'aether_runtime/fixtures/host_smoke'
    (packed/'AETHER-N2SYSTEM.JSON').write_bytes((fixture/'aether-n2system.json').read_bytes())
    shutil.copytree(fixture/'assets', packed/'assets')
    plain_npa(packed/'NSS.NPA',{'boot.nss':(fixture/'nss/boot.nss').read_bytes()})
    for cycle,game in enumerate((root/'manual_tests',fixture,packed,fixture)):
        h=C.c_void_p()
        def check(value):
            if value: raise RuntimeError((lib.aether_n2system_last_error(h) or b'unknown host error').decode())
        def tick(): check(lib.aether_n2system_tick(h,1/60))
        def pixel():
            f=Frame();check(lib.aether_n2system_frame(h,C.byref(f)));offset=90*f.stride+160*4
            return bytes(f.pixels[offset+i] for i in range(4)),f.sequence
        def advance():
            check(lib.aether_n2system_event(h,1,40,0,0));tick();check(lib.aether_n2system_event(h,2,40,0,0));tick();tick()
        check(lib.aether_n2system_create(str(game).encode(),str(temporary/f'cache-{cycle}').encode(),320,180,C.byref(h)))
        for _ in range(5): tick()
        color,sequence=pixel();assert color==bytes([0,0,255,255]),color
        check(lib.aether_n2system_pause(h,1));tick();assert pixel()[1]==sequence
        check(lib.aether_n2system_pause(h,0))
        if game!=root/'manual_tests':
            advance();assert pixel()[0]==bytes([255,0,0,255]),pixel()
            advance();assert pixel()[0]==bytes([0,128,0,255]),pixel()
            advance()
            for _ in range(100):
                if pixel()[0]==bytes([255,255,255,255]): break
                time.sleep(0.005);tick()
            assert pixel()[0]==bytes([255,255,255,255]),pixel()
            advance();assert lib.aether_n2system_status(h)==2
        else: advance()
        lib.aether_n2system_destroy(h);lib.aether_n2system_destroy(h)
        print(f'cycle {cycle}: {game.name}: RGBA, pause/resume, input, destroy passed')
    # A compressed NPA must return a diagnostic across the ABI and leave no handle.
    compressed=bytearray((packed/'NSS.NPA').read_bytes());compressed[15]=1;(packed/'NSS.NPA').write_bytes(compressed)
    h=C.c_void_p();assert lib.aether_n2system_create(str(packed).encode(),str(temporary/'bad').encode(),320,180,C.byref(h))==-1
    assert not h.value and b'Compression is not supported' in lib.aether_n2system_last_error(None)
    print('compressed NPA diagnostic passed')
