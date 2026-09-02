#Requires -Version 5.1
# Builds PairingHarness (Release|x64) only - not the main BetterJoy solution.
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot "PairingHarness.csproj"

function Find-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw "Visual Studio Installer not found. Install Visual Studio (or Build Tools) with the '.NET desktop development' workload."
    }
    $msbuildPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    if (-not $msbuildPath) {
        throw "MSBuild not found via vswhere. Make sure the '.NET desktop development' workload is installed."
    }
    return $msbuildPath
}

# The build copies the compiled exe over the existing one - a still-running instance locks that
# file and turns into a wall of MSB3026/MSB3027 retry spam before failing anyway. Check first and
# fail fast with a clear message instead.
$running = Get-Process PairingHarness -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "==> PairingHarness.exe is still running (PID $($running.Id -join ', ')). Close it first." -ForegroundColor Yellow
    exit 1
}

Write-Host "==> Locating MSBuild..."
$msbuild = Find-MSBuild
Write-Host "    $msbuild"

Write-Host "==> Building PairingHarness (Release|x64)..."
& $msbuild $project /p:Configuration=Release /p:Platform=x64 /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$exePath = Join-Path $repoRoot "bin\x64\Release\PairingHarness.exe"
Write-Host "==> Done: $exePath" -ForegroundColor Green
