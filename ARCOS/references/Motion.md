# ARCOS Motion 模組指令手冊

這是針對 **ARCOS Motion 模組** 專門設計的 Agent Skill 原則與指南。

## 啟動條件
當使用者要求編寫或修改 ARCOS 腳本，且內容涉及馬達控制、機構移動、復歸(Home)、路徑規劃及運動狀態等待時，請自動套用本技能。

## 指令語法規範
1. **指令句型**：所有的 Motion 指令都必須以 `Motion@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。
3. **與 Point&Path 模組連動**：`Point&Path` 與 `Motion` 具備重度依賴關係。前者負責「**產生與計算點位**」，後者負責根據點位「**執行運動**」。Point&Path 相關指令說明請參考 [`references/Point&Path.md`](Point&Path.md)。

### 1. 系統組態與初始化指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **AddMotion** | `$motor, $driver(,$parameter...)` | 新增一個 motion 裝置組合 |
| **Config** | `$motor, $parameter, $value` | 設定 Motion 內部控制組態與參數 |
| **SetTool** | `$motor, $toolpoint` | **設定工具座標系 (Robot 專用)**。將 `$toolpoint` 點位設定為該 Robot 的當前 Tool 座標系偏置。 |
| **ExtraCMD** | `$motor, $command` | 直接撰寫該 Robot / 運動控制器的原生指令（Native Controller Command）發送至控制器執行。由於每家品牌/型號的 Robot 控制器原生語法各不相同，各硬體 Driver 內部均自行定義 `ExecuteExtraCommandAsync` 處理函式，且在該 **Driver 的 UI 頁面（Panel / Wizard）上必須載明該 Driver 支援的 `ExtraCMD` 語法說明**。 |

### 2. 基礎移動與控制指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **Home** | `$motor(,$axis,CounterOnly)` | 機構復歸 (Home) |
| **Stop** | `$motor` | 強制停止機構運動 |
| **Go** | `$motor, $axis, $speed, $X(,$Y,$Z,...)` | 多軸線性移動到指定位置 |
| **Go(SingleAxis)** | `$motor, $axis, $speed, $X(,$Y,$Z,...)` | 多軸同動 (單軸聯動) 到指定位置 |

### 3. 點位與陣列移動指令
與 `Point&Path` 模組配合使用的移動指令（**支援笛卡爾座標與 `#Joint` 關節座標混用，由 Driver 自主分析判定**：若給定的點位為 `$point#Joint` 形式，Driver 會自動識別並下達 Robot 的 Joint 運動指令，上層指令不做預先拆解）：
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **GoPoint** | `$motor, $speed, $point` <br>或 `$motor, $speed, ($X,$Y,$Z,$U,$V,$W)` | 多軸移動到 `$point` 點位或直接給定座標陣列（若 `$point` 為 `$point#Joint` 則由 Driver 自動轉為 Robot Joint 指令） |
| **GoPoint(SingleAxis)** | `$motor, $speed, $point` | 多軸同動到 `$point` 指定點位（若 `$point` 為 `$point#Joint` 則由 Driver 自動轉為 Robot Joint 指令） |
| **GoSafe** | `$motor, $speed, $point, {X,Y,Z}=$safepos` | 多軸安全移動，先移到指定軸的 `$safepos` 後再移動 `$point` 的其他軸位置 |
| **GoPallet** | `$motor, $speed, $point` | 陣列移動：多軸移動 `$point`，每呼叫一次就移動到當前陣列位置，並將索引加一。走到最後一個索引時，會把對應的虛擬 IO `$(point)#Last?` 設為 On |   
| **GoSafe(Pallet)** | `$motor, $speed, $point, {X,Y,Z}=$safepos` | 陣列移動：但以 GoSafe 的方式移動到位置，索引遞增與虛擬 IO 設定邏輯同上 |

