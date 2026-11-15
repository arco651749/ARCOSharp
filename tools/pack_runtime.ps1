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
$modules = @("IO", "CAPTURE", "MOTION", "DAQ", "SYSTEM", "THREAD", "VARIABLE", "PYTHON")
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

New-Item -ItemType Directory -Path "$OutputDir\ExternalModules" -Force | Out-Null

# 2. Publish ARCOS.App Host
Write-Host "`n[1/5] Publishing ARCOS.App Host..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot\..\src\ARCOS.App\ARCOS.App.csproj" -c Release -o "$OutputDir" --nologo -v q

$rootDlls = Get-ChildItem -Path $OutputDir -Filter "*.dll" | Select-Object -ExpandProperty Name

# 3. Pure CSVI Architecture: System, Thread, Variable are deployed directly as <Module>.csvi + <Action>.csvi in step [4/5]
Write-Host "`n[2/5] Skipping legacy *.Module.dll projects (100% Pure CSVI Modules)..." -ForegroundColor Yellow

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
    if ($driverName -eq "MockMotion.Driver") {
        $srcMockMotionCsvi = "$PSScriptRoot\..\src\MODULES\MOTION\DRIVER\MockMotion.csvi"
        if (Test-Path -LiteralPath $srcMockMotionCsvi) {
            Copy-Item -LiteralPath $srcMockMotionCsvi -Destination "$outPath\MockMotion.csvi" -Force
            Copy-Item -LiteralPath $srcMockMotionCsvi -Destination "$OutputDir\MODULES\MOTION\DRIVER\MockMotion.csvi" -Force
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

# 5. Publish CSVI Virtual Instruments (純四位一體：各指令獨立 CSVI 直接放入 MODULES\IO)
Write-Host "`n[4/5] Publishing CSVI Virtual Instruments (Pure 4-in-1 Architecture)..." -ForegroundColor Yellow

# 部署 IO 模組 CSVI 虛擬儀表 (純四位一體)
$ioCsvis = Get-ChildItem -Path "$PSScriptRoot\..\src\MODULES\IO" -Filter "*.csvi" -Recurse -File
foreach ($csvi in $ioCsvis) {
    $rel = $csvi.FullName.Substring((Resolve-Path "$PSScriptRoot\..\src\MODULES\IO").Path.Length + 1)
    $dest = Join-Path "$OutputDir\MODULES\IO" $rel
    New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
    Copy-Item -LiteralPath $csvi.FullName -Destination $dest -Force
}

# 部署 SYSTEM 模組 CSVI 虛擬儀表 (四位一體)
$systemCsvis = Get-ChildItem -Path "$PSScriptRoot\..\src\MODULES\SYSTEM" -Filter "*.csvi" -File
foreach ($csvi in $systemCsvis) {
    Copy-Item -LiteralPath $csvi.FullName -Destination "$OutputDir\MODULES\SYSTEM\$($csvi.Name)" -Force
}

# 部署 UserPanel.csvi (獨立 CSVI 3.0 前面板)
Write-Host "Building and packaging UserPanel.csvi..." -ForegroundColor Magenta
$userPanelProj = "$PSScriptRoot\..\src\PANELS\UserPanel\ARCOS.UserPanel.csproj"
if (Test-Path -LiteralPath $userPanelProj) {
    $tempPanelBuild = "$OutputDir\temp_userpanel_build"
    dotnet build "$userPanelProj" -c Release -o "$tempPanelBuild" --nologo -v q
    $userPanelDll = "$tempPanelBuild\UserPanel.dll"
    if (Test-Path -LiteralPath $userPanelDll) {
        Copy-Item -LiteralPath $userPanelDll -Destination "$OutputDir\UserPanel.csvi" -Force
        Copy-Item -LiteralPath $userPanelDll -Destination "$OutputDir\MODULES\SYSTEM\UserPanel.csvi" -Force
        Copy-Item -LiteralPath $userPanelDll -Destination "$PSScriptRoot\..\src\MODULES\SYSTEM\UserPanel.csvi" -Force
        Copy-Item -LiteralPath $userPanelDll -Destination "$PSScriptRoot\..\UserPanel.csvi" -Force
        Write-Host "  -> Successfully deployed UserPanel.csvi to root and MODULES\SYSTEM" -ForegroundColor Cyan
    }
    Remove-Item -Path "$tempPanelBuild" -Recurse -Force -ErrorAction SilentlyContinue
}

# 部署自訂多面板與測試面板 PANELS/*.csvi (不放入 MODULES\SYSTEM 以避免汙染 System@getCMD)
$panelsSrcDir = "$PSScriptRoot\..\PANELS"
if (Test-Path -LiteralPath $panelsSrcDir) {
    New-Item -ItemType Directory -Path "$OutputDir\PANELS" -Force | Out-Null
    $customPanelCsvis = Get-ChildItem -LiteralPath $panelsSrcDir -Filter "*.csvi" -File
    foreach ($pCsvi in $customPanelCsvis) {
        Copy-Item -LiteralPath $pCsvi.FullName -Destination "$OutputDir\PANELS\$($pCsvi.Name)" -Force
        Copy-Item -LiteralPath $pCsvi.FullName -Destination "$OutputDir\$($pCsvi.Name)" -Force
        Copy-Item -LiteralPath $pCsvi.FullName -Destination "$PSScriptRoot\..\$($pCsvi.Name)" -Force
        Write-Host "  -> Deployed UserPanel package: $($pCsvi.Name)" -ForegroundColor Cyan
    }
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

# 部署 PYTHON 模組 CSVI 虛擬儀表 (四位一體)
$pythonCsvis = Get-ChildItem -Path "$PSScriptRoot\..\src\MODULES\PYTHON" -Filter "*.csvi" -File
foreach ($csvi in $pythonCsvis) {
    Copy-Item -LiteralPath $csvi.FullName -Destination "$OutputDir\MODULES\PYTHON\$($csvi.Name)" -Force
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

# 部署 CALL、POINT&PATH、USER、AIAGENT、MULTISTATION、REDIS 等純 CSVI 模組
foreach ($pureMod in @("CALL", "POINT&PATH", "USER", "AIAGENT", "MULTISTATION", "REDIS")) {
    $srcPureDir = "$PSScriptRoot\..\src\MODULES\$pureMod"
    if (Test-Path -LiteralPath $srcPureDir) {
        New-Item -ItemType Directory -Path "$OutputDir\MODULES\$pureMod" -Force | Out-Null
        $pureCsvis = Get-ChildItem -LiteralPath $srcPureDir -Filter "*.csvi" -File
        foreach ($csvi in $pureCsvis) {
            Copy-Item -LiteralPath $csvi.FullName -Destination "$OutputDir\MODULES\$pureMod" -Force
        }
    }
}

# 部署 ExternalModules (外部獨立 CSVI 模組，例如 mycsvi.csvi)
Write-Host "`nDeploying ExternalModules CSVI (mycsvi.csvi)..." -ForegroundColor Magenta
$srcExtCsvi = "$PSScriptRoot\..\ExternalModules\mycsvi.csvi"
if (Test-Path -LiteralPath $srcExtCsvi) {
    Copy-Item -LiteralPath $srcExtCsvi -Destination "$OutputDir\ExternalModules\mycsvi.csvi" -Force
    Write-Host "  -> Successfully deployed mycsvi.csvi to ExternalModules/" -ForegroundColor Cyan
}
$srcExtReadme = "$PSScriptRoot\..\ExternalModules\README.md"
if (Test-Path -LiteralPath $srcExtReadme) {
    Copy-Item -LiteralPath $srcExtReadme -Destination "$OutputDir\ExternalModules\README.md" -Force
}

# 徹底刪除任何 USERPARAMETEREDITOR 遺留目錄與暫存建置資料夾
$oldEditor = "$OutputDir\MODULES\IO\USERPARAMETEREDITOR"
if (Test-Path $oldEditor) {
    Remove-Item -Path $oldEditor -Recurse -Force -ErrorAction SilentlyContinue
}
$oldEditorSrc = "$PSScriptRoot\..\src\MODULES\IO\USERPARAMETEREDITOR\*.csvi"
Remove-Item -Path $oldEditorSrc -Force -ErrorAction SilentlyContinue

# 6. Summary & Verification
Write-Host "`n[5/5] Verifying Clean Deployment Topology:" -ForegroundColor Green
foreach ($mName in @("SYSTEM", "THREAD", "VARIABLE")) {
    $mDir = "$OutputDir\MODULES\$mName"
    if (Test-Path $mDir) {
        $files = (Get-ChildItem -Path $mDir -File | Select-Object -ExpandProperty Name) -join ", "
        Write-Host "  -> ARCOS_Runtime\MODULES\$mName : [ $files ]" -ForegroundColor Cyan
    }
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
