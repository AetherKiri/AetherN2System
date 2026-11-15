#!/usr/bin/env python3
"""Exercise persistent saves in separate NativeAOT processes and retain GPU evidence."""
import argparse
import ctypes as C
import hashlib
import json
import os
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import zlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
FIXTURE = ROOT / "aether_runtime/fixtures/save_load_smoke"
WIDTH, HEIGHT = 800, 600
RED = (255, 0, 0, 255)
BLUE = (0, 0, 255, 255)
YELLOW = (255, 255, 0, 255)
GREEN = (0, 128, 0, 255)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write_png(path, rgba):
    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
    rows = b"".join(b"\0" + rgba[y * WIDTH * 4:(y + 1) * WIDTH * 4] for y in range(HEIGHT))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 6, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


class Frame(C.Structure):
    _fields_ = [("pixels", C.POINTER(C.c_uint8)), ("width", C.c_uint32),
                ("height", C.c_uint32), ("stride", C.c_uint32), ("sequence", C.c_uint64)]


class Host:
    def __init__(self, library, writable, game):
        self.lib = C.CDLL(str(library))
        self.handle = C.c_void_p()
        for name, arguments in {
            "create": [C.c_char_p, C.c_char_p, C.c_uint32, C.c_uint32, C.POINTER(C.c_void_p)],
            "last_error": [C.c_void_p], "tick": [C.c_void_p, C.c_double],
            "frame": [C.c_void_p, C.POINTER(Frame)],
            "event": [C.c_void_p, C.c_uint32, C.c_int32, C.c_float, C.c_float],
            "destroy": [C.c_void_p], "status": [C.c_void_p],
        }.items():
            getattr(self.lib, "aether_n2system_" + name).argtypes = arguments
        self.lib.aether_n2system_last_error.restype = C.c_char_p
        self.check(self.lib.aether_n2system_create(str(game).encode(), str(writable).encode(), WIDTH, HEIGHT, C.byref(self.handle)))

    def check(self, result):
        if result:
            raise RuntimeError((self.lib.aether_n2system_last_error(self.handle) or b"Unknown host error").decode())

    def tick(self, count=1):
        for _ in range(count):
            self.check(self.lib.aether_n2system_tick(self.handle, 1 / 60))
            assert self.lib.aether_n2system_status(self.handle) == 0, "fixture exited or paused unexpectedly"
            time.sleep(0.003)

    def frame(self):
        frame = Frame()
        self.check(self.lib.aether_n2system_frame(self.handle, C.byref(frame)))
        assert (frame.width, frame.height) == (WIDTH, HEIGHT)
        raw = C.string_at(frame.pixels, frame.stride * frame.height)
        return b"".join(raw[y * frame.stride:y * frame.stride + WIDTH * 4] for y in range(HEIGHT))

    @staticmethod
    def pixel(frame, x, y):
        offset = (y * WIDTH + x) * 4
        return tuple(frame[offset:offset + 4])

    def key(self, scancode):
        self.check(self.lib.aether_n2system_event(self.handle, 1, scancode, 0, 0))
        self.tick()
        self.check(self.lib.aether_n2system_event(self.handle, 2, scancode, 0, 0))
        self.tick(5)

    def click(self, x, y):
        self.check(self.lib.aether_n2system_event(self.handle, 3, 0, x, y))
        self.check(self.lib.aether_n2system_event(self.handle, 4, 1, x, y))
        self.tick()
        self.check(self.lib.aether_n2system_event(self.handle, 5, 1, x, y))
        self.tick(5)

    def wait_color(self, color, marker, description):
        for _ in range(240):
            self.tick()
            frame = self.frame()
            if self.pixel(frame, 20, 580) == color and self.pixel(frame, 690, 70) == marker:
                return frame
        raise AssertionError((description, self.pixel(frame, 20, 580), self.pixel(frame, 690, 70)))

    def red(self):
        self.wait_color(RED, YELLOW, "red checkpoint or owned SCREEN texture missing")
        self.tick(20)
        frame = self.frame()
        # Both the heading and entered dialogue must be visible, not merely a red backdrop.
        for left, top, right, bottom in ((40, 140, 760, 200), (40, 230, 760, 390)):
            bright = sum(self.pixel(frame, x, y)[1] > 160 for y in range(top, bottom) for x in range(left, right))
            assert bright > 30, ("missing restored text", (left, top, right, bottom), bright)
        return frame

    def blue(self):
        return self.wait_color(BLUE, GREEN, "saved script branch or current common flags lost")

    def native_menu(self, saving, evidence):
        self.key(41)  # Escape opens the fallback home menu.
        frame = self.frame()
        assert self.pixel(frame, 20, 580) not in (RED, BLUE), "native menu did not open"
        write_png(evidence.with_name(evidence.stem + "-home.png"), frame)
        self.click(400, 142 if saving else 196)
        write_png(evidence.with_name(evidence.stem + "-slots.png"), self.frame())
        self.click(400, 142)  # Slot 1 in the Save/Load page.

    def destroy(self):
        self.lib.aether_n2system_destroy(self.handle)


