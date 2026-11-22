param(
    [switch]$IncludeUi,
    [switch]$SkipPack
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DotnetExe = "D:\Users\arco0\.dotnet\dotnet.exe"
if (-not (Test-Path $DotnetExe)) {
    $DotnetExe = "dotnet"
}

$SlnPath = Join-Path $RepoRoot "ARCOS.sln"
$PackScript = Join-Path $PSScriptRoot "pack_runtime.ps1"
$UiScript = Join-Path $RepoRoot "verification\2026-11-22\ui_e2e\run_golden_human_e2e.py"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [ARCOSharp] Automated Verification Gate" -ForegroundColor Cyan
Write-Host " Mode: $(if ($IncludeUi) { 'Layer 1 + Layer 2 + Package + Layer 3 (Virtual UI E2E)' } else { 'Layer 1 + Layer 2 (Unit + 244 CSVI Contract Audit)' })" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Step 1: Layer 1 (Headless VM & 14-Module Tests) + Layer 2 (244 CSVI & 246 Schema Contract Audit)
Write-Host "`n[Gate 1/3] Running Layer 1 + Layer 2 (dotnet test ARCOS.sln)..." -ForegroundColor Yellow
& $DotnetExe test $SlnPath --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] Layer 1 / Layer 2 tests failed (exit code $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "[PASS] Layer 1 + Layer 2 passed." -ForegroundColor Green

if (-not $IncludeUi) {
    Write-Host "`n[DONE] Layer 1 + Layer 2 Gate PASSED. (Pass -IncludeUi to also run Layer 3 virtual-display UI E2E.)" -ForegroundColor Green
    exit 0
}

# Step 2: Repackage dist/ARCOS_Runtime (unless -SkipPack)
if (-not $SkipPack) {
    Write-Host "`n[Gate 2/3] Packaging dist/ARCOS_Runtime..." -ForegroundColor Yellow
    Get-Process ARCOS.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # Stop only redis-server launched from dist/ARCOS_Runtime (avoid touching system services)
    Get-CimInstance Win32_Process -Filter "Name='redis-server.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -like "*ARCOS_Runtime*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300

    & $PackScript
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[FAIL] pack_runtime.ps1 failed (exit code $LASTEXITCODE)." -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "[PASS] Runtime bundle packaged." -ForegroundColor Green
} else {
    Write-Host "`n[Gate 2/3] Skipping pack_runtime.ps1 (-SkipPack specified)." -ForegroundColor DarkGray
}

# Step 3: Layer 3 Golden Factory Human UI E2E on Desktop Cockpit Virtual Display (DISPLAY9)
Write-Host "`n[Gate 3/3] Running Layer 3 Golden Factory Human UI E2E on virtual display (DISPLAY9)..." -ForegroundColor Yellow
if (-not (Test-Path $UiScript)) {
    Write-Host "[FAIL] Layer 3 script not found: $UiScript" -ForegroundColor Red
    exit 1
}

python.exe -X utf8 $UiScript
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] Layer 3 Human UI E2E failed (exit code $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n[ALL GATES PASSED] Layer 1 + Layer 2 + Layer 3 verified cleanly." -ForegroundColor Green
exit 0
