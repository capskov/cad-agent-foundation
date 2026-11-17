param(
    [string]$AutoCadDir = $env:AUTO_CAD_2022_DIR,
    [string]$Configuration = 'Release'
)
# Build + regression gate for ACAD2022 V1.1.
# Everything is resolved relative to this repository, so a clean checkout reproduces it.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $AutoCadDir) { $AutoCadDir = 'C:\Program Files\Autodesk\AutoCAD 2022' }
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
$results = @()

function Step($name, [scriptblock]$body) {
    Write-Host "== $name" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) { throw "$name failed with exit $LASTEXITCODE" }
    $script:results += [pscustomobject]@{ step = $name; ok = $true }
}

Step "plugin-acad22 build ($Configuration, net48)" {
    & $dotnet build --nologo -c $Configuration "/p:AutoCad2022Dir=$AutoCadDir" (Join-Path $root 'plugin-acad22\Bimwright.Dwg.Plugin.Acad22.csproj') | Select-Object -Last 3
}
Step "server build ($Configuration, net8)" {
    & $dotnet build --nologo -c $Configuration (Join-Path $root 'server\Bimwright.Dwg.Server.csproj') | Select-Object -Last 3
}
Step 'hash-regress build (net8)' {
    & $dotnet build --nologo -c $Configuration (Join-Path $root 'tools\hash-regress\hash-regress.csproj') | Select-Object -Last 3
}
Step 'net48/net8 canonical hash regression' {
    & (Join-Path $root "tools\hash-regress\bin\$Configuration\net8.0\hash-regress.exe") (Join-Path $root 'tools\hash-regress\samples') | Select-Object -Last 4
}

# Artefact identity: the DLLs this gate built, plus the client that ships beside them.
$artefacts = @(
    @{ name = 'plugin'; path = Join-Path $root "plugin-acad22\bin\$Configuration\net48\Bimwright.Dwg.Plugin.Acad22.dll" },
    @{ name = 'server'; path = Join-Path $root "server\bin\$Configuration\net8.0\Bimwright.Dwg.Server.dll" },
    @{ name = 'client'; path = Join-Path $root 'client\foundation.py' }
)
Write-Host "== artefact identity" -ForegroundColor Cyan
$ident = foreach ($a in $artefacts) {
    if (Test-Path -LiteralPath $a.path) {
        [pscustomobject]@{ artefact = $a.name; sha256 = (Get-FileHash -LiteralPath $a.path -Algorithm SHA256).Hash; path = $a.path }
    } else {
        [pscustomobject]@{ artefact = $a.name; sha256 = 'MISSING'; path = $a.path }
    }
}
$ident | Format-Table -AutoSize
$ident | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $root "tools\last-gate-artefacts.json") -Encoding utf8

$results | Format-Table -AutoSize
Write-Host 'GATE PASSED' -ForegroundColor Green
