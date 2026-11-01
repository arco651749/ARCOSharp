# MODULE/IO 指令開發進度清單 (IO Commands Status)

本清單追蹤 `src/MODULES/IO/` 目錄下所有指令組件升級至 **CSVI 3.0 四位一體規格**（包含：原生 C# 執行邏輯 `Logic.cs`、獨立參數導引視窗 `Wizard.axaml`、即時前面板 `Panel.axaml`、修訂履歷 `CSVI.Manifest.json`）之完成狀態。

---

## 📊 開發進度總覽

- **總指令數**：14 個指令 + 1 個模組驅動
- **CSVI 3.0 四位一體已完成**：
  - 指令：**1 / 14** (7.1%)
  - 驅動：**1 / 1** (100%)
- **C# Native 執行邏輯（直譯器支援）**：**14 / 14** (100% 綠燈通過)

---

## 📋 指令進度詳細對照表

| 項次 | 指令檔案名稱 | 指令語法簽章 (Signature) | C# 執行邏輯 | CSVI 3.0 Wizard 導引視窗 | CSVI 3.0 Panel 前面板 | CSVI 3.0 封裝狀態 | 說明 |
| :---: | :--- | :--- | :---: | :---: | :---: | :---: | :--- |
| **1** | **`SetPort.csvi`** | `IO@SetPort $port(,$second,ForceUpdate)` | ✅ 100% | ✅ 獨立完成 | ✅ 獨立完成 | 🟢 **已完成 (3.0)** | 旗艦範本，已於 CSVI Studio 截圖驗證 |
| 2 | `ClrPort.csvi` | `IO@ClrPort $port(,$second,ForceUpdate)` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 3 | `WaitPortOn.csvi` | `IO@WaitPortOn $port(,$timeout)` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 4 | `WaitPortOff.csvi` | `IO@WaitPortOff $port(,$timeout)` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 5 | `Flash(OnOff).csvi` | `IO@Flash(OnOff) $port,$onSecond,$offSecond(,$count)` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 6 | `Flash(OffOn).csvi` | `IO@Flash(OffOn) $port,$offSecond,$onSecond(,$count)` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 7 | `CheckLogicOn.csvi` | `IO@CheckLogicOn $port,$jumpStep` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 8 | `CheckLogicOff.csvi` | `IO@CheckLogicOff $port,$jumpStep` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 9 | `SetPortName.csvi` | `IO@SetPortName $port,$alias` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 10 | `AddVirtualIO.csvi` | `IO@AddVirtualIO $name,$initVal` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 11 | `AddIODriver.csvi` | `IO@AddIODriver $driverName,$param` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 12 | `InitIOPort.csvi` | `IO@InitIOPort $param` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 13 | `ForceUpdate.csvi` | `IO@ForceUpdate` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |
| 14 | `Shift.csvi` | `IO@Shift $startPort,$count,$direction` | ✅ 100% | 🟡 待升級獨立 | 🟡 待升級獨立 | ⚪ 待封裝 | 目前共用舊版通用對話框 |

---

## 🔌 模組驅動 (DRIVER) 開發狀態

| 驅動檔案路徑 | 支援裝置類型 | 硬體點位映射 | CSVI 3.0 Wizard | CSVI 3.0 Panel | CSVI 3.0 封裝狀態 | 說明 |
| :--- | :--- | :---: | :---: | :---: | :---: | :--- |
| **`DRIVER/MockIO.csvi`** | COM Port / TCP Socket | ✅ 8-bit Byte 對齊 (P0.0~P15.7) | ✅ 獨立完成 | ✅ 獨立完成 | 🟢 **已完成 (3.0)** | 支援 `Device=com1` 與 `Device=IP:Port` 點位自定義與即時配電盤開關 |

---

## 📌 符號說明

- 🟢 **已完成 (3.0)**：單一檔案已內嵌專屬 `Logic.cs`、`Wizard.axaml`、`Panel.axaml` 與 `Manifest`，支援於 `CSVI Studio` 獨立開啟、修改、沙盒秒測與現場熱覆蓋。
- 🟡 **待升級獨立**：C# 執行邏輯在 ARCOS 主機與直譯器上完全正常（100% 可執行），但目前其 UI 是依賴舊版集中式彈窗，尚未拆分出獨立的 3.0 檔案結構。
- ⚪ **待封裝**：等待透過 CSVI Studio / MCP 工具編譯並封裝為新版純 PE 結構。
