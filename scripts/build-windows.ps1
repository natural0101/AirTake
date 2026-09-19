param([string]$Output = "$PSScriptRoot/../artifacts/windows")
$ErrorActionPreference = 'Stop'
Set-Location "$PSScriptRoot/.."
dotnet test tests/AirTake.Tests/AirTake.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
dotnet publish src/AirTake.Desktop/AirTake.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Write-Host "App: $Output/AirTake.exe. Copy the FFmpeg media artifact into $Output/tools before recording."