def run_phase(library, output, phase):
    writable = output / "writable"
    game = output / "game"
    identity = hashlib.sha256(b"Aether N2 persistent save fixture\0Default").hexdigest()[:24].upper()
    savedata = game / "savedata" / "AetherN2System" / identity
    savedata.mkdir(parents=True, exist_ok=True)
    (savedata / "phase.npf").write_text("phase" + str(phase), encoding="utf-8")
    quick = savedata / "9998/9998.sav"
    native = savedata / "0001/0001.sav"
    host = Host(library, writable, game)
    try:
        startup = host.red()
        write_png(output / f"phase{phase}-startup.png", startup)
        assert (savedata / "roundtrip.npf").read_text(encoding="utf-8") == "保存 metadata|roundtrip"
        if phase == 1:
            host.native_menu(saving=True, evidence=output / "phase1-native-save.png")
            baseline = host.red()
            assert native.is_file(), "native menu did not create slot 1"
            host.key(62)  # SDL F5: quicksave.
            host.red()
            assert quick.is_file() and quick.read_bytes().startswith(b"AN2SSAV1")
            first_hash = digest(quick.read_bytes())
            # Force the next save to replace an existing file, then prove cold loading reads the replacement.
            quick.write_bytes(b"overwrite probe")
            host.key(62)
            baseline = host.red()
            assert quick.read_bytes().startswith(b"AN2SSAV1"), "quicksave did not replace the old slot file"
            assert (savedata / "9998/thum.npf").read_bytes().startswith(b"\x89PNG\r\n\x1a\n")
            (output / "checkpoint.rgba").write_bytes(baseline)
            write_png(output / "phase1-saved-red.png", baseline)
            host.click(400, 300)
            blue = host.blue()
            write_png(output / "phase1-progressed-blue.png", blue)
            assert (savedata / "common/val.npf").is_file(), "common flags were not persisted"
            result = {"phase": 1, "pid": os.getpid(), "quicksave_sha256": digest(quick.read_bytes()),
                      "first_save_sha256": first_hash, "frame_sha256": digest(baseline),
                      "native_slot_sha256": digest(native.read_bytes()), "sidecar_roundtrip": True,
                      "repeated_overwrite": True, "owned_screen_snapshot": True}
        else:
            before = digest(quick.read_bytes())
            host.click(400, 300)
            host.blue()  # Change the live scene before loading, making restoration visible.
            host.key(66)  # SDL F9: quickload.
            restored = host.red()
            baseline = (output / "checkpoint.rgba").read_bytes()
            write_png(output / "phase2-cold-restored-red.png", restored)
            assert restored == baseline, ("cold-restored GPU frame differs", digest(baseline), digest(restored))
            host.click(400, 300)
            resumed = host.blue()
            write_png(output / "phase2-resumed-blue.png", resumed)
            host.native_menu(saving=False, evidence=output / "phase2-native-load.png")
            native_restored = host.red()
            write_png(output / "phase2-native-restored-red.png", native_restored)
            assert native_restored == baseline, "native slot load restored a different frame"
            host.click(400, 300)
            host.blue()
            assert digest(quick.read_bytes()) == before, "loading modified the checkpoint"
            result = {"phase": 2, "pid": os.getpid(), "quicksave_sha256": before,
                      "restored_frame_sha256": digest(restored), "exact_frame_match": True,
                      "common_flags_preserved": True, "saved_branch_resumed": True, "native_menu_load": True}
        (output / f"phase{phase}.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    finally:
        host.destroy()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("library", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path)
    parser.add_argument("--phase", type=int, choices=(1, 2), help=argparse.SUPPRESS)
    args = parser.parse_args()
    library = args.library.resolve()
    output = (args.output or pathlib.Path(tempfile.mkdtemp(prefix="aether-save-load-"))).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if args.phase:
        run_phase(library, output, args.phase)
        return
    if list((output / "game/savedata/AetherN2System").glob("*/common/val.npf")):
        raise RuntimeError("Use a fresh output directory so phase 1 begins with no common flags.")
    shutil.copytree(FIXTURE, output / "game", dirs_exist_ok=True)
    for phase in (1, 2):
        subprocess.run([sys.executable, str(pathlib.Path(__file__).resolve()), str(library),
                        "--output", str(output), "--phase", str(phase)], check=True)
    results = [json.loads((output / f"phase{phase}.json").read_text()) for phase in (1, 2)]
    assert results[0]["pid"] != results[1]["pid"], "cold restart phases reused a process"
    assert results[0]["quicksave_sha256"] == results[1]["quicksave_sha256"]
    print(json.dumps({"passed": True, "output": str(output), "phases": results}, indent=2))


if __name__ == "__main__":
    main()
