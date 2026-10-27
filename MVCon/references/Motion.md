# MVCon Motion 模組指令手冊

這是針對 **MVCon Motion 模組** 專門設計的 Agent Skill 原則與指南。

## 啟動條件
當使用者要求編寫或修改 MVCon 腳本，且內容涉及馬達控制、機構移動、復歸(Home)、路徑規劃及運動狀態等待時，請自動套用本技能。

## 指令語法規範
1. **指令句型**：所有的 Motion 指令都必須以 `Motion@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。
3. **與 Point&Path 模組連動**：`Point&Path` 與 `Motion` 具備重度依賴關係。前者負責「**產生與計算點位**」，後者負責根據點位「**執行運動**」。Point&Path 相關指令說明請參考 [`references/Point&Path.md`](Point&Path.md)。

### 1. 系統組態與初始化指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **AddMotor** | `$motor, $driver(,$parameter...)` | 新增一個 motion 裝置組合 |
| **Config** | `$motor, $parameter, $value` | 設定 Motion 內部控制組態與參數 |
| **ExtraCMD** | `$motor, $command` | 將底層 `$command` 直接發送給控制器處理 |

### 2. 基礎移動與控制指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **Home** | `$motor(,$axis,CounterOnly)` | 機構復歸 (Home) |
| **Stop** | `$motor` | 強制停止機構運動 |
| **Go** | `$motor, $axis, $speed, $X(,$Y,$Z,...)` | 多軸線性移動到指定位置 |
| **Go(SingleAxis)** | `$motor, $axis, $speed, $X(,$Y,$Z,...)` | 多軸同動 (單軸聯動) 到指定位置 |

### 3. 點位與陣列移動指令
與 `Point&Path` 模組配合使用的移動指令：
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **GoPoint** | `$motor, $speed, $point` <br>或 `$motor, $speed, ($X,$Y,$Z,$U,$V,$W)` | 多軸線性移動到 `$point` 點位或直接給定座標陣列 |
| **GoPoint(SingleAxis)** | `$motor, $speed, $point` | 多軸同動到 `$point` 指定點位 |
| **GoSafe** | `$motor, $speed, $point, {X,Y,Z}=$safepos` | 多軸線性移動，先移到指定軸的 `$safepos` 後再移動 `$point` 的其他軸位置 |
| **GoPallet** | `$motor, $speed, $point` | 陣列移動：多軸線性移動 `$point`，每呼叫一次就移動到當前陣列位置，並將索引加一。走到最後一個索引時，會把對應的虛擬 IO `$(point)#Last?` 設為 On |   
| **GoSafe(Pallet)** | `$motor, $speed, $point, {X,Y,Z}=$safepos` | 陣列移動：但以GoSafe的方式移動到位置，索引遞增與虛擬 IO 設定邏輯同上 |

### 4. 運動路徑 (Path) 設定指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **ClearPath** | `$motor` | 清除該機構先前的運動路徑 |
| **AddPath** | `$motor, $speed, $point` | 新增一節目標點位與速度到運動路徑中 |
| **RunPath** | `$motor` | 啟動連續線性路徑運動 (預先透過 AddPath 建構) |

### 5. 狀態取得與等待指令 (支援 Error Handling)
> **注意**：`WaitMotor` 與 `WaitMotor(Error)` 為高風險指令，在撰寫腳本時務必依照 [使用範例](#使用範例) 加入 `ErrorHandler` 進行異常處理。

| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **GetPos** | `$motor` | 讀取馬達當前位置，會存入以該 Motion 為名稱的系統點資料中 |
| **GetState** | `$motor(, [[$label]]$message)` | 讀取並更新 Motion 最新錯誤狀態。若發生異常：參數僅提供字串則彈出錯誤視窗；若帶有 `[[$label]]` 則跳轉至錯誤處理區段。此指令會自動將 `$(motor)#Busy?` 寫入虛擬 IO，並將錯誤碼寫入當前 Thread 變數區的 `Motion#ErrorCode` 中。 |
| **WaitPosition** | `$motor, $tolerance, $point` <br>或 `$motor, $tolerance, (x,y,z,inf...)` | **等待到位**。等待馬達目前位置與 `$point` 之間的誤差進入 `$tolerance` 範圍內 |
| **WaitMotor** | `$motor(, TimeOut=$second, [[$label]]$message)` | **等待運動完成**。超時後若只帶字串則彈出錯誤視窗；若帶有 `[[$label]]` 則跳轉到錯誤處理區段。同時會自動將錯誤碼寫入當前 Thread 變數區的 `Motion#ErrorCode` 中 |
| **WaitMotor(Error)** | `$motor(, TimeOut=$second, [[$label]]$message)` | **等待運動並捕捉異常**。超時或發生異常時若只帶字串則彈出錯誤視窗；若帶有 `[[$label]]` 則跳轉到錯誤處理區段。同時會自動將錯誤碼寫入當前 Thread 變數區的 `Motion#ErrorCode` 中 |
## 使用範例

### 範例：安全移動與運動等待 (包含 Error Handling)
```markdown
// 設定變數
Variable@Set::RobotSpeed,500;
Point&Path@Calculate::P1=(100,200,50);

// 安全點移動
Motion@GoSafe::Robot,$RobotSpeed,P1,Z=0;

// 等待機構移動完成，若 10 秒內未完成則跳轉處理
Motion@WaitMotor::Robot,TimeOut=10,[[RobotError]]Robot Fail;
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