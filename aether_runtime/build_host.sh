#!/bin/sh
set -eu
runtime_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) default_rid=osx-arm64 ;;
  Darwin-x86_64) default_rid=osx-x64 ;;
  Linux-aarch64) default_rid=linux-arm64 ;;
  Linux-x86_64) default_rid=linux-x64 ;;
  *) echo 'Pass a supported desktop .NET runtime identifier as the first argument.' >&2; exit 1 ;;
esac
rid=${1:-$default_rid}
output=${2:-$runtime_dir/build/$rid}
dotnet publish "$runtime_dir/AetherN2System.Host.csproj" -c Release -r "$rid" -p:NativeRid="$rid" -o "$output"
