# MVCon Telnet 遠端通訊與即時控制參考手冊

這是針對 **MVCon Telnet 通訊介面與自動化調試** 專門設計的 Agent 參考指南。身為 MVCon 自動化系統專家，當需要透過遠端通訊對機台進行即時狀態查詢、零副作用健康探測、動態代跑測試或變數動態覆寫（如 AOI 演算法覆核過殺救援）時，請參照以下規範。

---

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到「**Telnet**」、「**遠端連線**」、「**讀取 IO 狀態**」、「**查詢點位**」、「**覆寫變數**」、「**代跑腳本**」或「**健康探測**」時，請自動套用本手冊。

---

## 🛠️ 基本呼叫方式與語法規範

- **工具腳本**：[`scripts/mvcon_telnet.py`](../scripts/mvcon_telnet.py)（工作區路徑：`Skills/MVCon/scripts/mvcon_telnet.py`）
- **終端機呼叫格式**：
  ```bash
  # 本機呼叫 (-h 預設為 127.0.0.1 可省略，強制以單引號包覆 $CMD)
  python.exe Skills/MVCon/scripts/mvcon_telnet.py -p $Port '$CMD'

  # 指定遠端主機 IP
  python.exe Skills/MVCon/scripts/mvcon_telnet.py -h $IP -p $Port '$CMD'

  # 結構化 JSON 輸出 (推薦 Agent 使用)
  python.exe Skills/MVCon/scripts/mvcon_telnet.py -p $Port --json '$CMD'
  ```

> [!IMPORTANT]
> **通訊與呼叫絕對約束**：
> 1. **單行傳送協議**：透過 `$Execute` 傳送腳本時，**整段指令必須壓平成單一行，所有指令純粹以 `;` 連接，絕不可包含任何換行符號（`\n` 或 `\r\n`）**。
> 2. **引號強制約束**：在 Windows PowerShell 呼叫時，$CMD 參數**必須強制使用單引號 `''` 包覆**，防止 PowerShell 將 `@`、`::`、`;` 或 `$` 誤解析為環境變數。

---

## 🩺 連線前置健康探測 (Zero Side-effect Probe)

在發送包含馬達運動或氣缸動作的腳本前，Agent **強烈建議先發送一次零副作用的常數虛擬 IO 查詢**，確認連線與直譯器健康狀態：
```bash
python.exe Skills/MVCon/scripts/mvcon_telnet.py -p 2800 '$GetIO,True'
```
- **預期成功回傳**：
  ```text
  #
  IO: {True: 1}
  ```
- 若能正常返回，代表 MVCon 主程式在線、目標 Port 通暢且核心直譯器未受 Modal 彈窗或死迴圈阻塞。

---

## 📜 支援的通訊指令集 (Supported $CMD)

| 指令 | 類別 | 行數 | 範例與原始回傳 | `--json` 結構化輸出 |
| :--- | :--- | :---: | :--- | :--- |
| **`$Execute`** | 腳本動態代跑 | 2 行 | `$Execute,IO@SetPort::P0.0;System@Delay::100`<br>➔ `#`<br>➔ `#{"ErrorMessage":""}` | 由系統直接返回 JSON |
| **`$SetIO`** | 設定 IO 狀態 | 1 行 | `$SetIO,P0.0=1,P0.1=0`<br>➔ `#` | *(無)* |
| **`$GetIO`** | 讀取 IO 狀態 | 2 行 | `$GetIO,P0.0,P0.1`<br>➔ `#`<br>➔ `IO: {P0.0: 1}, IO: {P0.1: 0}` | `{"P0.0": 1, "P0.1": 0}` |
| **`$SetPoint`** | 設定點位座標 | 1 行 | `$SetPoint,Pick=(1,2,3,4,5,6),Place=(11,22,33,44,55,66)`<br>➔ `#` | *(無)* |
| **`$GetPoint`** | 讀取單點/多點陣列 | 2 行 | `$GetPoint,Pick,Place`<br>➔ `#`<br>➔ `Point: {Pick: [...]}, PointIndex: {Pick: 0}` | `{"Pick": {"coords": [...], "points": [[...]], "count": 1, "index": 0}}` |
| **`$SetVariable`**| 設定/覆寫變數 | 1 行 | `$SetVariable,InspectResult=OK,Count=10`<br>➔ `#` | *(無)* |
| **`$GetVariable`**| 讀取變數數值 | 2 行 | `$GetVariable,InspectResult,Count`<br>➔ `#`<br>➔ `Variable: {InspectResult: OK}, Variable: {Count: 10}` | `{"InspectResult": "OK", "Count": "10"}` |
| **`$GetMotionState`**| 讀取馬達運動狀態 | 2 行 | `$GetMotionState,Robot`<br>➔ `#`<br>➔ `MotionState: { Robot: { State: Idle, ErrorCode: }}` | `{"Robot": {"State": "Idle", "ErrorCode": ""}}` |

---

## 🔀 多工並行通訊埠分配 (Ports 2800 ~ 2812)

- **Port 2800 (ThreadMain / Manager)**：專用於全局監控、心跳探測、狀態讀取與全局變數覆寫；**嚴禁在此 Port 執行長時間的阻斷式延遲或無窮迴圈腳本**，以免阻塞機台主排程。
- **Port 2801 ~ 2812 (Workers 1~12)**：對應 12 個工兵執行緒，專門用於發送獨立工站動作測試或非同步工件處理。

---

## 💡 AI 實戰應用情境

### 情境一：AOI 演算法誤判時的「過殺救援 (Overkill Rescue)」
當自訂演算法 `CustomAOI.vi` 誤判為 NG，但 AI 判斷實為良品時，AI Agent 透過 Telnet 直接覆寫結果：
```bash
python.exe Skills/MVCon/scripts/mvcon_telnet.py -p 2800 '$SetVariable,InspectResult=OK'
```
機台原生腳本接續讀取被修正後的 `InspectResult`，自動走向良品出料流程，達成不停機自癒。

### 情境二：單動測試氣缸或輸出點
```bash
python.exe Skills/MVCon/scripts/mvcon_telnet.py -p 2800 '$SetIO,P0.0=1'
```

### 情境三：安全探測與點位驗證
```bash
python.exe Skills/MVCon/scripts/mvcon_telnet.py -p 2800 --json '$GetPoint,PickPoint'
```