### 4. 運動路徑 (Path) 設定指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **ClearPath** | `$motor` | 清除該機構先前的運動路徑 |
| **AddPath** | `$motor, $speed, $point` | 新增一節目標點位與速度到運動路徑中（路徑中可混用一般笛卡爾點位與 `$point#Joint` 關節點位，由 Driver 內部逐點分析並轉換為對應的 Robot 運動指令） |
| **RunPath** | `$motor` | 啟動連續路徑運動 (預先透過 AddPath 建構) |

### 5. 狀態取得與等待指令 (支援 Error Handling)
> **注意**：`WaitMotion` 與 `WaitMotion(Error)` 為高風險指令，在撰寫腳本時務必依照 [使用範例](#使用範例) 加入 `ErrorHandler` 進行異常處理。

| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **GetPos** | `$motor` | 讀取機構/Robot 當前位置，Driver 會同時寫入兩個系統點位：以該 Motion 為名稱的 **`$motor`**（笛卡爾座標）以及 **`$motor#Joint`**（Robot 關節座標）。 |
| **GetState** | `$motor(, [[$label]]$message)` | 讀取並更新 Motion 最新狀態與錯誤。此指令會由 Driver 自動將 **`$(motor)#Busy?`** 與 **`$(motor)#Alarm?`** 寫入虛擬 IO，並將**控制器本身的原生錯誤碼**寫入 `Motion#ErrorCode` 中（由各 Driver 直接回報控制器 Error Code，不強行統一重定義）。若發生異常：參數僅提供字串則彈出錯誤視窗；若帶有 `[[$label]]` 則跳轉至錯誤處理區段。 |
| **WaitPosition** | `$motor, $tolerance, $point` <br>或 `$motor, $tolerance, (x,y,z,inf...)` | **等待到位**。等待馬達目前位置與 `$point` 之間的誤差進入 `$tolerance` 範圍內 |
| **WaitMotion** | `$motor(, TimeOut=$second, [[$label]]$message)` | **等待運動完成**。超時後若只帶字串則彈出錯誤視窗；若帶有 `[[$label]]` 則跳轉到錯誤處理區段。同時會自動將控制器錯誤碼寫入 `Motion#ErrorCode` 中 |
| **WaitMotion(Error)** | `$motor(, TimeOut=$second, [[$label]]$message)` | **等待運動並捕捉異常**。超時或發生異常時若只帶字串則彈出錯誤視窗；若帶有 `[[$label]]` 則跳轉到錯誤處理區段。同時會自動將控制器錯誤碼寫入 `Motion#ErrorCode` 中 |

### 6. Motion Driver 內部跨驅動 I/O API (供 IO Driver 調用)
部分 Robot 控制器僅允許單一連線（不允許多個 TCP/Serial 連線），當需要控制 Robot 控制器自帶的 I/O 時，會由 `IO Driver` 透過 `Motion Driver` 開放的內部 Ticket API 進行讀寫配合（非排程器腳本直接呼叫之 `Motion@` 指令）：
- **`SetDO`**：設定 Robot 控制器上的數位輸出點（例如 `Index=0,Value=True`）。
- **`GetDI`**：讀取 Robot 控制器上的數位輸入點並同步至 IO Engine。
- **`GetDO`**：讀取 Robot 控制器上的數位輸出點狀態。
## 使用範例

### 範例：安全移動與運動等待 (包含 Error Handling)
```markdown
// 設定變數
Variable@Set::RobotSpeed,500;
Point&Path@Calculate::P1=(100,200,50);

// 安全點移動
Motion@GoSafe::Robot,$RobotSpeed,P1,Z=0;

// 等待機構移動完成，若 10 秒內未完成則跳轉處理
Motion@WaitMotion::Robot,TimeOut=10,[[RobotError]]Robot Fail;
Thread@Stop::;

// ======== 錯誤處理區段 ========
Thread@ErrorHandler::[[RobotError]];
// 判斷是否為運動本身報錯
Thread@If::Motion#ErrorCode==0;
   // 嘗試重新復歸並重試
   Motion@Home::Robot;
   Thread@ErrorRetry::;
Thread@Else::;
   // 無法排解之硬體異常，終止程式
   Thread@Stop::True,Robot Fail;
Thread@EndIf::
```