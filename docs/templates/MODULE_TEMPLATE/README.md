# ARCOS 模組範本專案 (Module Template Package)

本目錄為 ARCOS 官方標準模組範本，實踐「三位一體雙模架構 (The Unified Dual-Mode Trinity)」。

## 快速建立新模組步驟：
1. 將整個 `MODULE_TEMPLATE` 資料夾複製至 `src/MODULES/<YourModuleName>`。
2. 將檔名與專案名中的 `Template` 改為您的模組名稱（例如 `LaserMarking`, `ScrewFastener`, `BarcodeScanner`）。
3. 實作您的自訂指令（`TemplateModuleHandler.cs`）、硬體通訊驅動（`DRIVER/`）與參數導引視窗（`USERPARAMETEREDITOR/`）。
4. 執行 `dotnet build`，產出的 `.Module.dll`、`.Driver.dll/.exe` 與 `.Guidance.dll/.exe` 即可直接置入 ARCOS Runtime 運行！

詳細設計原理與調用規範請參閱：[docs/MODULE_DEVELOPMENT_TEMPLATE_AND_SPEC.md](file:///d:/AntiGravity/ARCOS2CSharp/docs/MODULE_DEVELOPMENT_TEMPLATE_AND_SPEC.md)。
