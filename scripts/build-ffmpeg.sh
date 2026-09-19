#!/usr/bin/env bash
set -euo pipefail
# Minimal, network-disabled LGPL build. No precompiled third-party executables.
VERSION=8.0.3
ROOT="$(pwd)"
mkdir -p .ffmpeg-build .ffmpeg-win
cd .ffmpeg-build
export GNUPGHOME="$PWD/gnupg"
mkdir -p "$GNUPGHOME"; chmod 700 "$GNUPGHOME"
curl --fail --location --retry 3 "https://ffmpeg.org/releases/ffmpeg-${VERSION}.tar.xz" -o source.tar.xz
curl --fail --location --retry 3 "https://ffmpeg.org/releases/ffmpeg-${VERSION}.tar.xz.asc" -o source.tar.xz.asc
curl --fail --location --retry 3 https://ffmpeg.org/ffmpeg-devel.asc -o ffmpeg-devel.asc
gpg --batch --import ffmpeg-devel.asc
gpg --batch --with-colons --fingerprint | grep -q FCF986EA15E6E293A5644F10B4322F04D67658D8
gpg --batch --verify source.tar.xz.asc source.tar.xz
tar -xf source.tar.xz
cd "ffmpeg-${VERSION}"
./configure --target-os=mingw32 --arch=x86_64 --cross-prefix=x86_64-w64-mingw32- \
 --pkg-config=false --disable-autodetect --disable-everything --disable-x86asm \
 --disable-doc --disable-debug --disable-network --disable-ffplay --disable-ffprobe \
 --enable-ffmpeg --enable-small --extra-ldflags=-static \
 --enable-protocol=file,pipe --enable-demuxer=mov,wav --enable-muxer=mp4,mov \
 --enable-parser=hevc,h264,aac \
 --enable-decoder=pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_f64le,pcm_u8,aac \
 --enable-encoder=aac --enable-swresample \
 --enable-filter=abuffer,abuffersink,anull,aformat,aresample,atrim,asetpts,apad,adelay
make -j"$(nproc)"
cp ffmpeg.exe "$ROOT/.ffmpeg-win/ffmpeg.exe"
cp COPYING.LGPLv2.1 "$ROOT/.ffmpeg-win/COPYING.LGPLv2.1.txt"
cp "$ROOT/.ffmpeg-build/source.tar.xz" "$ROOT/.ffmpeg-win/ffmpeg-${VERSION}-source.tar.xz"
cp "$ROOT/scripts/build-ffmpeg.sh" "$ROOT/.ffmpeg-win/build-ffmpeg.sh"
printf 'FFmpeg %s. Source, exact build script and LGPL license accompany this executable. No GPL/nonfree components. Network protocols are disabled. https://ffmpeg.org\n' "$VERSION" > "$ROOT/.ffmpeg-win/FFmpeg-NOTICE.txt"
sha256sum "$ROOT/.ffmpeg-win/ffmpeg.exe" > "$ROOT/.ffmpeg-win/ffmpeg.sha256"
