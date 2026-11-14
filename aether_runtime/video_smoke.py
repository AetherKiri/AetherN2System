#!/usr/bin/env python3
"""Generate public test media and verify engine movie playback on a real GPU."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("native_directory", type=Path, help="Published host directory containing Fonts and native libraries")
args = parser.parse_args()
runtime = Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(prefix="aether-video-media-") as temporary:
    temporary = Path(temporary)
    generated = temporary / "generated.mpg"
    silent = temporary / "silent.mpg"
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=1.2", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=1.2", "-c:v", "mpeg1video", "-c:a", "mp2", "-f", "mpeg", "-y", str(generated)], check=True)
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25:duration=0.44", "-c:v", "mpeg1video", "-an", "-f", "mpeg", "-y", str(silent)], check=True)
    environment = os.environ.copy()
    environment.setdefault("DOTNET_ROLL_FORWARD", "Major")
    subprocess.run(["dotnet", "run", "--project", str(runtime / "tests/video_smoke/VideoSmoke.csproj"), "--", str(args.native_directory.resolve()), str(generated), str(silent)], env=environment, check=True)
