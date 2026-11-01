param (
    [string]$OutputDir = "$PSScriptRoot\..\dist\ARCOS_Runtime"
)

$ErrorActionPreference = "Stop"
if (Test-Path "$env:LOCALAPPDATA\dotnet\dotnet.exe") {
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\dotnet"
    $env:Path = "$env:LOCALAPPDATA\dotnet;$env:Path"
} elseif (Test-Path "D:\Users\arco0\.dotnet\dotnet.exe") {
    $env:DOTNET_ROOT = "D:\Users\arco0\.dotnet"
    $env:Path = "D:\Users\arco0\.dotnet;$env:Path"
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [ARCOS] Packaging ARCOS Runtime Deployment Bundle" -ForegroundColor Cyan
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
New-Item -ItemType Directory -Path "$OutputDir\SCRIPT" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\LOGS" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\PICTURE\CAM0" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\PICTURE\CAM1" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\PICTURE\CamTop" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\WAVEFORM" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\POINT&PATH" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\Python" -Force | Out-Null

# 2. Publish ARCOS.App Host
Write-Host "`n[1/5] Publishing ARCOS.App Host..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot\..\src\ARCOS.App\ARCOS.App.csproj" -c Release -o "$OutputDir" --nologo -v q

$rootDlls = Get-ChildItem -Path $OutputDir -Filter "*.dll" | Select-Object -ExpandProperty Name

# 3. Publish MODULE Command Execution DLLs and Deduplicate Shared DLLs
Write-Host "`n[2/5] Publishing Module Command Execution DLLs..." -ForegroundColor Yellow
$moduleProjects = @(
    @{ Proj = "$PSScriptRoot\..\src\MODULES\IO\ARCOS.Module.IO.csproj"; Out = "$OutputDir\MODULES\IO"; Name = "IO.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\MOTION\ARCOS.Module.Motion.csproj"; Out = "$OutputDir\MODULES\MOTION"; Name = "Motion.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\CAPTURE\ARCOS.Module.Capture.csproj"; Out = "$OutputDir\MODULES\CAPTURE"; Name = "Capture.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\DAQ\ARCOS.Module.Daq.csproj"; Out = "$OutputDir\MODULES\DAQ"; Name = "DAQ.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\SYSTEM\ARCOS.Module.System.csproj"; Out = "$OutputDir\MODULES\SYSTEM"; Name = "System.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\THREAD\ARCOS.Module.Thread.csproj"; Out = "$OutputDir\MODULES\THREAD"; Name = "Thread.Module" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\VARIABLE\ARCOS.Module.Variable.csproj"; Out = "$OutputDir\MODULES\VARIABLE"; Name = "Variable.Module" }
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
    @{ Proj = "$PSScriptRoot\..\src\MODULES\IO\DRIVER\MockIO\ARCOS.Driver.MockIO.csproj"; Out = "$OutputDir\MODULES\IO\DRIVER\MockIO"; Name = "MockIO.Driver" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\MOTION\DRIVER\MockMotion\ARCOS.Driver.MockMotion.csproj"; Out = "$OutputDir\MODULES\MOTION\DRIVER\MockMotion"; Name = "MockMotion.Driver" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\CAPTURE\DRIVER\MockCamera\ARCOS.Driver.MockCamera.csproj"; Out = "$OutputDir\MODULES\CAPTURE\DRIVER\MockCamera"; Name = "MockCamera.Driver" },
    @{ Proj = "$PSScriptRoot\..\src\MODULES\DAQ\DRIVER\MockDAQ\ARCOS.Driver.MockDaq.csproj"; Out = "$OutputDir\MODULES\DAQ\DRIVER\MockDAQ"; Name = "MockDAQ.Driver" }
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

    # 若為 MockIO 驅動，優先保留並部署 CSVI 3.0 封裝檔 (絕不拿無 XAML 的舊版 DLL 覆蓋 src 內的 CSVI 3.0)
    if ($driverName -eq "MockIO.Driver") {
        $srcMockCsvi = "$PSScriptRoot\..\src\MODULES\IO\DRIVER\MockIO.csvi"
        if (Test-Path -LiteralPath $srcMockCsvi) {
            Copy-Item -LiteralPath $srcMockCsvi -Destination "$outPath\MockIO.csvi" -Force
            Copy-Item -LiteralPath $srcMockCsvi -Destination "$OutputDir\MODULES\IO\DRIVER\MockIO.csvi" -Force
        } else {
            Copy-Item "$outPath\MockIO.Driver.dll" "$outPath\MockIO.csvi" -Force
            Copy-Item "$outPath\MockIO.Driver.dll" "$OutputDir\MODULES\IO\DRIVER\MockIO.csvi" -Force
        }
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
$guidanceProj = "$PSScriptRoot\..\src\MODULES\IO\USERPARAMETEREDITOR\ARCOS.Module.IO.Guidance.csproj"
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

# 部署多合一 IO.csvi (優先使用 src\MODULES\IO\IO.csvi，絕不逆向覆寫 src 目錄)
$srcIoRootCsvi = "$PSScriptRoot\..\src\MODULES\IO\IO.csvi"
if (Test-Path -LiteralPath $srcIoRootCsvi) {
    Copy-Item -LiteralPath $srcIoRootCsvi -Destination "$OutputDir\MODULES\IO\IO.csvi" -Force
} else {
    Copy-Item "$tempBuild\IO.Guidance.dll" "$OutputDir\MODULES\IO\IO.csvi" -Force
}

foreach ($cmd in $ioCommands) {
    $srcCmdCsvi = "$PSScriptRoot\..\src\MODULES\IO\$cmd.csvi"
    if (Test-Path -LiteralPath $srcCmdCsvi) {
        Copy-Item -LiteralPath $srcCmdCsvi -Destination "$OutputDir\MODULES\IO\$cmd.csvi" -Force
        Copy-Item -LiteralPath $srcCmdCsvi -Destination "$OutputDir\MODULES\IO\io@$cmd.csvi" -Force
    } else {
        Copy-Item "$tempBuild\IO.Guidance.dll" "$OutputDir\MODULES\IO\$cmd.csvi" -Force
        Copy-Item "$tempBuild\IO.Guidance.dll" "$OutputDir\MODULES\IO\io@$cmd.csvi" -Force
    }
}

# 部署 SYSTEM 模組 CSVI 虛擬儀表 (四位一體)
$systemCsvis = Get-ChildItem -Path "$PSScriptRoot\..\src\MODULES\SYSTEM" -Filter "*.csvi" -File
foreach ($csvi in $systemCsvis) {
    Copy-Item -LiteralPath $csvi.FullName -Destination "$OutputDir\MODULES\SYSTEM\$($csvi.Name)" -Force
}

# 部署 THREAD 模組 CSVI 虛擬儀表 (四位一體)
$threadCsvis = Get-ChildItem -Path "$PSScriptRoot\..\src\MODULES\THREAD" -Filter "*.csvi" -File
foreach ($csvi in $threadCsvis) {
    Copy-Item -LiteralPath $csvi.FullName -Destination "$OutputDir\MODULES\THREAD\$($csvi.Name)" -Force
}

# 部署 CAPTURE 模組 CSVI 虛擬儀表與驅動 (四位一體)
$captureCsvis = Get-ChildItem -Path "$PSScriptRoot\..\src\MODULES\CAPTURE" -Filter "*.csvi" -Recurse -File
foreach ($csvi in $captureCsvis) {
    $rel = $csvi.FullName.Substring((Resolve-Path "$PSScriptRoot\..\src\MODULES\CAPTURE").Path.Length + 1)
    $dest = Join-Path "$OutputDir\MODULES\CAPTURE" $rel
    New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
    Copy-Item -LiteralPath $csvi.FullName -Destination $dest -Force
}

# 部署 DAQ 模組 CSVI 虛擬儀表與驅動 (四位一體)
$daqCsvis = Get-ChildItem -Path "$PSScriptRoot\..\src\MODULES\DAQ" -Filter "*.csvi" -Recurse -File
foreach ($csvi in $daqCsvis) {
    $rel = $csvi.FullName.Substring((Resolve-Path "$PSScriptRoot\..\src\MODULES\DAQ").Path.Length + 1)
    $dest = Join-Path "$OutputDir\MODULES\DAQ" $rel
    New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
    Copy-Item -LiteralPath $csvi.FullName -Destination $dest -Force
}

# 部署 VARIABLE 模組 CSVI 虛擬儀表 (四位一體)
$variableCsvis = Get-ChildItem -Path "$PSScriptRoot\..\src\MODULES\VARIABLE" -Filter "*.csvi" -File
foreach ($csvi in $variableCsvis) {
    Copy-Item -LiteralPath $csvi.FullName -Destination "$OutputDir\MODULES\VARIABLE\$($csvi.Name)" -Force
}

# 部署 MOTION 模組 CSVI 虛擬儀表與驅動 (四位一體)
$motionCsvis = Get-ChildItem -Path "$PSScriptRoot\..\src\MODULES\MOTION" -Filter "*.csvi" -Recurse -File
foreach ($csvi in $motionCsvis) {
    $rel = $csvi.FullName.Substring((Resolve-Path "$PSScriptRoot\..\src\MODULES\MOTION").Path.Length + 1)
    $dest = Join-Path "$OutputDir\MODULES\MOTION" $rel
    New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
    Copy-Item -LiteralPath $csvi.FullName -Destination $dest -Force
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
    Write-Host "  -> $($m.Out.Replace($OutputDir, 'ARCOS_Runtime')) : [ $files ]" -ForegroundColor Cyan
}
foreach ($d in $driverProjects) {
    $files = (Get-ChildItem -Path $d.Out -File | Select-Object -ExpandProperty Name) -join ", "
    Write-Host "  -> $($d.Out.Replace($OutputDir, 'ARCOS_Runtime')) : [ $files ]" -ForegroundColor Cyan
}
$ioCsvis = (Get-ChildItem -Path "$OutputDir\MODULES\IO" -Filter "*.csvi" | Select-Object -ExpandProperty Name) -join ", "
Write-Host "  -> MODULES\IO CSVI Instruments : [ $ioCsvis ]" -ForegroundColor Cyan

Write-Host "`n[SUCCESS] ARCOS Runtime Bundle successfully packaged at: $OutputDir" -ForegroundColor Green

# 7. Zip Runtime Bundle for Beta Distribution
$zipFile = "$PSScriptRoot\..\dist\ARCOS_v3.0_Beta.zip"
Write-Host "`n[Packaging] Compressing Runtime into $zipFile..." -ForegroundColor Yellow
if (Test-Path $zipFile) {
    Remove-Item -Path $zipFile -Force
}
Compress-Archive -Path "$OutputDir\*" -DestinationPath $zipFile -Force
Write-Host "[SUCCESS] ARCOS v3.0 Beta Zip Distribution created successfully: $zipFile" -ForegroundColor Green
