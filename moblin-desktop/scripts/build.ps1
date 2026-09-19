$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    go test ./...
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    $env:GOOS = 'windows'; $env:GOARCH = 'amd64'; $env:CGO_ENABLED = '0'
    New-Item -ItemType Directory -Force dist | Out-Null
    go build -trimpath -ldflags='-s -w -H=windowsgui' -o dist/AirTake.exe ./cmd/airtake
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    Copy-Item README.md,LICENSE,THIRD-PARTY-NOTICES.txt,START-HERE-RU.txt dist
    Compress-Archive -Path dist/* -DestinationPath AirTake-Moblin-Windows-x64.zip -Force
} finally { Pop-Location }
