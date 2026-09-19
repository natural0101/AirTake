# Third-party components

AirTake invokes FFmpeg/ffprobe/ffplay as separate programs. Their licenses remain separate from AirTake source code.

## FFmpeg

Windows binaries: Gyan.dev FFmpeg 8.1.2 essentials build, with libsrt. Vendor archive SHA-256: `db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec`.

Vendor: https://www.gyan.dev/ffmpeg/builds/
Archive: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip
FFmpeg sources: https://github.com/FFmpeg/FFmpeg/tree/n8.1.2
Vendor build information and source pointers: https://github.com/GyanD/codexffmpeg
FFmpeg legal/licensing information: https://ffmpeg.org/legal.html

The vendor's included license/readme files are retained under `tools/`. These builds are GPLv3. They include separately licensed libraries; retain the vendor notices when redistributing. Source and license obligations apply to redistributed third-party binaries.

## QRCoder

QRCoder 1.6.0, MIT license.
Source and license: https://github.com/codebude/QRCoder/tree/v1.6.0

## .NET runtime

Self-contained .NET 8 Windows runtime. Microsoft / .NET Foundation.
Source and license: https://github.com/dotnet/runtime
