# ARCOSharp (ARCOS C# .NET 8 + Avalonia UI + CSVI 3.0)
### 🚀 次世代跨平台工業自動化控制系統 (ARCOS Modernized with Grand Unified CSVI Architecture)

**ARCOSharp** 是將經典 LabVIEW 自動化設備控制系統 **ARCOS** 全面以 **C# (.NET 8) + Avalonia UI 11** 現代化重構的跨平台工控軟體（支援 Windows 與 Linux IPC），並首創 **CSVI 3.0（C# Virtual Instrument）四位一體可攜式虛擬儀表架構**。

---

## 🏗️ 核心架構特色

1. **CSVI 四位一體統一虛擬儀表 (`*.csvi`)**：
   每一個指令（如 `MODULES/IO/SetPort.csvi`）與驅動（如 `MODULES/IO/DRIVER/MockIO.csvi`）皆為單一 `.csvi` 組件，同時具備四個面向：
   - **面向 1：純執行 (`Execute`)**：微秒級原生 C# IL 執行，0ms UI 開銷，供產線全速量產。
   - **面向 2：參數導引視窗 (`ShowGuidanceDialogAsync`)**：雙擊表格參數即刻彈出，動態感知 `BasicEngines` 硬體點位，實踐零記憶負擔（Zero Cognitive Load）。
   - **面向 3：即時運算視覺化 (`NotifyExecution`)**：執行時將狀態、電位與耗時即時推播至面板。
   - **面向 4：子面板與驅動 HMI (`GetFrontPanelControl`)**：回傳 Avalonia 原生控制項，可動態掛載於主程式 `User Tab` 的 `SubPanel`，或作為 `Modules Info` 下的硬體驅動控制台。
2. **目錄驅動探索（Directory-Driven Discovery）**：
   - `MODULES/<Module>/*.csvi`：目錄下有多少個 `.csvi`，主程式就支援多少個指令。
   - `MODULES/<Module>/DRIVER/*.csvi`：目錄下有多少個 `.csvi`，主程式就支援多少個硬體驅動。
   - **徹底淘汰舊時代 `USERPARAMETEREDITOR/` 冗餘目錄**。
3. **CSVI 3.0 可攜式原始碼封裝與獨立 `CSVI Studio` 工具鏈**：
   - 支援在標準 `.NET PE` 格式之 `.csvi` 內透過 `ManifestResource` 選擇性封裝 `.cs` 與 `.axaml` 原始碼（提供 **🟢 開放攜帶 / 🟡 AES-256 密碼鎖定 / 🔴 純執行剝離** 三種模式）。
   - 搭配獨立的 **`CSVI_Studio.exe`**（內建完整 Roslyn C# 編譯器、Runtime XAML 解析器與鎖定版標準 SDK 參考庫），強制對齊主程式 Runtime ABI，支援現場免 Visual Studio 即時修改、預覽、重編與非鎖檔熱插拔（`File.ReadAllBytes`）。

---

## 📚 完整規格書與開發文件索引

| 文件名稱 | 內容說明 |
| :--- | :--- |
| **[`docs/CSVI_STUDIO_AND_PORTABLE_SPEC.md`](docs/CSVI_STUDIO_AND_PORTABLE_SPEC.md)** | **CSVI 3.0 可攜式自包含格式（內嵌 CS+AXAML）與獨立 `CSVI Studio` 工具鏈規格書** |
| **[`docs/DYNAMIC_ARCHITECTURE_AND_GUIDANCE_SPEC.md`](docs/DYNAMIC_ARCHITECTURE_AND_GUIDANCE_SPEC.md)** | **ARCOSharp CSVI 四位一體全局架構、目錄驅動探索與非鎖檔熱插拔規格書** |
| **[`docs/GUIDANCE_DESIGN_PRINCIPLES.md`](docs/GUIDANCE_DESIGN_PRINCIPLES.md)** | **參數導引視窗設計原則（零記憶負荷原則與八大模組資源自動感知矩陣）** |
| **[`docs/MODULE_DEVELOPMENT_TEMPLATE_AND_SPEC.md`](docs/MODULE_DEVELOPMENT_TEMPLATE_AND_SPEC.md)** | **CSVI 模組與硬體驅動標準開發指南與程式碼範本** |
| **[`ARCOS_CSharp_Development_Roadmap.md`](ARCOS_CSharp_Development_Roadmap.md)** | **系統七大階段開發路線圖、API 規格檢核表與模組 CSVI 遷移里程碑** |
| **[`LabVIEW_to_CSharp_Migration_Guide.md`](LabVIEW_to_CSharp_Migration_Guide.md)** | **LabVIEW (ARCOS) 轉 C# (.NET 8 / Avalonia) 語法、狀態機與 CSVI 映射對照手冊** |

---

## 🚀 快速建置與測試

```powershell
# 1. 取得專案
git clone https://github.com/arco651749/ARCOSharp.git
cd ARCOSharp

# 2. 執行全套單元測試 (86 項測試)
dotnet test ARCOS.sln

# 3. 打包完整 Runtime 與所有 CSVI 虛擬儀表至 dist/ARCOS_Runtime
powershell.exe -ExecutionPolicy Bypass -File .\tools\pack_runtime.ps1
```
