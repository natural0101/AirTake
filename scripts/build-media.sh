#!/usr/bin/env bash
set -euo pipefail
# A standalone LGPL FFmpeg build. Complete unmodified source is shipped in the release.
VERSION=8.0.3
ROOT="$(pwd)"
mkdir -p artifacts/media/licenses artifacts/fixtures .tools/media
cd .tools/media
curl --fail --location --retry 3 "https://ffmpeg.org/releases/ffmpeg-${VERSION}.tar.xz" -o source.tar.xz
curl --fail --location --retry 3 "https://ffmpeg.org/releases/ffmpeg-${VERSION}.tar.xz.asc" -o source.tar.xz.asc
curl --fail --location --retry 3 https://ffmpeg.org/ffmpeg-devel.asc -o release-key.asc
export GNUPGHOME="$PWD/gnupg"; mkdir -p "$GNUPGHOME"; chmod 700 "$GNUPGHOME"
gpg --import release-key.asc
gpg --with-colons --fingerprint | grep -q 'FCF986EA15E6E293A5644F10B4322F04D67658D8'
gpg --verify source.tar.xz.asc source.tar.xz
tar -xf source.tar.xz
cd "ffmpeg-${VERSION}"
./configure --arch=x86_64 --target-os=mingw32 --cross-prefix=x86_64-w64-mingw32- \
  --enable-cross-compile --disable-autodetect --disable-doc --disable-debug --disable-network \
  --disable-everything --enable-ffmpeg --enable-ffprobe --enable-small \
  --enable-protocol=file,pipe --enable-demuxer=mov,wav --enable-muxer=mp4 \
  --enable-parser=hevc,h264,aac --enable-decoder=hevc,aac,pcm_f32le,pcm_s16le,pcm_s24le,pcm_s32le \
  --enable-encoder=aac --enable-filter=aresample,anull,aformat,apad \
  --enable-bsf=hevc_mp4toannexb,aac_adtstoasc,extract_extradata --enable-swresample \
  --extra-ldflags=-static
make -j2 ffmpeg.exe ffprobe.exe
cp ffmpeg.exe ffprobe.exe "$ROOT/artifacts/media/"
cp COPYING.LGPLv2.1 LICENSE.md "$ROOT/artifacts/media/licenses/"
cp ../source.tar.xz "$ROOT/artifacts/ffmpeg-${VERSION}-source.tar.xz"
cp ../source.tar.xz.asc "$ROOT/artifacts/ffmpeg-${VERSION}-source.tar.xz.asc"
cd "$ROOT"
# Synthetic file exercises 3840x2160/120 transport and muxing, NOT real iPhone capture.
ffmpeg -hide_banner -loglevel error -f lavfi -i 'color=c=0x1b3349:s=3840x2160:r=120:d=1' \
  -frames:v 120 -an -c:v libx265 -preset ultrafast -x265-params 'pools=2:frame-threads=2:keyint=60:log-level=error' \
  -tag:v hvc1 -movflags +frag_keyframe+empty_moov+default_base_moof artifacts/fixtures/source.mp4
python3 - <<'PY'
import math, struct, wave
with wave.open('artifacts/fixtures/microphone.wav', 'wb') as w:
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(48000)
    w.writeframes(b''.join(struct.pack('<h', int(5000*math.sin(2*math.pi*440*i/48000))) for i in range(96000)))
PY
