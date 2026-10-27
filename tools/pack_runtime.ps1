param (
    [string]$OutputDir = "$PSScriptRoot\..\dist\MVCON_Runtime"
)

$ErrorActionPreference = "Stop"
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\dotnet"
$env:Path = "$env:LOCALAPPDATA\dotnet;$env:Path"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [MVCON] Packaging MVCON Runtime Deployment Bundle" -ForegroundColor Cyan
Write-Host " Destination: $OutputDir" -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Cyan

# 0. Clean old output directory if present
if (Test-Path "$OutputDir") {
    Write-Host "Cleaning stale deployment directory..." -ForegroundColor Gray
    Remove-Item -Path "$OutputDir" -Recurse -Force
}

# 1. Create standard topology directories
$modules = @("IO", "CAPTURE", "MOTION", "DAQ", "SYSTEM", "THREAD", "VARIABLE")
foreach ($m in $modules) {
    New-Item -ItemType Directory -Path "$OutputDir\MODULES\$m\DRIVER" -Force | Out-Null
    New-Item -ItemType Directory -Path "$OutputDir\MODULES\$m\USERPARAMETEREDITOR" -Force | Out-Null
}
New-Item -ItemType Directory -Path "$OutputDir\EXTRACMD\USERPARAMETEREDITOR" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\Pictures\CAM0" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\Pictures\CAM1" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\Pictures\CamTop" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\Waveforms" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\Python" -Force | Out-Null

# 2. Publish MVCON.App Host
Write-Host "`n[1/5] Publishing MVCON.App Host..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot\..\src\MVCON.App\MVCON.App.csproj" -c Release -o "$OutputDir" --nologo -v q

$rootDlls = Get-ChildItem -Path $OutputDir -Filter "*.dll" | Select-Object -ExpandProperty Name

# 3. Publish MODULE Command Execution DLLs and Deduplicate Shared DLLs
Write-Host "`n[2/5] Publishing Module Command Execution DLLs..." -ForegroundColor Yellow
$moduleProjects = @(
    @{ Proj = "$PSScriptRoot\..\src\MODULES\IO\MVCON.Module.IO.csproj"; Out = "$OutputDir\MODULES\IO"; Name = "IO.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\MOTION\MVCON.Module.Motion.csproj"; Out = "$OutputDir\MODULES\MOTION"; Name = "Motion.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\CAPTURE\MVCON.Module.Capture.csproj"; Out = "$OutputDir\MODULES\CAPTURE"; Name = "Capture.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\DAQ\MVCON.Module.Daq.csproj"; Out = "$OutputDir\MODULES\DAQ"; Name = "DAQ.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\SYSTEM\MVCON.Module.System.csproj"; Out = "$OutputDir\MODULES\SYSTEM"; Name = "System.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\THREAD\MVCON.Module.Thread.csproj"; Out = "$OutputDir\MODULES\THREAD"; Name = "Thread.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\VARIABLE\MVCON.Module.Variable.csproj"; Out = "$OutputDir\MODULES\VARIABLE"; Name = "Variable.Module" }
)

foreach ($m in $moduleProjects) {
    $outPath = "$($m.Out)"
    $modName = "$($m.Name)"
    $projPath = "$($m.Proj)"
    Write-Host "Publishing Module Handler: $modName -> $outPath" -ForegroundColor Magenta

    dotnet publish $projPath -c Release -o $outPath --nologo -v q

    # 移除與根目錄重複的 DLL (保留模組本體的 DLL，如 IO.dll, Motion.dll 等)
    Get-ChildItem -Path $outPath -Filter "*.dll" | Where-Object { $_.Name -in $rootDlls -and $_.Name -notlike "$modName*" } | Remove-Item -Force
    # 移除重複的 PDB 與 runtimes
    Get-ChildItem -Path $outPath -Filter "*.pdb" | Where-Object { $_.Name -notlike "$modName*" } | Remove-Item -Force
    $nativeDir = Join-Path $outPath "runtimes"
    if (Test-Path $nativeDir) {
        Remove-Item -Path $nativeDir -Recurse -Force
    }
}

