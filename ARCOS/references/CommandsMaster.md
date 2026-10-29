# ARCOS 全指令語法速查總表 (CommandsMaster.md)

本文件是專為 **AI Agent** 設計的 ARCOS 全指令速查總表 (Machine-Readable Reference)。
在生成、重構或審查 ARCOS 腳本時，Agent 應優先以本表作為語法簽名 (Signature)、參數格式與防呆邊界的權威依據。

---

## 🚨 AI 生成零猜測最高防呆法則 (Zero-Guessing Invariants)

1. **基本格式**：`<Module>@<Action>::<Param1>,<Param2>...;`（指令以分號 `;` 分隔，最後一行指令結尾**不加** `;`）。
2. **無參數指令必須保留 `::`**：如 `Thread@Stop::;`、`Thread@ErrorRetry::;`、`Thread@Return::;`，嚴禁省略為單一指令名。
3. **`$logic` 比較運算式嚴禁 `$` 前綴**：比較式內的變數與 IO 一律直接書寫，如 `Thread@If::Count > 10 & ~Sensor_Feed?`（**嚴禁**寫成 `$Count` 或 `$Sensor_Feed?`）。
4. **IO 命名約定**：實體/虛擬 Input IO 結尾強制帶 `?`（如 `Sensor_Feed?`）；Output IO 嚴禁帶 `?`（如 `Cyl_Push`）。
5. **外部 VI 呼叫強制帶 `&`**：呼叫外部 LabVIEW VI 預設必須以 Runtime 模式執行：`Call@<VIName>.vi&::...`。`Thread@Call` 僅限調用內部 `.pro` 腳本區段，嚴禁用於調用 VI。
6. **錯誤處理標籤 `[[$label]]`**：帶有 TimeOut 或可能異常的指令，透過 `[[$label]]$msg` 攔截，跳轉至 `Thread@ErrorHandler::[[$label]];`，並以 `Thread@ErrorRetry::;` 或 `Thread@Stop::True,$err;` 終止。
7. **多行腳本透過 Telnet `$Execute` 傳送時**：整段腳本必須壓平為**單一行**，以 `;` 連接，嚴禁任何換行符號（`\n` 或 `\r\n`）。
8. **AIAgent `~` 旁路停用開關**：在 `AIAgent@SuperviseStart` 或 `ErrorHandle` 的 Prompt 前加上 `~`（如 `~Prompt`），可立即停用該 AI 功能（Supervise 轉為純 VI、ErrorHandle 直接執行 Fallback），實現產線零成本快速回退。

---

