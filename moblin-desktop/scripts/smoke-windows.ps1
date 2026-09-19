param([Parameter(Mandatory=$true)][string]$Exe)
$ErrorActionPreference = 'Stop'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('airtake-smoke-' + [guid]::NewGuid())
$env:AIRTAKE_DATA_DIR = $temp
$env:AIRTAKE_NO_BROWSER = '1'
New-Item -ItemType Directory $temp | Out-Null
$p = $null
try {
    $p = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
    $instanceFile = Join-Path $temp 'instance.json'
    for ($i=0; $i -lt 100 -and !(Test-Path $instanceFile); $i++) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path $instanceFile)) { throw 'AirTake did not start' }
    $inst = Get-Content $instanceFile -Raw | ConvertFrom-Json
    $headers = @{ 'X-AirTake-Token' = $inst.token }
    $s = Invoke-RestMethod ($inst.url + 'api/state') -Headers $headers
    if ($s.version -ne '0.1.0' -or $s.config.fps -ne 120 -or $s.config.width -ne 3840) { throw 'Invalid startup state' }
    $qr = Invoke-WebRequest ($inst.url + 'api/qr') -Headers $headers
    if ($qr.Content -notmatch '<svg') { throw 'QR generation failed' }
    $denied = $false
    try { Invoke-WebRequest ($inst.url + 'api/state') | Out-Null } catch { $denied = ([int]$_.Exception.Response.StatusCode -eq 401) }
    if (!$denied) { throw 'Unauthenticated API request was not rejected' }
    $s.config.bitrateMbps = 100
    Invoke-RestMethod ($inst.url + 'api/config') -Method Post -Headers $headers -ContentType 'application/json' -Body ($s.config | ConvertTo-Json) | Out-Null
    $s2 = Invoke-RestMethod ($inst.url + 'api/state') -Headers $headers
    if ($s2.config.bitrateMbps -ne 100) { throw 'Settings did not persist' }
    Invoke-RestMethod ($inst.url + 'api/shutdown') -Method Post -Headers $headers | Out-Null
    if (!$p.WaitForExit(10000)) { throw 'AirTake did not exit' }
    if ($p.ExitCode -ne 0) { throw 'AirTake exit error' }
    Write-Output 'PASS: Windows EXE starts, API authentication, QR, settings and clean shutdown'
} finally {
    if ($p -and !$p.HasExited) { Stop-Process -Id $p.Id -Force }
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:AIRTAKE_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:AIRTAKE_NO_BROWSER -ErrorAction SilentlyContinue
}
