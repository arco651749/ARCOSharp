# ARCOS MODULE/IO 模組手冊：架構、指令清單、使用流程與設計注意事項

本文件為 `src/MODULES/IO/` 之核心規範文件，說明 IO 模組的**雙動態資料模型**、**全指令函式簽章**、**標準 5 步使用流程**以及 **CSVI 3.0 虛擬儀表設計規範**。

---

## 目錄
1. [核心架構與哲學 (Architecture & Philosophy)](#一核心架構與哲學)
2. [標準使用流程 5 步驟 (Standard Operating Flow)](#二標準使用流程-5-步驟)
3. [完整指令函式規格清單 (Command Specifications)](#三完整指令函式規格清單)
4. [核心設計注意事項與工程規範 (Design Principles & Gotchas)](#四核心設計注意事項與工程規範)
5. [CSVI 3.0 組件封裝狀態對照表 (CSVI 3.0 Status)](#五csvi-30-組件封裝狀態對照表)

---

## 一、 核心架構與哲學

在自動化工控軟體中，底層硬體的實體位址（如 `P0.0`、`P1.0`）僅代表接線端子與暫存器偏移量。高階業務排程與工程師操作時，**必須全面脫離生硬的 `PX.X`，改採具備語意意義的 `PORTNAME`**。

IO 模組的核心由兩個**隨腳本編輯與執行動態演進的狀態機（Dynamic State）**所驅動：

```
+-----------------------------------------------------------------------------------+
|                                IO 模組動態資料庫                                    |
+-----------------------------------------+-----------------------------------------+
|        1. IO PortName 點位名冊          |        2. IO Driver List 驅動名冊        |
+-----------------------------------------+-----------------------------------------+
| - 語意別名 (如 OUT1, Start?，不分方向)  | - 透過 IO@AddDrvier 註冊的裝置代號       |
| - 動態虛擬點位 (如 RESULT)              |   (例如: IO1, IO2, StationIO)            |
| - 未命名實體點位 (P0.0 ~ P7.7 調機測試) | - 供 Initio / ForceUpdate 下拉選單讀取   |
| - 供 SetPort / ClrPort / WaitPort 讀取  |                                         |
+-----------------------------------------+-----------------------------------------+
```

### 雙時機動態同步（Two-Phase Synchronization）
1. **編輯期動態預解析 (Design-Time Pre-Scan)**：
   工程師在 Sequencer 表格中剛撰寫完驅動宣告與命名指令，**尚未點擊 RUN 執行**前，點開後續指令的導引視窗（Parameter Guidance / Wizard）時，系統自動預掃描當前腳本中的 `AddDrvier`、`SETPORTNAME`、`VIRTUALIO`，即時注入 `IoEngine`。因此導引視窗下拉選單立即可見最新定義的名稱，**絕不充斥無意義的 `PX.X`**。
2. **執行期動態註冊 (Runtime Execution)**：
   腳本單步（Step）或全速運行（Run）時，動態調用 `VIRTUALIO` 或 `SETPORTNAME` 即時更新 `IoEngine` 字典，並透過事件廣播給 UI 虛擬前面板與各執行緒。

---

## 二、 標準使用流程 5 步驟

在編寫 ARCOS 產線控制腳本時，請嚴格遵循以下 5 個標準步驟：

```markdown
// 步驟 1：宣告硬體通訊與實體埠對應
IO@AddDrvier IO1, DEVICE=COM1, OUTSTART=P0.0, OUTPORTS=8, INSTART=P1.0, INPORTS=8

// 步驟 2：賦予實體點位語意化的 PORTNAME (如 OUT1，感測/詢問類建議尾綴帶 ? 如 Start?)
IO@SETPORTNAME P0.0, OUT1
IO@SETPORTNAME P1.0, Start?

// 步驟 3：初始化並啟動該驅動裝置
IO@Initio IO1

// 步驟 4：動態宣告內部運算或邏輯虛擬點位 (Virtual IO)
IO@VIRTUALIO RESULT, 1

// 步驟 5：之後所有的控制、閃爍、等待指令，100% 使用 PORTNAME 操作
IO@WaitPortOn::Start?,5,[[Err1]]等待啟動訊號逾時
IO@SetPort::OUT1,0.5,ForceUpdate
IO@ClrPort::OUT1
```

---

## 三、 完整指令函式規格清單

所有的 IO 指令均以 `IO@` 開頭，參數以逗號 `,` 分隔（支援 `::` 或空格前綴）。

### 1. 驅動註冊與初始化指令

| 指令名稱 | 相容別名 | 參數語法 | 參數說明 | 範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddIODriver`** | `AddDrvier`, `AddDriver` | `$io, $driverType, [Config...]` | `$io`: 裝置識別碼<br>`$driverType`: 驅動類型 (MockIO, AdvantechIO, ADLINK8164)<br>`Config`: DEVICE=COM1, OUTSTART=P0.0, OUTPORTS=8... | `IO@AddDrvier IO1, DEVICE=COM1, OUTSTART=P0.0, OUTPORTS=8, INSTART=P1.0, INPORTS=8` |
| **`InitIOPort`** | `Initio`, `InitIO` | `$io` | `$io`: 欲初始化的裝置代號（動態對應已註冊之 Driver） | `IO@Initio IO1` |
| **`ForceUpdate`** | （無） | `$io` | `$io`: 欲強制立即向硬體發送/讀取更新的裝置代號 | `IO@ForceUpdate IO1` |

### 2. 點位命名與虛擬點位指令

| 指令名稱 | 相容別名 | 參數語法 | 參數說明 | 範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`SetPortName`** | `SETPORTNAME` | `$physicalPort, $portName` | `$physicalPort`: 實體埠號 (如 `P0.0`)<br>`$portName`: 語意名稱 (如 `OUT1`、`Start?`，建議感測/詢問類結尾帶 `?` 以利工程閱讀) | `IO@SetPortName P0.0, OUT1`<br>`IO@SetPortName P1.0, Start?` |
| **`AddVirtualIO`** | `VirtualIO`, `VIRTUALIO` | `$name, $initialValue` | `$name`: 虛擬點位名稱<br>`$initialValue`: 初始狀態 (`0`, `1`, `On`, `Off`, `True`, `False`) | `IO@VIRTUALIO RESULT, 1`<br>`IO@AddVirtualIO Station#Ready?, 0` |

### 3. 輸出控制指令

| 指令名稱 | 相容別名 | 參數語法 | 參數說明 | 範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`SetPort`** | `SETPORT` | `$port(,$second,ForceUpdate)` | `$port`: Output PORTNAME 或虛擬點位<br>`$second`: 保持秒數（可選）<br>`ForceUpdate`: 強制立即插隊刷新（可選） | `IO@SetPort OUT1`<br>`IO@SetPort OUT1,0.5`<br>`IO@SetPort OUT1,0.2,ForceUpdate` |
| **`ClrPort`** | `CLRPORT` | `$port(,$second,ForceUpdate)` | 同上，設定指定點位為 OFF (0) | `IO@ClrPort OUT1`<br>`IO@ClrPort OUT1,0.5` |
| **`Flash(OnOff)`** | （無） | `$port, $onSec, $offSec` | 設定指定點位先 ON `$onSec` 秒，再轉為 OFF `$offSec` 秒 | `IO@Flash(OnOff) OUT1,0.2,0.5` |
| **`Flash(OffOn)`** | （無） | `$port, $offSec, $onSec` | 設定指定點位先 OFF `$offSec` 秒，再轉為 ON `$onSec` 秒 | `IO@Flash(OffOn) OUT1,0.5,0.2` |
| **`Shift`** | （無） | `$port1, $port2..., Shift=$n` | 將指定一組點位的值位移 `$n` 位（支援正負方向，邊界補 0） | `IO@Shift OUT1,OUT2,OUT3,Shift=1` |

### 4. 狀態等待與條件檢查指令

| 指令名稱 | 參數語法 | 參數說明 | 範例 |
| :--- | :--- | :--- | :--- |
| **`WaitPortOn`** | `$port, $timeout, [[$label]]$message` | 等待 `$port` 變為 ON (1)，若超時則報警或跳轉至標籤 | `IO@WaitPortOn Start?,5,[[Err1]]等待啟動訊號逾時` |
| **`WaitPortOff`** | `$port, $timeout, [[$label]]$message` | 等待 `$port` 變為 OFF (0)，若超時則報警或跳轉至標籤 | `IO@WaitPortOff Sensor_Busy?,3,Sensor 未復位` |
| **`CheckLogicOn`** | `$logicExpr, $timeout, [[$label]]$message` | 等待布林表達式成立（支援 `&`、`\|`、`!` 與常數） | `IO@CheckLogicOn Start?&!SafetyDoor?,5,條件不滿足` |
| **`CheckLogicOff`**| `$logicExpr, $timeout, [[$label]]$message` | 等待布林表達式不成立 | `IO@CheckLogicOff Alarm1\|Alarm2,3,警報未解除` |

---

## 四、 核心設計注意事項與工程規範

### 1. 統一布林讀寫陣列設計 (Unified Boolean Array Philosophy)
- **不分 Input / Output**：ARCOS IO 本質為統一的布林讀寫陣列 (`bool[]`)。在架構設計上不對點位進行排他性隔離，任何點位皆可被讀取、寫入、等待或位移。
- **全量點位動態呈現（別名 + 虛擬點 + 未命名實體點）**：
  - **自訂別名置頂**：透過 `SETPORTNAME` 賦予語意的別名（如 `OUT1`、`Start?`）排在選單最前段。
  - **完整納入虛擬點**：透過 `VIRTUALIO` 新增之內部邏輯旗標（如 `RESULT`）完整列出。
  - **保留未命名實體點（P0.0 ~ P7.7）**：考慮到工程師在配盤調機、打樣階段可能尚未決定點位名稱，實體點位依然完整保留在選單中，確保未命名點位依然可直接被 Read/Write 操作測試。
- **命名前綴與即時篩選機制**：
  - 面對大量點位（數十至上百點），導引視窗與 CSVI 提供即時搜尋篩選文字框（`🔍 快速篩選點位...`），使用者打字即可即時模糊過濾下拉選項（例如輸入 `OUT`、`P0` 或 `RESULT` 立即定位）。
  - 建議工程師在命名時規劃前綴（如 `CYL_`、`SEN_`、`BTN_`、尾綴 `?` 作為詢問式語意），利用字典字母排序自動歸類，方便在下拉選單中快速選取。

### 2. CSVI 導引視窗（Wizard）嚴禁寫死規範
- **嚴禁在 `Wizard.axaml` 靜態硬編碼 `ComboBoxItem`**：
  ```xml
  <!-- ❌ 嚴禁寫死實體位址 -->
  <ComboBox x:Name="CmbSetPortName">
      <ComboBoxItem Content="P0.0"/>
      <ComboBoxItem Content="P0.1"/>
  </ComboBox>

  <!-- ✅ 正確做法：保持空選單，由 C# 程式碼動態注入 ItemsSource -->
  <ComboBox x:Name="CmbSetPortName" SelectedIndex="0" HorizontalAlignment="Stretch"/>
  ```
- **嚴禁在 `Logic.cs` 開頭無腦塞入 64 筆 `P0.0 ~ P7.7`**：
  ```csharp
  // ❌ 錯誤作法：
  for (int p = 0; p < 8; p++)
      for (int b = 0; b < 8; b++)
          portList.Add($"P{p}.{b}");

  // ✅ 正確作法：
  var portList = new List<string>();
  if (engines?.Io != null)
  {
      // 統一調用 GetConfiguredPortNames() 自動取得所有已命名別名、虛擬點位以及實體點位 (P0.0 ~ P7.7)
      // 未命名點位依然保留，確保調機打樣時未命名點位依然可直接被 Read/Write 測試
      portList.AddRange(engines.Io.GetConfiguredPortNames());
  }
  ```
- **Device 選單必須動態讀取已註冊 Driver**：
  ```csharp
  // ❌ 嚴禁寫死固定陣列：
  var devNames = new[] { "StationIO", "IO1", "IO2", "MockIO" };

  // ✅ 正確作法：
  var devNames = engines?.Io?.GetRegisteredDrivers();
  if (devNames == null || devNames.Count == 0)
  {
      devNames = new[] { "IO1" };
  }
  cmbDev.ItemsSource = devNames;
  ```

### 3. 靜態預解析（Pre-Scan）合約
在主畫面 Sequencer 或子線程視窗中，當工程師開啟任一列的導引視窗前，容器控制項必須先調用 `SyncIoDefinitionsFromRows()`：
```csharp
private void SyncIoDefinitionsFromRows()
{
    if (_vm?.Engines?.Io == null || _rows == null) return;
    foreach (var r in _rows)
    {
        if (string.IsNullOrWhiteSpace(r.Command) || string.IsNullOrWhiteSpace(r.Parameter)) continue;
        _vm.Engines.Io.ScanAndRegister(r.Command, r.Parameter);
    }
}
```
確保任何在前面步驟剛輸入的 `AddDrvier`、`SETPORTNAME` 或 `VIRTUALIO`，能在毫秒級同步進 `IoEngine`。

### 4. CSVI 3.0 四位一體自包含結構
每個 `.csvi` 組件本身是一顆獨立的 .NET 8 二進位 Assembly（PE 格式），內部必須內嵌以下資源：
1. `Logic.cs`：實作 `IParameterGuidance` 與 `IModuleHandler`，提供無頭執行與導引彈窗邏輯。
2. `Wizard.axaml`：Avalonia XAML 參數導引介面（內嵌於 `CSVI.RuntimeXaml.Wizard.axaml`）。
3. `Panel.axaml`：即時除錯與監控前面板（內嵌於 `CSVI.RuntimeXaml.Panel.axaml`）。
4. `CSVI.Manifest.json`：套件中繼資料、修訂作者與版次紀錄。
5. `CSVI.Source.Bundle`：未加密或密碼鎖定的原始碼壓縮包（供現場隨身除錯與重編）。

所有修改必須透過 `CSVI_Studio.exe --mcp` 的 `csvi_compile_and_pack` 工具編譯打包，嚴禁直接放入無 XAML 的空殼 DLL。

---

## 五、 CSVI 3.0 組件封裝狀態對照表

目前 `src/MODULES/IO/` 與 `dist/ARCOS_Runtime/MODULES/IO/` 下的所有組件均已完成動態重構與二進位封裝：

| 項次 | 指令組件檔案 | 核心職責 | 動態資料來源 | CSVI 3.0 封裝狀態 |
| :---: | :--- | :--- | :--- | :---: |
| 1 | **`SetPort.csvi`** | 單點置位 ON / 延遲保持 | `IoEngine.GetConfiguredPortNames()` (含即時搜尋框) | 🟢 封裝完成 |
| 2 | **`ClrPort.csvi`** | 單點復位 OFF / 延遲保持 | `IoEngine.GetConfiguredPortNames()` (含即時搜尋框) | 🟢 封裝完成 |
| 3 | **`Flash(OnOff).csvi`** | 亮滅閃爍脈衝輸出 | `IoEngine.GetConfiguredPortNames()` (含即時搜尋框) | 🟢 封裝完成 |
| 4 | **`Flash(OffOn).csvi`** | 滅亮閃爍脈衝輸出 | `IoEngine.GetConfiguredPortNames()` (含即時搜尋框) | 🟢 封裝完成 |
| 5 | **`WaitPortOn.csvi`** | 等待點位 ON / 支援超時跳轉 | `IoEngine.GetConfiguredPortNames()` (含即時搜尋框) | 🟢 封裝完成 |
| 6 | **`WaitPortOff.csvi`** | 等待點位 OFF / 支援超時跳轉 | `IoEngine.GetConfiguredPortNames()` (含即時搜尋框) | 🟢 封裝完成 |
| 7 | **`CheckLogicOn.csvi`** | 邏輯運算式等待成立 | `LogicEvaluator` 動態運算 | 🟢 封裝完成 |
| 8 | **`CheckLogicOff.csvi`** | 邏輯運算式等待不成立 | `LogicEvaluator` 動態運算 | 🟢 封裝完成 |
| 9 | **`SetPortName.csvi`** | 實體硬體點綁定語意名稱 | `IoEngine.GetPhysicalPortList()` | 🟢 封裝完成 |
| 10 | **`AddVirtualIO.csvi`** | 動態宣告軟體內部虛擬點 | `IoEngine.AddVirtualIO()` | 🟢 封裝完成 |
| 11 | **`AddIODriver.csvi`** | 宣告並註冊硬體驅動實例 | `IoEngine.RegisterDriver()` | 🟢 封裝完成 |
| 12 | **`InitIOPort.csvi`** | 啟動驅動與底層硬體通訊 | `IoEngine.GetRegisteredDrivers()` | 🟢 封裝完成 |
| 13 | **`ForceUpdate.csvi`** | 強制立即刷新指定驅動 | `IoEngine.GetRegisteredDrivers()` | 🟢 封裝完成 |
| 14 | **`Shift.csvi`** | 一組點位狀態整批移位 | 批次點位動態移位運算 | 🟢 封裝完成 |
| 15 | **`IO.csvi`** | IO 模組多合一集合庫 | 整合 14 指令動態通用對話框 | 🟢 封裝完成 |
| 16 | **`DRIVER/MockIO.csvi`** | 模擬串列通訊/TCP 8-bit IO 驅動 | 模擬硬體通訊與即時前面板開關 | 🟢 封裝完成 |
