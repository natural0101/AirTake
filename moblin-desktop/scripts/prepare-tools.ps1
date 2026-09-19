param([string]$Destination = 'tools')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$uri = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.1-essentials_build.zip'
$expected = 'fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('airtake-tools-' + [guid]::NewGuid())
New-Item -ItemType Directory $temp | Out-Null
try {
    $zip = Join-Path $temp 'ffmpeg.zip'
    Invoke-WebRequest -Uri $uri -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower() -ne $expected) { throw 'FFmpeg SHA-256 mismatch' }
    Expand-Archive $zip -DestinationPath (Join-Path $temp 'unpacked')
    New-Item -ItemType Directory -Force $Destination | Out-Null
    foreach ($name in @('ffmpeg.exe', 'ffprobe.exe', 'ffplay.exe')) {
        $file = @(Get-ChildItem (Join-Path $temp 'unpacked') -Filter $name -Recurse -File)
        if ($file.Count -ne 1) { throw "Expected one $name" }
        Copy-Item $file[0].FullName (Join-Path $Destination $name) -Force
    }
    $protocols = & (Join-Path $Destination 'ffmpeg.exe') -hide_banner -protocols 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $protocols -notmatch '(?m)^\s+srt\s*$') { throw 'FFmpeg SRT check failed' }
} finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
