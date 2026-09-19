# Third-party software

AirTake application source: MIT (see LICENSE).

- .NET / Windows Desktop / ASP.NET Core: Microsoft and contributors, MIT; https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- NAudio 2.2.1: Mark Heath and contributors, MIT; https://github.com/naudio/NAudio
- QRCoder 1.6.0: Raffael Herrmann and contributors, MIT; https://github.com/codebude/QRCoder
- FFmpeg 8.0.3: FFmpeg developers, LGPL 2.1-or-later in this configuration. No GPL/nonfree components are enabled. It runs as a separate executable and is not linked to AirTake.

The release includes FFmpeg's license texts, complete corresponding unmodified source archive and signature. The exact build configuration is in scripts/build-media.sh. Redistribution must preserve the relevant notices and source availability. A copy of the source is hosted alongside each binary release, not merely a link to a moving upstream branch.

Apple system frameworks used by the iOS app remain Apple platform components; they are not redistributed in the source package.
