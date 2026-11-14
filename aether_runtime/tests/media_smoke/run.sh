#!/bin/sh
set -eu

if [ "$#" -ne 1 ]; then
  echo "usage: $0 /path/to/libaether_n2system_media.dylib" >&2
  exit 2
fi

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output_dir=${TMPDIR:-/tmp}/aether-n2system-media-smoke
mkdir -p "$output_dir"

ffmpeg_bin=${FFMPEG_BIN:-ffmpeg}
layout_file=$output_dir/ffmpeg-layout.txt
layout_source=$output_dir/ffmpeg-layout.c
layout_bin=$output_dir/ffmpeg-layout
include_dir=${FFMPEG_INCLUDE_DIR:-}

if [ -z "$include_dir" ]; then
  for candidate in \
    "$script_dir/../../../../../out/macos/debug/vcpkg_installed/arm64-osx/include" \
    "$script_dir/../../../../../out/macos/release/vcpkg_installed/arm64-osx/include" \
    "$script_dir/../../../../../out/linux/debug/vcpkg_installed/x64-linux/include"; do
    if [ -f "$candidate/libavformat/avformat.h" ]; then
      include_dir=$candidate
      break
    fi
  done
fi

if [ -z "$include_dir" ] || [ ! -f "$include_dir/libavformat/avformat.h" ]; then
  echo "set FFMPEG_INCLUDE_DIR to the FFmpeg 8.1 include directory" >&2
  exit 2
fi

cat > "$layout_source" <<'EOF'
#include <stddef.h>
#include <stdio.h>
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/channel_layout.h>
#define L(type,field) printf("%s.%s=%zu\n",#type,#field,offsetof(type,field))
int main(void) {
#define S(type) printf("%s.size=%zu\n",#type,sizeof(type))
S(AVFormatContext); S(AVIOContext); S(AVCodecContext); S(AVStream); S(AVFrame); S(AVPacket); S(AVChannelLayout);
L(AVFormatContext,pb); L(AVFormatContext,streams); L(AVFormatContext,nb_streams); L(AVFormatContext,iformat);
L(AVIOContext,buffer); L(AVCodecContext,get_format); L(AVCodecContext,ch_layout); L(AVCodecContext,sample_rate); L(AVCodecContext,sample_fmt);
L(AVStream,codecpar); L(AVStream,time_base); L(AVStream,duration); L(AVStream,discard); L(AVStream,index);
L(AVFrame,best_effort_timestamp); L(AVFrame,ch_layout); L(AVFrame,extended_data); L(AVFrame,sample_rate); L(AVFrame,nb_samples); L(AVFrame,data); L(AVFrame,linesize);
L(AVPacket,data); L(AVPacket,stream_index);
}
EOF

${CC:-cc} -I"$include_dir" "$layout_source" -o "$layout_bin"
"$layout_bin" > "$layout_file"

"$ffmpeg_bin" -v error -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=0.6' -ac 2 -c:a pcm_s16le "$output_dir/fixture.wav" -y
"$ffmpeg_bin" -v error -i "$output_dir/fixture.wav" -c:a vorbis -strict experimental "$output_dir/fixture.ogg" -y
"$ffmpeg_bin" -v error -i "$output_dir/fixture.wav" -c:a libmp3lame "$output_dir/fixture.mp3" -y
"$ffmpeg_bin" -v error -f lavfi -i 'testsrc2=size=64x48:rate=12:duration=0.6' -c:v mpeg4 -pix_fmt yuv420p "$output_dir/fixture.avi" -y

dotnet run --project "$script_dir/MediaSmoke.csproj" -- \
  "$1" "$layout_file" \
  "$output_dir/fixture.wav" "$output_dir/fixture.ogg" \
  "$output_dir/fixture.mp3" "$output_dir/fixture.avi"
