# ARCOS IO 模組指令手冊

這是針對 **ARCOS IO 模組** 專門設計的 Agent Skill 原則與指南。

## 啟動條件
當使用者要求編寫或修改 ARCOS 腳本，且內容涉及 IO 埠 (Port) 的開啟、關閉、狀態等待、設備新增或虛擬 IO 設定時，請自動套用本技能。

## 指令語法規範
1. **指令句型**：所有的 IO 指令都必須以 `IO@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。

### 1. 基本控制指令
| 指令 | 參數 | AI 生成格式範例 | 說明 |
| :--- | :--- | :--- | :--- |
| **SetPort** | `$port` | `IO@SetPort::$port` | 設定 `$port` 為 ON |
| **SetPort** | `$port, $second` | `IO@SetPort::$port,0.5` | 設定 `$port` 為 ON，並延時 `$second` 秒 |
| **ClrPort** | `$port` | `IO@ClrPort::$port` | 設定 `$port` 為 OFF |
| **ClrPort** | `$port, $second` | `IO@ClrPort::$port,0.5` | 設定 `$port` 為 OFF，並延時 `$second` 秒 |
| **Flash(OnOff)** | `$port, $onSecond, $offSecond` | `IO@Flash(OnOff)::$port,0.2,0.5` | 設定 `$port` 為 ON 並延時 `$onSecond` 秒，再轉為 OFF 並延時 `$offSecond` 秒 |
| **Flash(OffOn)** | `$port, $offSecond, $onSecond` | `IO@Flash(OffOn)::$port,0.5,0.2` | 設定 `$port` 為 OFF 並延時 `$offSecond` 秒，再轉為 ON 並延時 `$onSecond` 秒 |

### 2. 狀態等待與檢查指令 (支援 Error Handling)
> **注意**：具有 `$timeout=second` 參數的指令，在超時後若只帶 `$message` 則彈出警告視窗；若加上 `[[$label]]`，則不會彈出視窗，而是跳往對應的 `Thread@ErrorHandler` 標籤以進行錯誤處理。

| 指令 | 參數 | AI 生成格式範例 | 說明 |
| :--- | :--- | :--- | :--- |
| **WaitPortOn** | `$port, $timeout, $message` | `IO@WaitPortOn::P0.0,3,Timeout WaitPortOn P0.0` | 等待 `$port` 為 ON，超時後彈出錯誤視窗 |
| **WaitPortOn** | `$port, $timeout, [[$label]]$message` | `IO@WaitPortOn::P0.0,3,[[Err1]]Timeout P0.0` | 等待 `$port` 為 ON，超時後跳到 `[[$label]]` 進行處理 |
| **WaitPortOff** | `$port, $timeout, $message` | `IO@WaitPortOff::P0.0,3,Timeout WaitPortOff P0.0` | 等待 `$port` 為 OFF，超時後彈出錯誤視窗 |
| **WaitPortOff** | `$port, $timeout, [[$label]]$message` | `IO@WaitPortOff::P0.0,3,[[Err1]]Timeout P0.0` | 等待 `$port` 為 OFF，超時後跳到 `[[$label]]` 進行處理 |
| **CheckLogicOn** | `$logic, $timeout, $message` | `IO@CheckLogicOn::P0.0\|P0.1,3,Logic Error` | 等待 `$logic` 為 ON，超時後彈出錯誤視窗 |
| **CheckLogicOn** | `$logic, $timeout, [[$label]]$message` | `IO@CheckLogicOn::P0.0&P0.1,3,[[Err2]]Logic Error` | 等待 `$logic` 為 ON，超時後跳到 `[[$label]]` 進行處理 |
| **CheckLogicOff** | `$logic, $timeout, $message` | `IO@CheckLogicOff::P0.0\|P0.1,3,Logic Error` | 等待 `$logic` 為 OFF，超時後彈出錯誤視窗 |
| **CheckLogicOff** | `$logic, $timeout, [[$label]]$message` | `IO@CheckLogicOff::P0.0&P0.1,3,[[Err2]]Logic Error` | 等待 `$logic` 為 OFF，超時後跳到 `[[$label]]` 進行處理 |

### 3. 進階設定與初始化指令
| 指令 | 參數 | AI 生成格式範例 | 說明 |
| :--- | :--- | :--- | :--- |
| **SetPortName** | `$port, $newname` | `IO@SetPortName::P0.0,Output1`<br>或 `IO@SetPortName::P1.0,Sensor1?` | 重新設定 `$port` 名稱（Input 結尾須帶 `?`，Output 嚴禁帶 `?`） |
| **AddVirtualIO** | `$port, {on,off,0,1,$logic}` | `IO@AddVirtualIO::VPort1,ON` | 若 IO 名稱不存在，則新增一個虛擬 IO 並設定其值；若已存在，則直接設定新值 |
| **AddIODriver** | `$io, $driver, ...` | `IO@AddIODriver::IO1,DriverA` | 增加一個 IO 裝置 |
| **InitIOPort** | `$io` | `IO@InitIOPort::IO1` | 初始化 `$io` 裝置，並開始交換實體 IO 訊號 |
| **ForceUpdate** | `$io` | `IO@ForceUpdate::IO1` | 強制更新 `$io`，否則系統預設每隔 25ms 更新一次 |
| **Shift** | `$io1, $io2(, $io3, ..., Shift=$num)` | `IO@Shift::IO1,IO2,IO3,Shift=1` | 將 `$io1, $io2, $io3...` 等狀態進行位移 `$num` 位 (`$num` 可以為負值，即反向位移) |

## 使用範例

### 範例：新增 IO 裝置
```markdown
IO@AddIODriver::IO1,DriverA,IP=192.168.0.1,OutStart=P0.0,OutPorts=8,InStart=P1.0,InPorts=8;
IO@SetPortName::P0.0,Output1;
IO@SetPortName::P0.1,Output2;
IO@SetPortName::P0.2,Output3;
IO@SetPortName::P0.3,Output4;
IO@SetPortName::P0.4,Output5;
IO@SetPortName::P0.5,Output6;
IO@SetPortName::P0.6,Output7;
IO@SetPortName::P0.7,Output8;
IO@SetPortName::P1.0,Input1?;
IO@SetPortName::P1.1,Input2?;
IO@SetPortName::P1.2,Input3?;
IO@SetPortName::P1.3,Input4?;
IO@SetPortName::P1.4,Input5?;
IO@SetPortName::P1.5,Input6?;
IO@SetPortName::P1.6,Input7?;
IO@SetPortName::P1.7,Input8?;
IO@InitIOPort::IO1
```

### 範例：等待 IO 狀態並搭配錯誤處理
```markdown
IO@WaitPortOn::P0.0,1,[[P0.0WaitTimeOut]]P0.0 TimeOut;
System@Log Message::Done!;
Thread@Stop::;
Thread@ErrorHandler::[[P0.0WaitTimeOut]];
IO@SetPort::P0.0;
Thread@ErrorRetry::
```