# 4. Publish MODULE Drivers (Directory-Per-Driver Isolation)
Write-Host "`n[3/5] Publishing Module Drivers (Directory-Per-Driver Isolation)..." -ForegroundColor Yellow
$driverProjects = @(
    @{ Proj = "$PSScriptRoot\..\src\MODULES\IO\DRIVER\MockIO\MVCON.Driver.MockIO.csproj"; Out = "$OutputDir\MODULES\IO\DRIVER\MockIO"; Name = "MockIO.Driver" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\MOTION\DRIVER\MockMotion\MVCON.Driver.MockMotion.csproj"; Out = "$OutputDir\MODULES\MOTION\DRIVER\MockMotion"; Name = "MockMotion.Driver" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\CAPTURE\DRIVER\MockCamera\MVCON.Driver.MockCamera.csproj"; Out = "$OutputDir\MODULES\CAPTURE\DRIVER\MockCamera"; Name = "MockCamera.Driver" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\DAQ\DRIVER\MockDAQ\MVCON.Driver.MockDaq.csproj"; Out = "$OutputDir\MODULES\DAQ\DRIVER\MockDAQ"; Name = "MockDAQ.Driver" }
)

foreach ($d in $driverProjects) {
    $outPath = "$($d.Out)"
    $driverName = "$($d.Name)"
    $projPath = "$($d.Proj)"
    Write-Host "Processing driver: $driverName -> $outPath" -ForegroundColor Magenta

    dotnet publish $projPath -c Release -o $outPath --nologo -v q
    
    # 移除與根目錄重複的通用 DLL (保留驅動本身的 DLL)
    Get-ChildItem -Path $outPath -Filter "*.dll" | Where-Object { $_.Name -in $rootDlls -and $_.Name -notlike "$driverName*" } | Remove-Item -Force
    Get-ChildItem -Path $outPath -Filter "*.pdb" | Where-Object { $_.Name -notlike "$driverName*" } | Remove-Item -Force
    
    # 移除重複的 native runtimes 目錄 (共用根目錄下的 runtimes/)
    $nativeDir = Join-Path $outPath "runtimes"
    if (Test-Path $nativeDir) {
        Remove-Item -Path $nativeDir -Recurse -Force
    }

    # 若為 MockIO 驅動，額外產生標準 MockIO.csvi 驅動檔
    if ($driverName -eq "MockIO.Driver") {
        Copy-Item "$outPath\MockIO.Driver.dll" "$outPath\MockIO.csvi" -Force
        Copy-Item "$outPath\MockIO.Driver.dll" "$OutputDir\MODULES\IO\DRIVER\MockIO.csvi" -Force
        Copy-Item "$outPath\MockIO.Driver.dll" "$PSScriptRoot\..\src\MODULES\IO\DRIVER\MockIO\MockIO.csvi" -Force
        Copy-Item "$outPath\MockIO.Driver.dll" "$PSScriptRoot\..\src\MODULES\IO\DRIVER\MockIO.csvi" -Force
    }

    # 確保 runtimeconfig.json 包含 additionalProbingPaths 指向根目錄 (4層深度)
    $targetRc = "$outPath\$driverName.runtimeconfig.json"
    if (-not [string]::IsNullOrEmpty($targetRc) -and (Test-Path -LiteralPath $targetRc)) {
        $rcJson = Get-Content -LiteralPath $targetRc -Raw | ConvertFrom-Json
        if (-not $rcJson.runtimeOptions.additionalProbingPaths) {
            $rcJson.runtimeOptions | Add-Member -MemberType NoteProperty -Name "additionalProbingPaths" -Value @("../../../..", "../../../../runtimes")
            $rcJson | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $targetRc -Encoding UTF8
        }
    }
}

# 5. Publish CSVI Virtual Instruments (四位一體：各指令獨立 CSVI 直接放入 MODULES\IO)
Write-Host "`n[4/5] Publishing CSVI Virtual Instruments (Pure 4-in-1 Architecture)..." -ForegroundColor Yellow
$tempBuild = "$OutputDir\MODULES\IO\temp_csvi_build"
$guidanceProj = "$PSScriptRoot\..\src\MODULES\IO\USERPARAMETEREDITOR\MVCON.Module.IO.Guidance.csproj"
dotnet build "$guidanceProj" -c Release -o "$tempBuild" --nologo -v q

$ioCommands = @(
    "SetPort",
    "ClrPort",
    "Flash(OnOff)",
    "Flash(OffOn)",
    "WaitPortOn",
    "WaitPortOff",
    "CheckLogicOn",
    "CheckLogicOff",
    "SetPortName",
    "AddVirtualIO",
    "AddIODriver",
    "InitIOPort",
    "ForceUpdate",
    "Shift"
)

# 生成多合一 IO.csvi
Copy-Item "$tempBuild\IO.Guidance.dll" "$OutputDir\MODULES\IO\IO.csvi" -Force
Copy-Item "$tempBuild\IO.Guidance.dll" "$PSScriptRoot\..\src\MODULES\IO\IO.csvi" -Force

foreach ($cmd in $ioCommands) {
    Copy-Item "$tempBuild\IO.Guidance.dll" "$OutputDir\MODULES\IO\$cmd.csvi" -Force
    Copy-Item "$tempBuild\IO.Guidance.dll" "$OutputDir\MODULES\IO\io@$cmd.csvi" -Force
    Copy-Item "$tempBuild\IO.Guidance.dll" "$PSScriptRoot\..\src\MODULES\IO\$cmd.csvi" -Force
}

# 徹底刪除任何 USERPARAMETEREDITOR 遺留目錄與暫存建置資料夾
Remove-Item -Path "$tempBuild" -Recurse -Force -ErrorAction SilentlyContinue
$oldEditor = "$OutputDir\MODULES\IO\USERPARAMETEREDITOR"
if (Test-Path $oldEditor) {
    Remove-Item -Path $oldEditor -Recurse -Force -ErrorAction SilentlyContinue
}
$oldEditorSrc = "$PSScriptRoot\..\src\MODULES\IO\USERPARAMETEREDITOR\*.csvi"
Remove-Item -Path $oldEditorSrc -Force -ErrorAction SilentlyContinue

# 6. Summary & Verification
Write-Host "`n[5/5] Verifying Clean Deployment Topology:" -ForegroundColor Green
foreach ($m in $moduleProjects) {
    $files = (Get-ChildItem -Path $m.Out -File | Select-Object -ExpandProperty Name) -join ", "
    Write-Host "  -> $($m.Out.Replace($OutputDir, 'MVCON_Runtime')) : [ $files ]" -ForegroundColor Cyan
}
foreach ($d in $driverProjects) {
    $files = (Get-ChildItem -Path $d.Out -File | Select-Object -ExpandProperty Name) -join ", "
    Write-Host "  -> $($d.Out.Replace($OutputDir, 'MVCON_Runtime')) : [ $files ]" -ForegroundColor Cyan
}
$ioCsvis = (Get-ChildItem -Path "$OutputDir\MODULES\IO" -Filter "*.csvi" | Select-Object -ExpandProperty Name) -join ", "
Write-Host "  -> MODULES\IO CSVI Instruments : [ $ioCsvis ]" -ForegroundColor Cyan

Write-Host "`n[SUCCESS] MVCON Runtime Bundle successfully packaged at: $OutputDir" -ForegroundColor Green
