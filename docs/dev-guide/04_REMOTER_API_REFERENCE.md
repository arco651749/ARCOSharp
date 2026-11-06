# 04. ARCOS.Remoter 通訊協定 (Remoter Reference)

`ARCOS.Remoter` 為獨立運行的 TCP 服務（Sidecar），預設監聽 **Port 9527**，提供輕量級純文字行通訊協定，供外部產線系統（MES）、上位 SCADA 或 Python 腳本對 ARCOSharp 進行遠端即時監控與控制。

---

## 1. 連線規範與生命週期

- **傳輸協定**：TCP / IP
- **預設埠號**：`9527`
- **文字編碼**：`UTF-8`
- **終結符號**：每行以 `\r\n` 或 `\n` 作為指令結尾。
- **連線歡迎詞**：連線建立時，伺服器立即主動傳送：
  ```
  200 ARCOS Remoter Sidecar Ready (Port 9527)
  ```

---

## 2. 指令集與回應格式規範 (Command Set)

### 2.1 讀取數位點位 (`$GetIO`)
- **語法**：`$GetIO <portNameOrAlias>`
- **成功回應**：`OK: True` 或 `OK: False`
- **範例**：
  ```
  >>> $GetIO P0.0
  <<< OK: True
  ```

### 2.2 寫入數位點位 (`$SetIO`)
- **語法**：`$SetIO <portNameOrAlias>=<1|0|True|False|On|Off>`
- **成功回應**：`OK`
- **失敗回應**：`ERR: Invalid format, expected $SetIO Port=1`
- **範例**：
  ```
  >>> $SetIO P0.4=1
  <<< OK
  ```

### 2.3 讀取變數 (`$GetVariable`)
- **語法**：`$GetVariable <key> [section]`（未指定 section 預設為 `Global`）
- **成功回應**：`OK: <value>`
- **範例**：
  ```
  >>> $GetVariable CycleCount Production
  <<< OK: 1420
  ```

### 2.4 寫入變數 (`$SetVariable`)
- **語法**：`$SetVariable <key>=<value> [section]`
- **成功回應**：`OK`
- **範例**：
  ```
  >>> $SetVariable TargetYield=99.5 SystemConfig
  <<< OK
  ```

### 2.5 讀取機台空間點位 (`$GetPoint`)
- **語法**：`$GetPoint <pointName>`
- **成功回應**：`OK: (X,Y,Z,U,V,W)`
- **範例**：
  ```
  >>> $GetPoint StandbyPos
  <<< OK: (100.5,200.0,50.0,0,0,0)
  ```

### 2.6 寫入機台空間點位 (`$SetPoint`)
- **語法**：`$SetPoint <pointName>=(X,Y,Z,U,V,W)`
- **成功回應**：`OK`
- **範例**：
  ```
  >>> $SetPoint PickPos=(120.0,300.0,-15.5,0,0,90.0)
  <<< OK
  ```

### 2.7 動態執行即時腳本 (`$Execute`)
- **語法**：`$Execute <cmd1>; <cmd2>; <cmd3>`（以分號分隔多行指令）
- **行為**：伺服器將動態建立臨時 `ExecutionContext (ThreadId: 999)`，循序調用 `CommandExecutor.ExecuteStep`。
- **成功回應**：`OK: Executed`
- **失敗回應**：`ERR: Command failed at line <FailedPC>: <ErrorMessage>`
- **範例**：
  ```
  >>> $Execute System@Log Message Remote Trigger; IO@SetPort DO0,1; System@Delay 0.5; IO@SetPort DO0,0
  <<< OK: Executed
  ```

### 2.8 結束連線 (`QUIT` / `EXIT`)
- **語法**：`QUIT` 或 `EXIT`
- **回應**：
  ```
  <<< 221 Goodbye.
  ```

---

## 3. Python 客戶端調用範例

```python
import socket

def send_remoter_cmd(cmd_str: str) -> str:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.connect(('127.0.0.1', 9527))
        welcome = s.recv(1024).decode('utf-8') # 讀取 200 歡迎訊息
        s.sendall((cmd_str + '\n').encode('utf-8'))
        response = s.recv(4096).decode('utf-8').strip()
        return response

# 測試讀取 IO 與設定變數
print("IO State:", send_remoter_cmd("$GetIO P0.0"))
print("Set Var:", send_remoter_cmd("$SetVariable LotNo=20261106A Batch"))
```

---

## 4. 關鍵架構弱點與安全性審查

> [!WARNING]
> 現行 `ARCOS.Remoter` 存在重大工控安全風險：
> 1. **無身分驗證 (No Auth)**：TCP Socket 監聽 `IPAddress.Any`，未配置任何 Token、密碼或 TLS 加密。任何區域網路內的設備皆可直接調用 `$Execute` 或觸發 IO。
> 2. **阻塞型即時執行**：`$Execute` 執行長時指令（如長時間 Delay 或軸移動等待）時，該 Socket 連線將持續阻塞直到完畢。
> 3. **改善建議**：正式生產環境請將 `IPAddress.Any` 限縮至 `IPAddress.Loopback`，或透過反向代理加上 TLS 與驗證機制。