## 📑 模組索引 (Module Quick Jump)
- [1. IO 模組 (實體與虛擬 IO)](#1-io-模組-實體與虛擬-io)
- [2. Motion 模組 (軸向運動與馬達控制)](#2-motion-模組-軸向運動與馬達控制)
- [3. Point&Path 模組 (點位計算與路徑)](#3-pointpath-模組-點位計算與路徑)
- [4. Thread 模組 (流程控制、多執行緒與異常)](#4-thread-模組-流程控制多執行緒與異常)
- [5. Variable 模組 (變數、陣列、多工分區)](#5-variable-模組-變數陣列多工分區)
- [6. System 模組 (系統延遲、日誌、計時、事件)](#6-system-模組-系統延遲日誌計時事件)
- [7. Call 模組 (外部 LabVIEW VI 呼叫)](#7-call-模組-外部-labview-vi-呼叫)
- [8. AIAgent 模組 (視覺監督與異常後備)](#8-aiagent-模組-視覺監督與異常後備)
- [9. Capture 模組 (攝影機取像與視覺辨識)](#9-capture-模組-攝影機取像與視覺辨識)
- [10. DAQ 模組 (資料擷取、波形與比對)](#10-daq-模組-資料擷取波形與比對)
- [11. User 模組 (人機介面與面板元件)](#11-user-模組-人機介面與面板元件)
- [12. Redis 模組 (跨專案/跨站狀態總線)](#12-redis-模組-跨專案跨站狀態總線)
- [13. Python 模組 (內嵌 Python 運算)](#13-python-模組-內嵌-python-運算)
- [14. MultiStation 模組 (多工站狀態管理)](#14-multistation-模組-多工站狀態管理)
- [15. Telnet 通訊指令集 (TCP 2800~2812)](#15-telnet-通訊指令集-tcp-28002812)

---

## 1. IO 模組 (實體與虛擬 IO)
前綴：`IO@`

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 虛擬 IO / 旗標副作用 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **SetPort** | `IO@SetPort::$port(, $sec)` | `$port`: IO名稱<br>`$sec`: 保持秒數 (選填) | `$port` 設為 ON | 若帶 `$sec`，延時後保持或自動復歸 |
| **ClrPort** | `IO@ClrPort::$port(, $sec)` | `$port`: IO名稱<br>`$sec`: 保持秒數 (選填) | `$port` 設為 OFF | 常用於關閉電磁閥或清除狀態 |
| **Flash(OnOff)** | `IO@Flash(OnOff)::$port, $onSec, $offSec` | `$port`: IO<br>`$onSec`: ON秒數<br>`$offSec`: OFF秒數 | `$port` 先ON後OFF | 常用於警示燈閃爍 |
| **Flash(OffOn)** | `IO@Flash(OffOn)::$port, $offSec, $onSec` | 同上，順序相反 | `$port` 先OFF後ON | 常用於反向重置信號 |
| **WaitPortOn** | `IO@WaitPortOn::$port, $timeout(, [[$label]]$msg)` | `$port`: 等待之輸入IO<br>`$timeout`: 逾時秒數<br>`$label`: 錯誤標籤 (選填) | 無 | 輸入IO結尾必須帶 `?`；帶 `[[$label]]` 則超時不彈窗跳標籤 |
| **WaitPortOff** | `IO@WaitPortOff::$port, $timeout(, [[$label]]$msg)` | `$port`: 等待之輸入IO<br>`$timeout`: 逾時秒數<br>`$label`: 錯誤標籤 (選填) | 無 | 同上，等待信號熄滅 |
| **CheckLogicOn** | `IO@CheckLogicOn::$logic, $timeout(, [[$label]]$msg)` | `$logic`: 複合邏輯式 (如 `IO1 & ~IO2`)<br>`$timeout`: 秒數 | 無 | 邏輯式內變數與IO嚴禁帶 `$` |
| **CheckLogicOff** | `IO@CheckLogicOff::$logic, $timeout(, [[$label]]$msg)` | 同上，等待邏輯為假 | 無 | 邏輯式內變數與IO嚴禁帶 `$` |
| **AddVirtualIO** | `IO@AddVirtualIO::$port, $val` | `$port`: 虛擬IO名<br>`$val`: {On, Off, 0, 1, $logic} | 建立或更新虛擬IO | 若名稱不存在自動創建，存在則覆寫 |
| **AddIODriver** | `IO@AddIODriver::$io, $driver(, $params...)` | `$io`: 裝置識別碼<br>`$driver`: 驅動名稱 | 註冊 IO 驅動硬體 | 須搭配 `InitIOPort` 啟動 |
| **SetPortName** | `IO@SetPortName::$port, $newname` | `$port`: 實體埠號 (如 P0.0)<br>`$newname`: 別名 | 重新命名埠號 | Input 名稱末尾務必加 `?`，Output 嚴禁加 `?` |
| **InitIOPort** | `IO@InitIOPort::$io` | `$io`: 裝置識別碼 | 啟動底層通訊輪詢 | 預設輪詢週期為 25ms |
| **ForceUpdateIO**| `IO@ForceUpdateIO::$io` | `$io`: 裝置識別碼 | 即時強制同步實體IO | 繞過 25ms 週期限制立即更新 |
| **Shift** | `IO@Shift::$io1, $io2..., Shift=$num` | 多個 IO 列表<br>`$num`: 位移量 (可為負) | 批次移位 IO 狀態 | 常用於步進輸送帶載具狀態傳遞 |

---

## 2. Motion 模組 (軸向運動與馬達控制)
前綴：`Motion@`

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 虛擬 IO / 旗標副作用 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **AddMotor** | `Motion@AddMotor::$motor, $driver(, $params...)` | `$motor`: 機構名稱<br>`$driver`: 軸卡驅動 | 註冊 Motion 機構 | 通常於機台初始化腳本執行 |
| **Config** | `Motion@Config::$motor, $param, $value` | 內部參數設定 | 無 | 依硬體手冊設定速度、加減速 |
| **Home** | `Motion@Home::$motor(, $axis, CounterOnly)` | `$motor`: 機構名稱<br>`$axis`: 指定軸向 (選填) | 自動更新座標為原點 | 軸復歸指令，建議於 Prog 96 檢查原點旗標 |
| **Stop** | `Motion@Stop::$motor` | `$motor`: 機構名稱 | 減速停止機構所有軸 | EMG Stop (Prog 99) 必備指令 |
| **Go** | `Motion@Go::$motor, $axis, $speed, $pos...` | `$motor`, 軸向, 速度, 目標數值 | `$(motor)#Busy?` 設為 On | 多軸絕對座標移動 |
| **GoPoint** | `Motion@GoPoint::$motor, $speed, $point` | `$motor`: 機構<br>`$speed`: 速度<br>`$point`: 點位名稱或座標 | `$(motor)#Busy?` 設為 On | 支援點位修飾符（如 `PT@Z(50)`, `PT@XY`） |
| **GoSafe** | `Motion@GoSafe::$motor, $speed, $point, {X,Y,Z}=$safepos` | 先升/降指定軸至安全位，再移其餘軸 | 同上 | 快速門型避障標準指令 |
| **GoPallet** | `Motion@GoPallet::$motor, $speed, $point` | 多軸移動至當前陣列位置，執行完索引+1 | 終點自動設 `$(point)#Last?` 為 On | 常用於矩陣取放，末點後需檢查 `#Last?` |
| **GoSafe(Pallet)**| `Motion@GoSafe(Pallet)::$motor, $speed, $point, Z=$safe` | 陣列移動並帶安全退點，索引自動+1 | 終點自動設 `$(point)#Last?` 為 On | 產線料盤取放兼顧安全退點首選 |
| **ClearPath** | `Motion@ClearPath::$motor` | 清除運動緩衝區 | 無 | 建構路徑前先清除 |
| **AddPath** | `Motion@AddPath::$motor, $speed, $point` | 將點位與速度壓入連續軌跡隊列 | 無 | 搭配 `RunPath` 執行連續同動 |
| **RunPath** | `Motion@RunPath::$motor` | 啟動預先建構的平滑連續軌跡 | `$(motor)#Busy?` 設為 On | 塗膠、同動焊接必備 |
| **WaitMotor** | `Motion@WaitMotor::$motor(, TimeOut=$sec, [[$label]]$msg)` | 等待馬達停止動作 | 寫入 `Motion#ErrorCode` | **高風險指令**：量產版務必掛 `[[$label]]` |
| **WaitMotor(Error)**| `Motion@WaitMotor(Error)::$motor(, TimeOut=$sec, [[$label]]$msg)` | 等待馬達並捕捉驅動器 Alarm | 寫入 `Motion#ErrorCode` | 包含驅動器硬體警報檢測 |
| **WaitPosition**| `Motion@WaitPosition::$motor, $tol, $point` | 等待位置誤差進入 `$tol` 容許公差 | 無 | 精度到位判定 |
| **GetPos** | `Motion@GetPos::$motor` | 讀取機構當前位置 | 存入系統點位 `$motor` | 即時座標同步 |
| **GetState** | `Motion@GetState::$motor(, [[$label]]$msg)` | 查詢機構錯誤狀態 | 寫入 `$(motor)#Busy?`, `Motion#ErrorCode` | 異常排查與連線探測使用 |

---

## 3. Point&Path 模組 (點位計算與路徑)
前綴：`Point&Path@`

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 說明與核心語意 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **Calculate** | `Point&Path@Calculate::$P=(X,Y,Z,U,V,W)` | 單點定義（使用 `=`） | 宣告或覆寫單點座標 | 未給之軸向預設為 0；寫 `inf` 保持原機台座標 |
| **Calculate(Array)**| `Point&Path@Calculate::$P<=(X,Y,Z...)` | 陣列添加（使用 `<=`） | 將點位依序壓入 `$P` 陣列末端 | 第一點使用 `=` 定義，第二點起用 `<=` 擴充 |
| **Calculate(Math)** | `Point&Path@Calculate::$P3=$P1 {+-*/} $P2` | 點對點 / 點對常數四則運算 | 各軸分別運算或所有軸同乘/除常數 | 支援 `P1+P2` 或 `P1+10.5` |
| **EnableAxis** | `Point&Path@EnableAxis::$point, $axis` | 限制該點作動之軸向 | 屏蔽未指定的軸（轉為 inf） | 格式如 `XYZ`、`XY`、`Z` |
| **Pallet1D** | `Point&Path@Pallet1D::$point, $num` | `$point[0]`起點, `$point[1]`終點, `$num`總數 | 自動產生 1D 等距陣列 | `$point` 內必須預先具備 2 個端點 |
| **Pallet2D** | `Point&Path@Pallet2D::$point, $nx, $ny` | `$point[0]`原點, `[1]`X終點, `[2]`Y終點 | 自動產生 2D 矩陣陣列 | `$point` 內必須預先具備 3 個基準點 |
| **Circle2DPath** | `Point&Path@Circle2DPath::$P, $deg1, $deg2, $sec` | 圓心[0], 起點[1], 終點[2], 起迄角, 分割數 | 自動依圓弧分割出點陣列路徑 | 配合 `Motion@AddPath` 跑圓弧運動 |
| **ResetPalletIndex**| `Point&Path@ResetPalletIndex::$point(, $idx)` | 重設料盤內部游標，未給預設 0 | 滿盤換盤後必須呼叫此指令歸零 | 換新 Tray 盤時必備 |
| **CheckPalletLast?**| `Point&Path@CheckPalletLast?::$point` | 檢查內部游標是否在最後一顆 | 結果輸出至 `$(point)#Last?` | 亦可於 `GoPallet` 執行完自動觸發 |
| **Select(X=A?B:C)** | `Point&Path@Select(X=A?B:C)::$P=$logic ? $P1 : $P2` | 三元條件選擇點位 | 依據條件動態二選一目標點 | 條件式 `$logic` 內嚴禁帶 `$` 前綴 |
| **Point2WS** | `Point&Path@Point2WS::$point, $ws` | 機構座標轉換為工作區座標 | 多相機、多手臂手眼校正換算 | 需先以 `Calculate` 建立好 `$ws` 原點與軸向 |
| **WS2Point** | `Point&Path@WS2Point::$point, $ws` | 工作區座標轉回機構實際座標 | 讓機構移動至視覺辨識出的空間位置 | 影像對位轉手臂座標最常用指令 |
| **SelfMotion** | `Point&Path@SelfMotion::$point, $base, $new` | 剛體旋轉與平移轉換 | 工件角度補正 | 以 `$base` 與 `$new` 之偏差做旋轉 |
| **ChkInRangeXY** | `Point&Path@ChkInRangeXY::$point, $range` | 檢查點是否在矩形範圍內 | 結果輸出至 `$(point)#InRange?` | 防撞軟體極限檢查 |

### 📌 點位修飾符 `@` (In-line Modifiers)
- `PT@指定軸`：僅保留指定軸數值，其餘軸自動轉為 `inf`（如 `Motion@GoPoint::Robot,500,PT@XY`）。
- `PT@指定軸(新數值)`：覆寫指定軸數值，其餘軸自動轉為 `inf`（如 `Motion@GoPoint::Robot,500,PT@Z(50)`）。

---

## 4. Thread 模組 (流程控制、多執行緒與異常)
前綴：`Thread@`

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 說明與核心語意 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **Label** | `Thread@Label::$label;` | `$label`: 標籤名稱 | 設定程式跳躍標記點 | 嚴禁與其他 Label 重名 |
| **Jump** | `Thread@Jump::$label(, $logic);` | 標籤名, 觸發條件 (選填) | 跳躍至 `$label` 執行 | 若帶 `$logic` 則條件成立才跳 |
| **Call** | `Thread@Call::$prog(, $logic);` | `$prog`: 內部程式區段 (ProNo/名) | 呼叫內部腳本副程式 | **嚴禁用於呼叫 .vi**，僅限調度內部程式區 |
| **Return** | `Thread@Return::($logic);` | 條件式 (選填) | 從副程式返回呼叫處下一行 | 無參數必須保留 `::;` |
| **Repeat** | `Thread@Repeat::($logic);` | 條件式 (選填) | 當前程式段從第 1 行重頭執行 | Program 0 輪詢常駐結尾必備 |
| **Stop** | `Thread@Stop::($logic, $msg);` | 條件 (選填), 錯誤訊息 (選填) | 終止當前 Thread | 無參數必須保留 `::;`；帶 `$msg` 則向 Manager 報錯 |
| **If / ElseIf**| `Thread@If::$logic;` / `Thread@ElseIf::$logic;` | `$logic`: 條件式 | 條件分支判斷 | 運算式內變數與 IO 嚴禁帶 `$` |
| **Else / EndIf**| `Thread@Else::;` / `Thread@EndIf::;` | 無參數 | 分支結束標記 | 必須保留 `::;`，兩者不可遺漏 |
| **ForeachStart**| `Thread@ForeachStart::$label;` | 迴圈標籤 | 陣列遍歷起始點 | 與 `ForeachLoop` 成對出現 |
| **ForeachLoop** | `Thread@ForeachLoop::$label, $array;` | 標籤, 陣列變數 | 游標+1 並跳回標籤，直至走完 | 取值直接用 `$array` |
| **ForeachBreak**| `Thread@ForeachBreak::$logic;` | 條件成立強制跳出迴圈 | 提前終止迴圈 | `$logic` 內變數嚴禁帶 `$` |
| **Run** | `Thread@Run::$prog, $thread(, $idx, $logic);` | 程式段, 執行緒號 (0~12), 條件 | 派發並啟動執行緒 | Manager 派工必帶 `& ~ThreadX#Run?` 防重入鎖 |
| **Wait** | `Thread@Wait::$thread1(, $thread2...);` | 等待指定主執行緒 (1~12) 結束 | 同步阻斷等待 | 可逗號串接多個 Thread |
| **Suspend** | `Thread@Suspend::$threadNo(, $logic);` | 強制掛起/暫停指定執行緒 | 停止該 Thread 運作 | EMG Stop 或 SystemStop 常用 |
| **Resume** | `Thread@Resume::$threadNo(, $logic);` | 恢復被掛起的執行緒 | 重新喚醒執行 | 搭配 Suspend 使用 |
| **SubThreadStart**| `Thread@SubThreadStart::$subname(&)(&);` | 子執行緒名稱 | 啟動輕量平行任務 | `&`: 不受 SystemPause 影響<br>`&&`: 併發多實例陣列處理 |
| **SubThreadEnd**| `Thread@SubThreadEnd::$subname;` | 子執行緒結束邊界 | 封閉子任務程式塊 | 名稱必須與 `SubThreadStart` 完全一致 |
| **SubThreadWait**| `Thread@SubThreadWait::$sub1(, $sub2...);` | 等待子執行緒執行完成 | 支援 `SKIPNOTFIND=1` | 同步主流程與子任務交握點 |
| **SubThreadSuspend**| `Thread@SubThreadSuspend::$subname;` | 強制停止子執行緒 | 異常時強制回收子任務 | 常用於超時守護行程關閉 |
| **SubThreadScript** | `Thread@SubThreadScript::$sub, $file.init;` | 載入獨立 `.init` 腳本為子執行緒 | 動態腳本注入 | 適合外掛單元動作 |
| **ErrorHandler**| `Thread@ErrorHandler::[[$label]];` | 錯誤跳轉標籤 | 捕捉該 Thread 內的指令異常 | 每個 Thread/SubThread 必須有各自獨立的 Handler |
| **ErrorRetry** | `Thread@ErrorRetry::($logic, $offset);` | 原處重試 | 跳回原發生錯誤的指令重新執行 | 無參數必須保留 `::;` |
| **MutePopupMessage**| `Thread@MutePopupMessage::1;` (或 0) | `1`=無人化靜音, `0`=跳彈窗 | 工站級彈窗分級控制 | 必須在 Worker (Prog 1~12) 開頭設定，非 Prog 0 全域 |
| **KeySectionLock** | `Thread@KeySectionLock::$lock, $timeout, $msg;`| 互斥鎖名稱, 秒數, 逾時說明 | 跨執行緒硬體資源互斥鎖 | 避免多線程同時搶同一隻相機或掃描器 |
| **KeySectionUnLock**| `Thread@KeySectionUnLock::$lock;` | 釋放互斥鎖 | 歸還硬體使用權 | 關鍵操作完成後務必釋放 |

---

## 5. Variable 模組 (變數、陣列、多工分區)
前綴：`Variable@`

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 說明與核心語意 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **Set** | `Variable@Set::$var, $val(, $sec)` | 變數名, 數值/字串, 分區 (選填) | 賦值變數；支援陣列 `[A;B;C]` | 存取值直接用 `$var`；內插用 `$(var)` |
| **Inc / Dec** | `Variable@Inc::$var(, $step, $sec)` | 變數名, 增減量 (選填，預設 1) | 數值累加 / 遞減 | 重試計數器或良品計數必備 |
| **Calculate** | `Variable@Calculate::$var=算式(, $sec)` | 數學運算式 | 執行文字/數值公式計算並賦值 | 如 `Variable@Calculate::Total=A+B*2` |
| **Delete** | `Variable@Delete::$var(, $sec)` | 變數名稱 | 釋放記憶體中的變數 | 清除臨時變數 |
| **Rename** | `Variable@Rename::$old, $new(, $sec)` | 舊名, 新名 | 重新命名變數 | 保持作用域不變 |
| **Append** | `Variable@Append::$var, $str(, $sec)` | 變數名, 追加字串 | 字串尾端拼接 | 常用於 Log 或 MES 封包組裝 |
| **Trim** | `Variable@Trim::$var, $start, $end` | 起始位, 結束位 | 擷取子字串 | 條碼解析與序號過濾 |
| **Slice** | `Variable@Slice::$var, $char(, $sec)` | 變數名, 分隔字元 | 以指定字符將字串切割為陣列 | 例如以 `,` 切割 CSV 欄位 |
| **Joint** | `Variable@Joint::$var, $char(, $sec)` | 變數名, 連接字元 | 將陣列元素以字符合併為字串 | 反向拼裝資料包 |
| **LResetIndex**| `Variable@LResetIndex::$var, $idx(, $sec)` | 陣列變數名, 游標索引值 | 強制指定陣列目前讀取游標 | 陣列重頭讀取時設為 0 |
| **LSetIndexValue**| `Variable@LSetIndexValue::$var, $idx, $val` | 變數名, 索引, 新數值 | 直接修改陣列指定索引之數值 | 陣列隨機寫入 |
| **LPush / RPush** | `Variable@LPush::$var, $val` / `RPush` | 陣列變數名, 數值 | 從左端 (L) 或右端 (R) 推入新元素 | 佇列 (Queue / Stack) 操作 |
| **LPop / RPop** | `Variable@LPop::$var` / `RPop` | 陣列變數名 | 從左端 (L) 或右端 (R) 彈出並移除元素 | 佇列消費取值 |
| **LRemove** | `Variable@LRemove::$var, $val` | 變數名, 目標值 | 清除陣列中所有等於 `$val` 的項目 | 資料過濾 |
| **LReverse** | `Variable@LReverse::$var` | 變數名 | 顛倒陣列元素順序 | 逆序處理 |
| **LGetIndexSize**| `Variable@LGetIndexSize::$var` | 變數名 | 取得目前內部索引游標與陣列長度 | 寫入系統變數供長度校驗 |
| **LFindIndexSet**| `Variable@LFindIndexSet::$key, $val, $col1...` | Key陣列, 搜尋值, 連動陣列列表 | **資料庫主鍵檢索**：命中後同步切換關聯陣列索引 | 多穴位或多配方快速查表核心指令 |
| **SAdd** | `Variable@SAdd::$var, $val` | 集合變數名, 數值 | 加入唯一值至集合 (Set，不重複) | 批次序號防重比對 |
| **SUnion / SDiff**| `Variable@SUnion::$dest, $set1, $set2` | 目標集合, 集合1, 集合2 | 集合聯集 / 差集 / 交集運算 | 批次過濾與差異比對 |
| **GetPointElement**| `Variable@GetPointElement::$var=X(P1)` | 變數名, 軸向與點位 | 提取點位中指定軸 (X/Y/Z/U/V/W) 之數值 | 空間座標拆解運算 |
| **VariableSave**| `Variable@VariableSave::$file(, $v1, $v2...)` | 檔案路徑, 變數清單 (選填) | 將變數持久化儲存至硬碟 | 預設存全部；指定時僅存特定變數 |
| **VariableLoad**| `Variable@VariableLoad::$file` | 檔案路徑 | 從硬碟檔載入並覆寫變數 | 配方切換或斷電復歸使用 |
| **Publish / Subscribe**| `Variable@Publish::$var` / `Subscribe::$var` | 變數名稱 | 全域事件發送 (Pub) 與阻斷接收 (Sub) | 跨執行緒輕量事件通知 |

---

## 6. System 模組 (系統延遲、日誌、計時、事件)
前綴：`System@`

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 說明與核心語意 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **#Comment----->>**| `System@#Comment----->>::$msg;` | 註解文字 | 腳本行內結構化註解 | 提升腳本可讀性 |
| **Delay** | `System@Delay::$second;` | `$second`: 秒數 (浮點數) | 當前執行緒延遲等待 | Manager 輪詢必須帶 `0.1`，避免 CPU 100% |
| **Log Message**| `System@Log Message::$msg;` | 訊息文字 (支援 `$(var)`) | 記錄一般資訊至 Log 視窗與檔案 | 檔案存於 `Log/yyyymmdd-hhmmss.log` |
| **Log Error** | `System@Log Error::$msg;` | 錯誤訊息 | 記錄致命錯誤並觸發全機急停 (EMG Stop) | 慎用！會引發系統中斷 |
| **Clear Message**| `System@Clear Message::;` | 無參數 | 清除 Log 視窗文字 | 必須保留 `::;` |
| **TimerReset** | `System@TimerReset::$timer, $second;` | 計時器名稱, 倒數秒數 | 初始化計時器 | 搭配 `TimerCheck` 使用 |
| **TimerCheck** | `System@TimerCheck::$timer;` | 計時器名稱 | 檢查是否逾時 | 逾時自動將 IO `$(timer)#Timeout?` 設為 On |
| **CounterReset**| `System@CounterReset::$cnt, $target;` | 計數器名稱, 目標次數 | 初始化計數器 | 搭配 `CounterCheck` 使用 |
| **CounterCheck**| `System@CounterCheck::$cnt;` | 計數器名稱 | 計數器減 1 並檢查歸零 | 歸零自動將 IO `$(cnt)#Over?` 設為 On |
| **MessageBox** | `System@MessageBox::$msg;` | 對話框文字 | 彈出模態對話框，卡控等待人員按確定 | 生產量產模式慎用，避免阻塞無人產線 |
| **WarningBox** | `System@WarningBox::$logic, $msg;` | 條件式, 提示文字 | 當條件為真時彈出警告詢問是否繼續 | 條件式內嚴禁帶 `$` |
| **ProgramSection**| `System@ProgramSection::$no, $name;` | 編號 (0~99), 區段名稱 | 命名當前程式區段 | 方便架構化識別 |
| **ProgramLoad** | `System@ProgramLoad::$file.pro;` | 專案檔名 | 載入並切換至新工程檔 | 機種整線大換模使用 |
| **SystemPause** | `System@SystemPause::;` | 無參數 | 全域暫停（觸發 Prog 95） | 掛起所有普通 Thread，但帶 `&` 的守護緒不中斷 |
| **SystemResume**| `System@SystemResume::;` | 無參數 | 全域恢復運作（觸發 Prog 94） | 喚醒被 SystemPause 暫停的任務 |
| **IncludeModule**| `System@IncludeModule::$module;` | 模組名稱 (如 Redis, Python) | 載入 `ExternalModules/$module` 外掛 | 調用 Redis/Python 前必須先 Include |
| **ParameterSet**| `System@ParameterSet::$key, $val(, $sec);` | 鍵, 值, 分區 | 寫入系統全域配方參數 | 支援獨立分區 |
| **ParameterSave**| `System@ParameterSave::$file;` | 設定檔名 | 備份當前系統參數到硬碟 | 配方存檔 |
| **ParameterLoad**| `System@ParameterLoad::$file;` | 設定檔名 | 從硬碟讀取配方參數覆寫 | 開機或配方切換時加載 |

---

## 7. Call 模組 (外部 LabVIEW VI 呼叫)
前綴：`Call@`

| 呼叫模式 | 語法簽名 (Signature) | 視窗行為 | 執行緒特性 | 適用場景 |
| :--- | :--- | :--- | :--- | :--- |
| **Debug 模式** | `Call@<VI>.vi::$p1, $p2...` | 開啟 FrontPanel 視窗 | 單一實例 | 現場研發、手動單動與演算法調試 |
| **Runtime 模式** | `Call@<VI>.vi&::$p1, $p2...` | **不開啟視窗 (背景執行)** | 單一實例 | **AI Agent 預設生成模式 (最常用)** |
| **Reentrant 模式**| `Call@<VI>.vi&&::$p1, $p2...`| 不開啟視窗 | 支援多線程並行可重入 | 多穴位平行呼叫同一演算法 VI |

### 📌 參數與回傳語法：
- **純輸入參數**：`Call@MyVI.vi&::Param1,Param2;`
- **接收回傳值**：最後一個參數放入變數名：`Call@Calc.vi&::Input1,Input2,ReturnVar;`
- **錯誤跳轉攔截**：`Call@Check.vi&::Cam1,[[ErrVI]]Vision Failed;` ➔ 跳至 `Thread@ErrorHandler::[[ErrVI]];`

---

## 8. AIAgent 模組 (視覺監督與異常後備)
前綴：`AIAgent@`

> [!IMPORTANT]
> **「~」停用機制 (Universal Bypass Convention)**：
> 在 `$prompt` 最前方加上 **`~`**（例如 `~Prompt`），即可**立即停用該項 AI 功能**，達成產線零成本快速回退。

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 說明與核心語意 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **SuperviseStart**| `AIAgent@SuperviseStart::$prompt;`<br>或 `AIAgent@SuperviseStart::~$prompt;` | 當前工站任務與檢測提示詞 | 標記 AI 監督區段起點。<br>**若帶 `~` 前綴則直接停用 AI 監督** | 與 `SuperviseEnd` 成對；腳本首行需 `System@IncludeModule::AIAgent;` |
| **SuperviseEnd** | `AIAgent@SuperviseEnd::;` | 無參數 (保留 `::`) | **觸發點**：經 Port 2900 送出 JSON，於 Port 2901 等待 AI 決策回傳 (`DONE:` / `ERROR:`) | 無參數必須保留 `::;`；AI 回傳 `DONE:[[Label]]` 時觸發原生 ErrorHandler，回傳 `ERROR:` 則停機 |
| **ErrorHandle** | `AIAgent@ErrorHandle::$prompt, Message=[[$label]]$fallback;`<br>或 `AIAgent@ErrorHandle::~$prompt, Message=...;` | 異常描述, 後備標籤/警報碼 | 觸發 AI 現場排查 (Port 2900，於 2901 等待，逾時 15 秒)。<br>**若帶 `~` 前綴則直接跳轉 Fallback** | 處置成功回傳 `DONE:` 後呼叫 `Thread@ErrorRetry::;` 原處重試；失敗回傳 `ERROR:` 或超時走 `$fallback` |

---

## 9. Capture 模組 (攝影機取像與視覺辨識)
前綴：`Capture@`

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 虛擬 IO / 旗標副作用 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **AddCam** | `Capture@AddCam::$cam, $driver, $param` | 攝影機名, 驅動, 參數 | 註冊相機硬體 | 開機初始化配置 |
| **Config** | `Capture@Config::$cam, $param, $val` | 內部曝光、增益設定 | 無 | 調光調焦參數 |
| **Snap** | `Capture@Snap::$cam(, $imaqs, [[$label]]$msg)` | 相機名, 影像陣列 (選填), 錯誤標籤 | 成功自動設 `$(cam)#OK?` 為 On | 若略過 `$imaqs` 需保留逗號：`Snap::Cam1,,[[Err]]msg` |
| **Trigger** | `Capture@Trigger::$cam(, $imaqs, [[$label]]$msg)` | 相機名, 影像陣列 (選填), 錯誤標籤 | 成功自動設 `$(cam)#OK?` 為 On | 硬體外觸發飛拍使用 |
| **ImageSave** | `Capture@ImageSave::$cam, $file.png` | 相機名, 儲存檔名 | 存入硬碟 Picture/ 目錄 | 常用於 NG 存圖或 AI 監督複查 |
| **LoadFile** | `Capture@LoadFile::$cam, $file.png` | 相機名, 讀取圖檔 | 載入記憶體作為虛擬取像 | 離線演算法回放測試 |
| **ClearImaq** | `Capture@ClearImaq::$imaqs` | 影像陣列名稱 | 釋放記憶體影像陣列 | 批次飛拍前清空陣列 |
| **CoordSet** | `Capture@CoordSet::$cam, $points` | 相機名, 3 點基準陣列 | 建立影像座標系 (原點, X, Y) | 影像空間幾何校正 |
| **ROISet** | `Capture@ROISet::$cam, $roi_str` | 相機名, ROI 字串 | 設定感興趣區域 (Region of Interest)| UI 上標定區域代號 |
| **PatternMatchOne**| `Capture@PatternMatchOne::$cam, $file, $score, $pt(, ANGLE=A~B)` | 相機, 範本圖, 閥值, 結果點, 角度 | 找到自動設 `$(pt)#Find?` 為 On | 2D 幾何特徵對位標準指令 |
| **FindCircle(OutIn)**| `Capture@FindCircle(OutIn)::$cam, $pt` | 由外向內邊界擬合求圓心與半徑 | 找到自動設 `$(pt)#Find?` 為 On | 圓孔或晶圓對位，$pt.H 為半徑 |
| **FindEdge** | `Capture@FindEdge::$cam, $pt` | 尋找單一邊界線 | 找到自動設 `$(pt)#Find?` 為 On | 產品邊界對齊 |
| **Display2User** | `Capture@Display2User::$cam, $control` | 相機名, UI 圖片元件名 | 即時將影像投影至使用者介面 | 視覺面板即時監看 |

---

## 10. DAQ 模組 (資料擷取、波形與比對)
前綴：`DAQ@`

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 說明與核心語意 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **AddDAQ** | `DAQ@AddDAQ::$daq, $driver, $param` | 裝置名, 驅動, 參數 | 註冊擷取卡 (如 NI-DAQ) | 系統初始化執行 |
| **Start / Stop**| `DAQ@Start::$daq;` / `Stop::$daq;` | 裝置名 | 啟動 / 停止連續背景資料採樣 | 連續波形擷取開關 |
| **Wait** | `DAQ@Wait::$daq(, TimeOut=$s, [[$label]]$msg)`| 裝置名, 逾時, 錯誤標籤 | 等待新一筆合法資料抵達 | 捕捉擷取超時異常 |
| **AnalogIn** | `DAQ@AnalogIn::$daq(, $waveform)` | 裝置名, 存入波形變數 (選填) | 讀取單點即時電壓/電流/壓力數值 | 感測器即時監控 |
| **AnalogInWaveForm**| `DAQ@AnalogInWaveForm::$daq, $sec, $waveform`| 裝置名, 採樣秒數, 波形變數 | 連續收集指定秒數的波形序列陣列 | 伺服壓裝、扭力曲線收集 |
| **Filter** | `DAQ@Filter::$waveform, $filterType(, $param)` | 波形變數, 濾波模式 | 套用高/低通或均值數位濾波去除毛刺 | 平滑原始雜訊 |
| **CompareMaxMinInRange**| `DAQ@CompareMaxMinInRange::$wf, $max, $min` | 波形, 上限, 下限 | 檢查波形峰谷極值是否全在公差內 | 結果寫入系統判定旗標 |
| **CompareCoverBy**| `DAQ@CompareCoverBy::$wf, $specWave` | 測試波形, 包絡線標準樣板 | **包絡線檢測**：比對波形是否完全落在上下公差帶內 | 壓裝品質判定黃金標準 |
| **Display2User**| `DAQ@Display2User::$waveform, $control` | 波形變數, UI 圖表控制項 | 將波形即時繪製至人機介面圖表 | 現場工程師即時監控 |

---

## 11. User 模組 (人機介面與面板元件)
前綴：`User@`

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 說明與核心語意 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **Param2UserPanel**| `User@Param2UserPanel::;` | 無參數 | 強制將底層參數全部刷新至 HMI 面板 | 必須保留 `::;` |
| **ButtonValue** | `User@ButtonValue::$btn, $logic` | 按鈕名, ON/OFF | 改變按鈕燈號顯示狀態 (不觸發點擊事件) | 純視覺狀態回饋 |
| **ButtonTrigger** | `User@ButtonTrigger::$btn, $logic` | 按鈕名, ON/OFF | 程式化點擊按鈕 (觸發按鈕關聯的事件程序) | 模擬人工按鈕點擊 |
| **ButtonBlinking**| `User@ButtonBlinking::$btn, $logic` | 按鈕名, 1/0 | 啟動/關閉按鈕持續閃爍提示 | 提示作業員按壓 |
| **ButtonText** | `User@ButtonText::$btn, $text` | 按鈕名, 顯示字串 | 動態修改按鈕表面文字 | 如 "啟動" 變為 "暫停中" |
| **TextBoxValue**| `User@TextBoxValue::$box, $text` | 文字框名, 內容字串 | 填寫文字框內容 (純顯示，不觸發事件) | 顯示條碼或測試狀態 |
| **TabValue** | `User@TabValue::$tab, $pageIdxOrName` | 分頁元件名, 索引/頁面名 | 切換 HMI 分頁畫面 (純顯示，不觸發事件) | 自動切換至測試頁或檢驗頁 |
| **TabTrigger** | `User@TabTrigger::$tab, $pageIdxOrName` | 分頁元件名, 索引/頁面名 | 程式化切換分頁 (觸發換頁關聯事件) | 帶連動邏輯之換頁 |

---

## 12. Redis 模組 (跨專案/跨站狀態總線)
前綴：`Redis@`（使用前必須 `System@IncludeModule::Redis;`）

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 說明與核心語意 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **CreateSession** | `Redis@CreateSession::$session, $host:$port` | 連線名, IP:Port | 建立 Redis 連線通道 (預設 Key 為連線名) | 亦可支援 `$session, $key@$host:$port` |
| **Set** | `Redis@Set::$session, $value` | 連線名, 寫入值 | 寫入目標 Key 值至 Redis 總線 | 支援字串與 IO 狀態 |
| **Get** | `Redis@Get::$session(, $var@$sec)` | 連線名, 接收變數 (選填) | 讀取 Redis Key 值至本地變數 | 預設存入同名區域變數 |
| **Publish** | `Redis@Publish::$session, $msg` | 連線名, 廣播字串 | 跨工站廣播即時事件發佈 | 多台機台即時急停廣播 |
| **Subscribe** | `Redis@Subscribe::$session` | 連線名 | 阻塞式訂閱監聽廣播訊息 | 跨機台交握通知 |
| **LPush / LPop** | `Redis@LPush::$session, $val(, Dir=Start/End)` | 連線名, 數值, 方向 | 操作 Redis List 佇列 (先進先出/後進先出) | 工件履歷流動佇列 |
| **BLPop** | `Redis@BLPop::$session, TimeOut=$sec` | 連線名, 阻塞秒數 | 阻塞式從 List 頭部取出工件序號 | 站點入料阻塞等待標準指令 |

---

## 13. Python 模組 (內嵌 Python 運算)
前綴：`Python@`（使用前必須 `System@IncludeModule::Python;`，限 C:\Python310 環境）

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 說明與核心語意 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **Script** | `Python@Script::$pythonCode;` | 多行 Python 程式碼 (以 0x0A 換行) | 在內嵌 Python 環境執行一段腳本 | 結尾需加 `;` 配合 ARCOS 規範 |
| **File** | `Python@File::$script.py;` | 檔案路徑 | 執行指定 Python 檔案 | 複雜演算法建議拆成 .py 檔呼叫 |
| **SetVariable** | `Python@SetVariable::$var, $val` | ARCOS 變數名, 同步值 | 將 ARCOS 變數設定注入至 Python 全域命名空間 | 支援直接賦值同步 |
| **GetVariable** | `Python@GetVariable::$pyvar(, ARCOSName=$var)`| Python 變數名, ARCOS 變數名 | 從 Python 提取運算結果寫回 ARCOS 變數 | 演算法計算結果回傳 |
| **SetPoint** | `Python@SetPoint::$point, PythonName=$py` | ARCOS 點位, Python 變數名 | 將點位以 NumPy Array 格式注入 Python | 支援高維矩陣運算 |
| **GetPoint** | `Python@GetPoint::$py, ARCOSName=$point` | Python 變數名, ARCOS 點位 | 將 Python 的 NumPy 陣列轉回 ARCOS 點位 | 軌跡規劃演算法回傳 |
| **SetCapture** | `Python@SetCapture::$cam, PythonName=$py` | 相機名, OpenCV 變數名 | 將相機影像轉換為 OpenCV Mat 格式傳給 Python | AI 模型推論輸入 |
| **GetCapture** | `Python@GetCapture::$py, ARCOSName=$cam` | OpenCV 變數名, 相機名 | 將 Python 繪圖/標註後的影像傳回相機記憶體 | 畫框顯示檢測結果 |

---

## 14. MultiStation 模組 (多工站狀態管理)
前綴：`MultiStation@`

| 指令 (Command) | 語法簽名 (Signature) | 參數說明 (Parameters) | 說明與核心語意 | 防呆與注意事項 |
| :--- | :--- | :--- | :--- | :--- |
| **SetStationON** | `MultiStation@SetStationON::$stNo` | 工站號碼 | 標記工站狀態為啟用 (ON) | 治具就位或工件入站 |
| **SetStationOFF**| `MultiStation@SetStationOFF::$stNo` | 工站號碼 | 標記工站狀態為閒置 (OFF) | 出料或空站 |
| **SetStationOK** | `MultiStation@SetStationOK::$stNo` | 工站號碼 | 標記該站加工/檢測品質為 OK | 良品旗標設定 |
| **SetStationNG** | `MultiStation@SetStationNG::$stNo` | 工站號碼 | 標記該站加工/檢測品質為 NG | 不良品旗標設定 |
| **StateShift** | `MultiStation@StateShift::$direction` | 移位方向 | 環狀/直線流水線工件狀態整批移位 | 轉盤 (Index Table) 旋轉後狀態同步移位 |
| **UpdateIndicator**| `MultiStation@UpdateIndicator::;` | 無參數 | 將所有工站狀態同步更新至 UI 指示燈 | 必須保留 `::;` |
| **ResetDefault** | `MultiStation@ResetDefault::;` | 無參數 | 重設所有工站為預設初始狀態 | 必須保留 `::;` |
| **ChkStationRunState**| `MultiStation@ChkStationRunState::$stNo` | 工站號碼 | 查詢該站運行狀態，寫入虛擬 IO | 派工前檢查工站是否 Busy |

---

## 15. Telnet 通訊指令集 (TCP 2800~2812)
底層協議：文字串流以 `\r\n` 結尾。回傳第一行 `#` 代表成功，`?` 代表語法或命名錯誤。

| 通訊指令 ($CMD) | 終端機呼叫範例 | 原始回傳結構 (Line 1 / Line 2) | `--json` 結構化輸出 | 應用場景 |
| :--- | :--- | :--- | :--- | :--- |
| **`$GetIO`** | `python.exe arcos_telnet.py -p 2800 '$GetIO,True,P0.0'` | `#`<br>`IO: {True: 1}, IO: {P0.0: 0}` | `{"True": 1, "P0.0": 0}` | 零副作用健康探測、IO 狀態輪詢 |
| **`$SetIO`** | `python.exe arcos_telnet.py -p 2800 '$SetIO,P0.0=1'` | `#` | *(無)* | 單動測試輸出閥、遠端點火 |
| **`$GetVariable`**| `python.exe arcos_telnet.py -p 2800 '$GetVariable,InspectResult'` | `#`<br>`Variable: {InspectResult: OK}` | `{"InspectResult": "OK"}` | 讀取檢驗結果、生產計數、警報碼 |
| **`$SetVariable`**| `python.exe arcos_telnet.py -p 2800 '$SetVariable,InspectResult=OK'` | `#` | *(無)* | **AOI 演算法過殺救援**、變數動態覆寫放行 |
| **`$GetPoint`** | `python.exe arcos_telnet.py -p 2800 '$GetPoint,PickPoint'` | `#`<br>`Point: {PickPoint: [1,2,3,0,0,0,0]}` | `{"PickPoint": {"coords": [...]}}` | 讀取軸點位座標與料盤索引 |
| **`$SetPoint`** | `python.exe arcos_telnet.py -p 2800 '$SetPoint,P1=(10,20,30,0,0,0)'` | `#` | *(無)* | 視覺對位空間座標偏差動態補償 |
| **`$GetMotionState`**| `python.exe arcos_telnet.py -p 2800 '$GetMotionState,Robot'`| `#`<br>`MotionState: {Robot: {State: Idle}}`| `{"Robot": {"State": "Idle"}}` | 遠端軸狀態與警報檢測 |
| **`$Execute`** | `python.exe arcos_telnet.py -p 2800 '$Execute,IO@SetPort::P0.0;System@Delay::0.1'` | `#`<br>`#{"ErrorMessage":""}` | 由系統直接返回 JSON | **動態代跑完整 ARCOS 腳本**（必須壓平成單一行） |

---

## 🛠️ AI Agent 腳本輸出前自我查核清單 (Pre-flight Checklist)
在生成任何腳本或回應前，Agent 請在內部依據以下 6 點秒速確認：
1. [ ] 指令前綴是否一律帶有 `@`（如 `IO@`、`Motion@`、`Thread@`）？
2. [ ] 指令與參數之間是否皆以 `::` 分隔？無參數指令是否皆保留了 `::;`？
3. [ ] 所有的 `$logic` 條件式內，變數與 IO 名稱是否皆**沒有** `$` 前綴？
4. [ ] 所有的輸入型 IO 名稱結尾是否皆具備 `?`？輸出型 IO 是否皆未帶 `?`？
5. [ ] 呼叫外部 LabVIEW VI 是否皆帶有 `&`（Runtime 模式）？
6. [ ] 所有的 TimeOut 與 Wait 型指令是否皆已掛載 `[[$label]]` 錯誤處理跳轉？
