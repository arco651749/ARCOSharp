# ARCOS 全指令語法速查總表 (CommandsMaster.md — v2.0)

本文件是專為 **AI Agent** 設計的 ARCOS 全指令速查總表 (Machine-Readable Reference)，已與 [docs/COMMAND_SYNTAX_AND_SCHEMA_SPEC.md](../../docs/COMMAND_SYNTAX_AND_SCHEMA_SPEC.md) 100% 同步，涵蓋全系統 **14 大模組共 246 條指令**。

---

## 🚨 AI 生成零猜測最高防呆法則 (v2.0 Zero-Guessing Invariants)

1. **四段式 v2.0 標準語法**（同時 100% 向下相容舊版位置語法）：
   - `Module@Action [:: <必填輸入>] [{<選填覆寫>}] [-> <輸出/側寫目標>] [[[ErrorLabel]]ErrorMsg]`
   - 無任何參數的指令可省略 `::`（如 `Thread@Stop`、`Thread@Return`、`Hello@Init`）。
2. **硬體控制卡語意鍵名（嚴禁混用 `dev`）**：
   - IO 卡：`card` ｜ 軸控馬達：`mot` ｜ 相機：`cam` ｜ 資料擷取卡：`daq`
3. **`logic=` 與 `expr=` 不加外層引號**：
   - 邏輯式與運算式內直接書寫空格與比較運算子（如 `logic=Start? & !Alarm?`、`expr=$(A) + $(B)`），變數與 IO 名稱於 `logic=` 內嚴禁加 `$` 前綴；一般含空格文字（如 `msg="Production Started"`）則維持雙引號。
4. **IO 命名約定**：實體/虛擬 Input IO 結尾強制帶 `?`（如 `Sensor_Feed?`）；Output IO 嚴禁帶 `?`（如 `Cyl_Push`）。
5. **輸出與副作用分離**：主寫入目標置於 `::` 首參（裸名不帶 `$`）；選填/側通道輸出置於 `->`（裸名不帶 `$`）；系統隱式旗標（`#OK?`、`#On?`、`#Found?`、`#Over?`、`#Timeout?`）由引擎自動維護。
6. **錯誤處理標籤 `[[Label]]`**：標記 `[err]` 之指令可於句尾掛載 `[[ErrLabel]]錯誤訊息`，跳轉至 `Thread@ErrorHandler :: label=ErrLabel`。
7. **`Redis` 本機自動啟動與多 Session 規則**：載入 `REDIS` 模組時自動啟動隨附本機 `127.0.0.1:6379` 作為預設 Session（省略 `{session=...}` 直接讀寫）；`Redis@CreateSession :: session=Name {url=...}` 用於連線其他外部 Redis 或獨立 DB；統一使用 `key` 與 `val`。
8. **`MultiStation` 統一工位模型**：透過 `MultiStation@AddStation` 建立命名或 1-based 料盤工站，`工站.工位` 自動綁定 IO / Variable；`SetOn` / `SetOK` 直接以 `logic` 設定狀態；`StateShift` 採環形緩衝區（`+shift` / `-shift`），直線輸送帶另以 `SetOn :: ..., logic=False` 清頭/尾即可；與 `Point&Path` 解耦，`FindFirstOK`/`FindEmpty` 輸出 0-based `index` 串接 `Point&Path@ResetPalletIndex`。
9. **AIAgent 旁路開關**：v2.0 顯式使用 `{bypass=true}`（對應舊版 `~Prompt` 前綴）。
10. **外部 CSVI / VI 呼叫**：外部獨立 CSVI 透過 `Call@<Name>.csvi& :: ...`（Runtime 模式）呼叫；`Thread@Call` 僅限內部 `.pro` 程式區段。

---

## 📑 模組索引 (14 模組 246 條指令 + Telnet)

1. `IO`（14）｜2. `SYSTEM`（21）｜3. `THREAD`（31）｜4. `VARIABLE`（34）｜5. `POINT&PATH`（22）
6. `MOTION`（21）｜7. `CAPTURE`（28）｜8. `DAQ`（18）｜9. `USER`（21）｜10. `PYTHON`（10）
11. `REDIS`（9）｜12. `MULTISTATION`（12）｜13. `AIAGENT`（3）｜14. `HELLO`（2）｜15. `Telnet` 通訊指令集

---

## 1. `IO` 模組（14 條指令）

| 指令 (`IO@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`AddIODriver`** | `card`, `driver` | `device`(""), `outStart`("P0.0"), `outPorts`(8), `inStart`("P1.0"), `inPorts`(8) | `[effect: reg driver $(card)]` | `IO@AddIODriver :: card=IO1, driver=MockIO {device=COM1, outStart=P0.0, outPorts=8, inStart=P1.0, inPorts=8}` | `IO1, MockIO, DEVICE=..., ...` |
| **`InitIOPort`** | `card` | — | `[effect: open $(card)]` | `IO@InitIOPort :: card=IO1` | `$io` |
| **`ForceUpdate`** | `card` | — | `[effect: sync HW $(card)]` | `IO@ForceUpdate :: card=IO1` | `$io` |
| **`SetPortName`** | `io`, `alias` | — | `[effect: alias $(io)<->$(alias)]` | `IO@SetPortName :: io=P0.0, alias=OUT1` | `$physicalPort, $portName` |
| **`AddVirtualIO`** | `io` | `logic`(False) | `[effect: create $(io) = eval($(logic))]` | `IO@AddVirtualIO :: io=CycleOK? {logic=Start? & !Alarm?}` | `$name, $logic` |
| **`SetPort`** | `io` | `holdSec`(0), `forceUpdate`*(false) | `[effect: $(io)=ON]` | `IO@SetPort :: io=OUT1 {holdSec=0.5, forceUpdate=true}` | `$port[, $second[, ForceUpdate]]` |
| **`ClrPort`** | `io` | `holdSec`(0), `forceUpdate`*(false) | `[effect: $(io)=OFF]` | `IO@ClrPort :: io=OUT1 {holdSec=0.2}` | `$port[, $second[, ForceUpdate]]` |
| **`Flash(OnOff)`** | `io`, `onSec`, `offSec` | — | `[effect: pulse $(io) 1->0]` | `IO@Flash(OnOff) :: io=OUT1, onSec=0.2, offSec=0.5` | `$port, $onSec, $offSec` |
| **`Flash(OffOn)`** | `io`, `offSec`, `onSec` | — | `[effect: pulse $(io) 0->1]` | `IO@Flash(OffOn) :: io=OUT1, offSec=0.5, onSec=0.2` | `$port, $offSec, $onSec` |
| **`Shift`** | `ios` | `shift`(1) | `[effect: shift $(ios)]` | `IO@Shift :: ios=[OUT1;OUT2;OUT3] {shift=1}` | `OUT1,OUT2,OUT3,Shift=1` |
| **`WaitPortOn`** `[err]` | `io` | `timeout`*(0=無限) | `[effect: wait $(io)==ON]` | `IO@WaitPortOn :: io=Start? {timeout=5} [[Err1]]啟動逾時` | `$port, $timeout, [[$label]]$msg` |
| **`WaitPortOff`** `[err]` | `io` | `timeout`*(0=無限) | `[effect: wait $(io)==OFF]` | `IO@WaitPortOff :: io=Busy? {timeout=3} [[Err1]]未復位` | `$port, $timeout, [[$label]]$msg` |
| **`CheckLogicOn`** `[err]` | `logic` | `timeout`*(0=無限) | `[effect: wait $(logic)==true]` | `IO@CheckLogicOn :: logic=Start? & !Door? {timeout=5} [[Err]]條件不滿足` | `$logic, $timeout, [[$label]]$msg` |
| **`CheckLogicOff`** `[err]` | `logic` | `timeout`*(0=無限) | `[effect: wait $(logic)==false]` | `IO@CheckLogicOff :: logic=Alarm1 \| Alarm2 {timeout=3}` | `$logic, $timeout, [[$label]]$msg` |

---

## 2. `SYSTEM` 模組（21 條指令）

| 指令 (`System@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`Delay`** | `sec` | — | `[effect: wait $(sec)s]` | `System@Delay :: sec=0.5` | `$seconds` |
| **`Log Message`** | `msg` | — | `[effect: append log]` | `System@Log Message :: msg="Production Started"` | `$message` |
| **`Log Error`** | `msg` | — | `[effect: fail thread]` | `System@Log Error :: msg="Vacuum Sensor Fault"` | `$message` |
| **`Clear Message`** | — | — | `[effect: clear UI logs]` | `System@Clear Message` | (無參) |
| **`Clear Error`** | — | — | `[effect: clear EMG & errors]` | `System@Clear Error` | (無參) |
| **`Note`** (`#Comment----->>`) | `text` | — | *(無副作用，純註解)* | `System@Note :: text="=== 進料段初始化 ==="` | `$comment` |
| **`MessageBox`** | `msg` | — | `[effect: block until OK]` | `System@MessageBox :: msg="請確認治具已鎖緊"` | `$message` |
| **`WarningBox`** | `msg` | `logic`("True") | `[effect: prompt Continue/Stop]` | `System@WarningBox :: msg="料盤即將耗盡" {logic=LowTray?}` | `[$logic, ]$message` |
| **`Pause`** | — | `threads`*("ALL"), `logic`("True") | `[effect: pause $(threads)]` | `System@Pause {threads=Thread1, logic=DoorOpen?}` | `[$thread...][, CONDITION=...]` |
| **`Resume`** | — | `threads`*("ALL"), `logic`("True") | `[effect: resume $(threads)]` | `System@Resume {threads=Thread1}` | `[$thread...][, CONDITION=...]` |
| **`TimerReset`** | `timer` | `target`(0) | `[effect: reset $(timer)#Timeout?]` | `System@TimerReset :: timer=T_Heat {target=30}` | `$timerName[, $timeout]` |
| **`TimerCheck`** | `timer` | — | `[effect: update $(timer)#Timeout?]` | `System@TimerCheck :: timer=T_Heat` | `$timerName` |
| **`CounterReset`** | `counter` | `target`(0) | `[effect: reset $(counter)#Over?]` | `System@CounterReset :: counter=C_Retry {target=3}` | `$cntName[, $targetCount]` |
| **`CounterCheck`** | `counter` | — | `[effect: dec & update $(counter)#Over?]` | `System@CounterCheck :: counter=C_Retry` | `$cntName` |
| **`Record`** | `rec` | `msg`("") | `[effect: timestamp $(rec)]` | `System@Record :: rec=CT_Pick {msg=Start}` | `$recName[, $msg]` |
| **`LogRecordCycleTime`** | `rec` | `prefix`("") | `-> [opt] var` / `[effect: log CT $(rec)]` | `System@LogRecordCycleTime :: rec=CT_Pick {prefix="Pick CT:"} -> var=CycleSec` | `$recName[, $prefix]` |
| **`ProgramLoad`** | `file` | — | `[effect: reload prog slot(s)]` | `System@ProgramLoad :: file=Master.prog` | `$filePath` |
| **`ProgramSection`** | `section` | `title`("") | `[effect: switch ctx.Section=$(section)]` | `System@ProgramSection :: section=StationA {title=Load}` | `$section[, $progName]` |
| **`IncludeModule`** | `module` | — | `[effect: reload module $(module)]` | `System@IncludeModule :: module=IO` | `$moduleName` |
| **`UserPanelOpen`** | `panel` | `alias`(""), `activate`(true) | `[effect: mount UserPanel $(panel)]` | `System@UserPanelOpen :: panel=UserPanel.csvi {alias=StationB, activate=false}` | `$panelSpec` |
| **`UserPanelClose`** | `panel` | — | `[effect: close UserPanel $(panel)]` | `System@UserPanelClose :: panel=StationB` | `$panelName` |

> **註**：舊 `System@UserPanel.csvi` 屬於無原始碼殼層/舊版別名，新腳本統一使用 `UserPanelOpen` 與 `UserPanelClose`。

---

## 3. `THREAD` 模組（31 條指令）

#### A. 結構化條件與迴圈（AI 首選控制流）

| 指令 (`Thread@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`If`** | `logic` | — | `[effect: branch block]` | `Thread@If :: logic=Start? & !Alarm?` | `$logicExpr` |
| **`ElseIf`** | `logic` | — | `[effect: branch block]` | `Thread@ElseIf :: logic=Manual?` | `$logicExpr` |
| **`Else`** | — | — | `[effect: branch block]` | `Thread@Else` | (無參) |
| **`EndIf`** | — | — | `[effect: close If block]` | `Thread@EndIf` | (無參) |
| **`ForeachStart`** | `label` | — | `[effect: mark loop head $(label)]` | `Thread@ForeachStart :: label=LoopItems` | `$label` |
| **`ForeachLoop`** | `label`, `vars` | — | `[effect: LMoveNext $(vars) & loop $(label)]` | `Thread@ForeachLoop :: label=LoopItems, vars=[ItemList;PosList]` | `$label, $arr1[, $arr2...]` |
| **`ForeachBreak`** | — | `logic`("True") | `[effect: exit loop]` | `Thread@ForeachBreak {logic=Found?}` | `[$logicExpr]` |
| **`ForeachContinue`** | — | `logic`("True") | `[effect: next iteration]` | `Thread@ForeachContinue {logic=Skip?}` | `[$logicExpr]` |

#### B. 子程序與線程調度（Main / Worker / SubThread）

| 指令 (`Thread@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`Call`** | `prog` | `logic`("True") | `[effect: call $(prog)]` | `Thread@Call :: prog=SubInit {logic=NeedInit?}` | `$prog[, $logicExpr]` |
| **`Return`** | — | `logic`("True") | `[effect: pop call stack]` | `Thread@Return {logic=Done?}` | `[$logicExpr]` |
| **`Run`** | `prog`, `thread` | `startLine`(1/Label), `logic`("True") | `[effect: start Thread$(thread)#Run?]` | `Thread@Run :: prog=Prog1, thread=1 {startLine=InitStep, logic=Auto?}` | `$prog, $threadNo[, $startLineOrLabel[, $logic]]` |
| **`Wait`** | `threads` | — | `[effect: wait !$(threads)#Run?]` | `Thread@Wait :: threads=[Thread1;Thread2]` | `$t1[, $t2...]` |
| **`Suspend`** | `thread` | `logic`("True") | `[effect: stop Thread$(thread)]` | `Thread@Suspend :: thread=1 {logic=Err?}` | `$threadNo[, $logicExpr]` |
| **`Resume`** | `thread` | `logic`("True") | `[effect: resume Thread$(thread)]` | `Thread@Resume :: thread=1 {logic=ResumeBtn?}` | `$threadNo[, $logicExpr]` |
| **`Stop`** | — | `logic`("True"), `msg`("") | `[effect: halt current thread]` | `Thread@Stop {logic=Abort?, msg=使用者中止}` | `[$logicExpr[, $msg]]` |
| **`SubThreadStart`** | `sub` | `logic`("True") | `[effect: spawn $(sub)#Run?]` | `Thread@SubThreadStart :: sub=CamTask {logic=EnCam?}` | `$subName[, $logicExpr]` |
| **`SubThreadEnd`** | `sub` | — | `[effect: end inline SubThread $(sub)]` | `Thread@SubThreadEnd :: sub=CamTask` | `$subName` |
| **`SubThreadScript`** | `sub`, `file` | — | `[effect: spawn $(sub) from $(file)]` | `Thread@SubThreadScript :: sub=LoadTask, file=Load.init` | `$subName, $initFile` |
| **`SubThreadSuspend`** | `sub` | — | `[effect: stop SubThread $(sub)]` | `Thread@SubThreadSuspend :: sub=CamTask` | `$subName` |
| **`SubThreadWait`** | `subs` | `skipNotFound`(false) | `[effect: wait SubThread $(subs)]` | `Thread@SubThreadWait :: subs=[CamTask;IoTask] {skipNotFound=true}` | `$sub1[, $sub2...][, SKIPNOTFIND=1]` |

#### C. 互斥鎖、單步控制與異常處理

| 指令 (`Thread@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`KeySectionLock`** | `lock` | `timeout`*(5.0), `msg`("") | `[effect: acquire mutex $(lock)]` | `Thread@KeySectionLock :: lock=GantryZone {timeout=10, msg=機構區佔用逾時}` | `$lockName[, $timeout[, $msg]]` |
| **`KeySectionUnLock`** | `lock` | — | `[effect: release mutex $(lock)]` | `Thread@KeySectionUnLock :: lock=GantryZone` | `$lockName` |
| **`GoOn`** | — | `logic`("True"), `msg`("") | `[effect: halt current thread if !$(logic)]` | `Thread@GoOn {logic=MaterialOK?, msg=缺料中止}` | `[$logicExpr[, $msg]]` |
| **`Repeat`** | — | `logic`("True") | `[effect: jump line 1]` | `Thread@Repeat {logic=AutoRun?}` | `[$logicExpr]` |
| **`MutePopupMessage`** | `enabled` | — | `[effect: ctx.IsMuted=$(enabled)]` | `Thread@MutePopupMessage :: enabled=true` | `1` / `0` |
| **`ErrorHandler`** | `label` | — | `[effect: mark error handler $(label)]` | `Thread@ErrorHandler :: label=ErrRecover` | `$label` |
| **`ErrorRetry`** | — | `logic`("True"), `offset`(0) | `[effect: retry failed cmd (+$(offset))]` | `Thread@ErrorRetry {logic=RetryOK?, offset=0}` | `[$logicExpr[, $lineOffset]]` |
| **`Label`** | `label` | — | *(跳轉錨點)* | `Thread@Label :: label=RetryPoint` | `$labelName` |
| **`Jump`** ⚠️ | `label` | `logic`("True") | `[effect: PC = $(label)]` *(AI 審查警示)* | `Thread@Jump :: label=RetryPoint {logic=NeedRetry?}` | `$label[, $logicExpr]` |
| **`RunNextCMD`** ⚠️ | — | `logic`("True") | `[effect: skip next if !$(logic)]` *(AI 審查警示)* | `Thread@RunNextCMD {logic=DebugMode?}` | `[$logicExpr]` |
| **`SkipNextCMD`** ⚠️ | — | `logic`("True") | `[effect: skip next if $(logic)]` *(AI 審查警示)* | `Thread@SkipNextCMD {logic=DryRun?}` | `[$logicExpr]` |

> ⚠️ **AI 編碼審查政策（Audit Policy）**：`Jump`、`RunNextCMD`、`SkipNextCMD` 在 `CommandSchema` 中標記 `AiAuditWarning = true`。舊腳本完全相容，但 AI 撰寫新流程時應優先使用 `If / ElseIf / Else / EndIf`、`Foreach` 與 `Call`；若 AI 產出包含上述三指令，驗證器將標記 `W002` 提醒審查者人工複查控制流。

---

## 4. `VARIABLE` 模組（34 條指令）

| 指令 (`Variable@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`Set`** | `var`, `val` | `section`("") | `[effect: $(var)=$(val)]` | `Variable@Set :: var=Count, val=10 {section=Global}` |
| **`Delete`** | `var` | `section`("") | `[effect: delete $(var)]` | `Variable@Delete :: var=Temp` |
| **`Rename`** | `var`, `to` | `section`("") | `[effect: rename $(var)->$(to)]` | `Variable@Rename :: var=OldKey, to=NewKey` |
| **`Inc`** | `var` | `step`(1), `section`("") | `[effect: $(var)+=$(step)]` | `Variable@Inc :: var=Count {step=2}` |
| **`Dec`** | `var` | `step`(1), `section`("") | `[effect: $(var)-=$(step)]` | `Variable@Dec :: var=Count {step=1}` |
| **`Calculate`** | `var`, `expr` | `section`("") | `[effect: $(var)=eval($(expr))]` | `Variable@Calculate :: var=Total, expr=A + B` |
| **`Select`** | `var`, `logic`, `true`, `false` | `section`("") | `[effect: $(var)=?:]` | `Variable@Select :: var=Result, logic=Pass?, true=OK, false=NG` |
| **`Append`** | `var`, `val` | `section`("") | `[effect: $(var)+=$(val)]` | `Variable@Append :: var=LogStr, val="_Done"` |
| **`Trim`** | `var`, `start`, `end` | `section`("") | `[effect: trim $(var)]` | `Variable@Trim :: var=Barcode, start=0, end=8` |
| **`Slice`** | `var`, `sep` | `section`("") | `[effect: slice $(var)]` | `Variable@Slice :: var=CsvRow, sep=","` |
| **`Joint`** | `var`, `sep` | `section`("") | `[effect: join $(var)]` | `Variable@Joint :: var=Items, sep="-"` |
| **`LPush`** | `var`, `val` | `section`("") | `[effect: lpush $(var)]` | `Variable@LPush :: var=Queue, val=Item1` |
| **`RPush`** | `var`, `val` | `section`("") | `[effect: rpush $(var)]` | `Variable@RPush :: var=Queue, val=Item2` |
| **`LPop`** | `var` | `section`("") | `-> [opt] out` / `[effect: lpop $(var)]` | `Variable@LPop :: var=Queue -> out=HeadVal` |
| **`RPop`** | `var` | `section`("") | `-> [opt] out` / `[effect: rpop $(var)]` | `Variable@RPop :: var=Queue -> out=TailVal` |
| **`LInsert`** | `var`, `val`, `logic` | `section`("") | `[effect: linsert $(var)]` | `Variable@LInsert :: var=Arr, val=5, logic=>0` |
| **`LRemove`** | `var`, `val` | `section`("") | `[effect: lremove $(val)]` | `Variable@LRemove :: var=Arr, val=BadItem` |
| **`LResetIndex`** | `var` | `index`(0), `section`("") | `[effect: $(var).index=$(index)]` | `Variable@LResetIndex :: var=Arr {index=0}` |
| **`LSetIndexValue`** | `var`, `index`, `val` | `section`("") | `[effect: $(var)[$(index)]=$(val)]` | `Variable@LSetIndexValue :: var=Arr, index=2, val=NewVal` |
| **`LReverse`** | `var` | `section`("") | `[effect: reverse $(var)]` | `Variable@LReverse :: var=Arr` |
| **`LGetIndexSize`** | `var` | `section`("") | `[effect: sets $(var)#Index & #Size]` | `Variable@LGetIndexSize :: var=Arr` |
| **`LFindIndexSet`** | `var`, `val` | `linked`("") | `[effect: sync cursor]` | `Variable@LFindIndexSet :: var=IdArr, val=ID003 {linked=[NameArr;PosArr]}` |
| **`Shift`** | `vars` | `shift`(1) | `[effect: shift $(vars)]` | `Variable@Shift :: vars=[V1;V2;V3] {shift=1}` |
| **`SAdd`** | `var`, `val` | `section`("") | `[effect: sadd $(var)]` | `Variable@SAdd :: var=SetA, val=K1` |
| **`SUnion`** | `var`, `varA`, `varB` | `section`("") | `[effect: A ∪ B]` | `Variable@SUnion :: var=SetOut, varA=SetA, varB=SetB` |
| **`SDiff`** | `var`, `varA`, `varB` | `section`("") | `[effect: A \ B]` | `Variable@SDiff :: var=SetOut, varA=SetA, varB=SetB` |
| **`SIntersection`** | `var`, `varA`, `varB` | `section`("") | `[effect: A ∩ B]` | `Variable@SIntersection :: var=SetOut, varA=SetA, varB=SetB` |
| **`GetPointElement`** | `var`, `pt`, `axis` | `section`("") | `[effect: $(var)=$(axis)($(pt))]` | `Variable@GetPointElement :: var=PickX, pt=PickPos, axis=X` |
| **`GetPointString`** | `var`, `pt` | `section`("") | `[effect: $(var)=str($(pt))]` | `Variable@GetPointString :: var=PickStr, pt=PickPos` |
| **`Publish`** | `vars` | — | `[effect: publish $(vars)]` | `Variable@Publish :: vars=[SyncEvt]` |
| **`Subscribe`** | `var` | — | `[effect: wait publish $(var)]` | `Variable@Subscribe :: var=SyncEvt` |
| **`Flush`** | `var` | — | `[effect: flush $(var)]` | `Variable@Flush :: var=SyncEvt` |
| **`VariableLoad`** | `file` | — | `[effect: load vars]` | `Variable@VariableLoad :: file=RecipeA.var` |
| **`VariableSave`** | `file` | `patterns`("") | `[effect: save vars]` | `Variable@VariableSave :: file=RecipeA.var {patterns=[Cfg_*;Cal_*]}` |

---

## 5. `POINT&PATH` 模組（22 條指令）

| 指令 (`Point&Path@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddXYZ`** | `pt`, `delta` | — | `[effect: $(pt).XYZ += $(delta)]` | `Point&Path@AddXYZ :: pt=P1, delta=(10,20,0)` |
| **`AddUVW`** | `pt`, `delta` | — | `[effect: $(pt).UVW += $(delta)]` | `Point&Path@AddUVW :: pt=P1, delta=(0,0,90)` |
| **`SubXYZ`** | `pt`, `delta` | — | `[effect: $(pt).XYZ -= $(delta)]` | `Point&Path@SubXYZ :: pt=P1, delta=OffsetPt` |
| **`SubUVW`** | `pt`, `delta` | — | `[effect: $(pt).UVW -= $(delta)]` | `Point&Path@SubUVW :: pt=P1, delta=(0,0,45)` |
| **`ReplaceXYZ`** | `pt`, `val` | — | `[effect: $(pt).XYZ = $(val)]` | `Point&Path@ReplaceXYZ :: pt=P1, val=(100,50,10)` |
| **`ReplaceUVW`** | `pt`, `val` | — | `[effect: $(pt).UVW = $(val)]` | `Point&Path@ReplaceUVW :: pt=P1, val=(0,0,180)` |
| **`ReplaceH`** | `pt`, `h` | — | `[effect: $(pt).H = $(h)]` | `Point&Path@ReplaceH :: pt=P1, h={"speed":80}` |
| **`Calculate`** | `pt`, `expr` | `append`(false) | `[effect: $(pt)=calc($(expr))]` | `Point&Path@Calculate :: pt=P_Out, expr=P1 + P2 {append=true}` |
| **`InsertOnePoint`** | `pt`, `val` | — | `[effect: $(pt) <= $(val)]` | `Point&Path@InsertOnePoint :: pt=Tray, val=(10,20,30)` |
| **`ClearPoint`** | `pt` | — | `[effect: clear $(pt)]` | `Point&Path@ClearPoint :: pt=Tray` |
| **`EnableAxis`** | `pt`, `axis` | — | `[effect: mask $(pt) axis=$(axis)]` | `Point&Path@EnableAxis :: pt=P1, axis=XY` |
| **`Select`** | `pt`, `logic`, `true`, `false` | — | `[effect: $(pt)=?:]` | `Point&Path@Select :: pt=P_Target, logic=Left?, true=P_Left, false=P_Right` |
| **`Pallet1D`** | `pt`, `count` | — | `[effect: gen 1D pallet $(pt)]` | `Point&Path@Pallet1D :: pt=Tray1D, count=10` |
| **`Pallet2D`** | `pt`, `countX`, `countY` | — | `[effect: gen 2D pallet $(pt)]` | `Point&Path@Pallet2D :: pt=Tray2D, countX=4, countY=5` |
| **`ResetPalletIndex`** | `pt` | `index`(0) | `[effect: $(pt).index=$(index)]` | `Point&Path@ResetPalletIndex :: pt=Tray2D {index=0}` |
| **`Circle2DPath`** | `pt`, `startDeg`, `endDeg`, `segments` | — | `[effect: gen circle $(pt)]` | `Point&Path@Circle2DPath :: pt=ArcPath, startDeg=0, endDeg=180, segments=12` |
| **`CoordWS(2Edge)`** | `ws`, `xEnd`, `yEnd` | — | `[effect: build WS $(ws)]` | `Point&Path@CoordWS(2Edge) :: ws=WS1, xEnd=PtX, yEnd=PtY` |
| **`Point2WS`** | `pt`, `ws` | — | `[effect: $(pt)=Point2WS($(ws))]` | `Point&Path@Point2WS :: pt=P1, ws=WS1` |
| **`WS2Point`** | `pt`, `ws` | — | `[effect: $(pt)=WS2Point($(ws))]` | `Point&Path@WS2Point :: pt=P1, ws=WS1` |
| **`SelfMotion`** | `pt`, `basePt`, `newPt` | — | `[effect: compensate $(pt)]` | `Point&Path@SelfMotion :: pt=PickPt, basePt=TeachMark, newPt=CamMark` |
| **`ChkInRangeXY`** | `pt`, `rangePt` | `axis`("XY") | `-> [opt] io` / `[effect: sets $(pt)#InRange?]` | `Point&Path@ChkInRangeXY :: pt=P1, rangePt=SafeBox {axis=XYZ} -> io=IsSafe?` *(1點圓/球、2點矩形/長方體、3點立體或三角、多點多邊形/稜柱)* |
| **`TransImaqPoint`** | `imaqs`, `pt` | — | `[effect: transform $(pt) via $(imaqs)]` | `Point&Path@TransImaqPoint :: imaqs=Cam0, pt=P_Vision` |

---

## 6. `MOTION` 模組（21 條指令）

> **點位立即值規則**：所有接受 `pt` 的運動指令（`GoPoint`、`GoPoint(SingleAxis)`、`GoSafe`、`AddPath`、`WaitPosition`、`SetTool`）除可填點位名稱（如 `PickPos`、`PickPos@Z(50)`）外，亦皆可直接填入無引號立即值座標，如 `pt=(100,200,300,90,180,90)`。

| 指令 (`Motion@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddMotion`** | `mot`, `driver` | `device`(""), `axis`(""), `speed`(""), `acc`(""), `dec`(""), `options`("") | `[effect: reg driver $(mot)]` | `Motion@AddMotion :: mot=Motor1, driver=MockMotion {device=COM3, axis=4, speed=100}` |
| **`Home`** | `mot` | `axis`(""), `mode`("") | `[effect: reset err & Servo ON, home $(mot), sets #Homed?]` *(不在 Home 跳錯，異常由 GetState/WaitMotion 檢出)* | `Motion@Home :: mot=Motor1 {axis=Z, mode=Positive}`<br>`Motion@Home :: mot=Robot1 {mode=ResetCounter}` *(axis Robot 可省略；mode 固定值：`ResetCounter` / `Positive` / `Negative`)* |
| **`Stop`** | — | `mot`*("") | `[effect: clears $(mot)#Busy?]` | `Motion@Stop {mot=Motor1}` |
| **`Go`** | `mot`, `axis`, `speed`*, `coords` | — | `[effect: sets $(mot)#Busy?]` | `Motion@Go :: mot=Motor1, axis=XY, speed=100, coords="50,80"` |
| **`Go(SingleAxis)`** | `mot`, `axis`, `speed`*, `coords` | — | `[effect: sets $(mot)#Busy?]` | `Motion@Go(SingleAxis) :: mot=Motor1, axis=Z, speed=50, coords=10` |
| **`GoPoint`** | `mot`, `speed`*, `pt` | — | `[effect: sets $(mot)#Busy?]` | `Motion@GoPoint :: mot=Motor1, speed=100, pt=PickPos`<br>`Motion@GoPoint :: mot=Motor1, speed=100, pt=(100,200,300,90,180,90)` |
| **`GoPoint(SingleAxis)`** | `mot`, `speed`*, `pt` | — | `[effect: sets $(mot)#Busy?]` | `Motion@GoPoint(SingleAxis) :: mot=Motor1, speed=80, pt=PickPos`<br>`Motion@GoPoint(SingleAxis) :: mot=Motor1, speed=80, pt=(inf,inf,50)` |
| **`GoPallet`** | `mot`, `speed`*, `pt` | — | `[effect: advances $(pt).index & #Busy?]` | `Motion@GoPallet :: mot=Motor1, speed=100, pt=Tray2D` |
| **`GoSafe`** `[err]` | `mot`, `speed`*, `pt` | `axis`*("Z"), `safePos`*(0) | `[effect: retreat $(axis)=$(safePos)]` | `Motion@GoSafe :: mot=Motor1, speed=100, pt=PickPos {axis=Z, safePos=20}`<br>`Motion@GoSafe :: mot=Motor1, speed=100, pt=(100,200,10) {axis=Z, safePos=50}` |
| **`GoSafe(Pallet)`** `[err]` | `mot`, `speed`*, `pt` | `axis`*("Z"), `safePos`*(0) | `[effect: retreat & advance $(pt)]` | `Motion@GoSafe(Pallet) :: mot=Motor1, speed=100, pt=Tray2D {axis=Z, safePos=20}` |
| **`AddPath`** | `mot`, `speed`*, `pt` | — | `[effect: enqueues $(pt)]` | `Motion@AddPath :: mot=Motor1, speed=100, pt=WayPt1`<br>`Motion@AddPath :: mot=Motor1, speed=100, pt=(150,80,20,0,0,0)` |
| **`ClearPath`** | `mot` | — | `[effect: clears $(mot).Path]` | `Motion@ClearPath :: mot=Motor1` |
| **`RunPath`** | `mot` | `options`("") | `[effect: runs $(mot).Path]` | `Motion@RunPath :: mot=Motor1` |
| **`GetPos`** | `mot` | — | `[effect: updates $(mot) & $(mot)#Joint]` | `Motion@GetPos :: mot=Motor1` |
| **`GetState`** `[err]` | `mot` | — | `[effect: updates #Busy?/#Alarm?/ErrorCode]` | `Motion@GetState :: mot=Motor1 [[ErrM]]馬達異常` |
| **`WaitMotion`** `[err]` | `mot` | `timeout`*(30) | `[effect: waits !$(mot)#Busy?]` | `Motion@WaitMotion :: mot=Motor1 {timeout=10} [[ErrM]]到位逾時` |
| **`WaitMotion(Error)`** `[err]` | `mot` | `timeout`*(30) | `[effect: waits !#Busy? & !#Alarm?]` | `Motion@WaitMotion(Error) :: mot=Motor1 {timeout=10}` |
| **`WaitPosition`** | `mot`, `tol`, `pt` | — | `[effect: waits \|$(mot)-$(pt)\|<=$(tol)]` | `Motion@WaitPosition :: mot=Motor1, tol=0.1, pt=PickPos`<br>`Motion@WaitPosition :: mot=Motor1, tol=0.1, pt=(100,200,300,90,180,90)` |
| **`SetTool`** `[err]` | `mot`, `pt` | — | `[effect: sets $(mot)#Tool]` | `Motion@SetTool :: mot=Motor1, pt=Nozzle1`<br>`Motion@SetTool :: mot=Motor1, pt=(0,0,15,0,0,0)` |
| **`Config`** `[err]` | `mot`, `key`, `val` | — | `[effect: config $(mot)[$(key)]]` | `Motion@Config :: mot=Motor1, key=Accel, val=2000`<br>`Motion@Config :: mot=Motor1, key=MoveMode, val=HighSpeed` |
| **`ExtraCMD`** `[err]` | `mot`, `cmd` | — | `[effect: native $(cmd)]` | `Motion@ExtraCMD :: mot=Motor1, cmd="PWR ON"` |

---

## 7. `CAPTURE` 模組（28 條指令）

> **`cam` 與 `imaqs` 區分與操作原則**：**`cam` 就是 ARCOSharp 中 `Capture` 分頁附帶 `ImageViewer` 的 `imaqs`，兩者本質是完全相同的資料結構**（底層統一皆為 `ImaqArrayEntry` 影像陣列）。實務操作與視覺檢測**一律以 `cam` 為主體**（因為唯有 `cam` 掛載 `ImageViewer`，取像、ROI、校正與檢測疊加皆可於畫面即時觀察）；`imaqs` 則作為背景影像陣列緩衝（如飛拍 `FlyBuf`、批次讀圖 `BatchImgs`），需要檢視或進一步檢測時再載入至 `cam` 進行操作。

| 指令 (`Capture@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddCam`** | `cam`, `driver` | `device`(""), `width`(""), `height`(""), `exposure`(""), `file`(""), `options`("") | `[effect: reg camera $(cam)]` | `Capture@AddCam :: cam=Cam0, driver=MockCamera {device=0, width=640, height=480}` |
| **`Config`** `[err]` | `cam`, `key`, `val` | — | `[effect: config $(cam)[$(key)]]` | `Capture@Config :: cam=Cam0, key=Exposure, val=15`<br>`Capture@Config :: cam=Cam0, key=TriggerMode, val=Hardware` |
| **`Snap`** `[err]` | `cam` | `imaqs`("") | `[effect: overwrites imaq $(cam) & sets $(cam)#OK?]` | `Capture@Snap :: cam=Cam0`<br>`Capture@Snap :: cam=Cam0 {imaqs=FlyBuf}` *(覆寫 `Cam0` 並同步 Append 至 `FlyBuf`)* |
| **`Trigger`** `[err]` | `cam` | `imaqs`("") | `[effect: overwrites imaq $(cam) & sets $(cam)#OK?]` | `Capture@Trigger :: cam=Cam0`<br>`Capture@Trigger :: cam=Cam0 {imaqs=FlyBuf}` *(覆寫 `Cam0` 並同步 Append 至 `FlyBuf`)* |
| **`UpdateImage`** | `cam` | — | `[effect: refresh preview $(cam)]` | `Capture@UpdateImage :: cam=Cam0` |
| **`LoadFile`** | `imaqs`, `file` | — | `[effect: loads $(file) into $(imaqs)]` | `Capture@LoadFile :: imaqs=Cam0, file=sample.png` |
| **`ImageSave`** | `imaqs`, `file` | `burnOverlays`(false) | `[effect: saves $(imaqs)]` | `Capture@ImageSave :: imaqs=Cam0, file=out.png {burnOverlays=true}` |
| **`ClearImaq`** | `imaqs` | — | `[effect: clears $(imaqs)]` | `Capture@ClearImaq :: imaqs=FlyBuf` |
| **`LoadFile2Imaq`** | `imaqs`, `file` | — | `[effect: appends regex-matched files to $(imaqs)]` | `Capture@LoadFile2Imaq :: imaqs=BatchImgs, file="PICTURE/CAM0/img_\d+\.png"` |
| **`LoadNextImaq`** | `cam`, `imaqs` | — | `[effect: advances $(imaqs) -> $(cam)]` | `Capture@LoadNextImaq :: cam=Cam0, imaqs=BatchImgs` |
| **`ResetImaqIndex`** | `imaqs` | `index`(0) | `[effect: $(imaqs).index=$(index)]` | `Capture@ResetImaqIndex :: imaqs=BatchImgs {index=0}` |
| **`Display2User`** | `cam`, `ui` | — | `[effect: renders $(cam) -> $(ui)]` | `Capture@Display2User :: cam=Cam0, ui=ImgView1` |
| **`ROISet`** | `cam`, `roi` | — | `[effect: sets ROI on $(cam)]` | `Capture@ROISet :: cam=Cam0, roi="10,20,300,200"` |
| **`ROISave`** | `cam`, `file` | — | `[effect: saves ROI]` | `Capture@ROISave :: cam=Cam0, file=roi.png` |
| **`ROITranslate`** | `cam`, `basePt`, `newPt` | — | `[effect: translates ROI]` | `Capture@ROITranslate :: cam=Cam0, basePt=Mark0, newPt=MarkNow` |
| **`CalibrateLoad`** | `cam`, `file` | — | `[effect: loads calibration]` | `Capture@CalibrateLoad :: cam=Cam0, file=cal.png` |
| **`CalibrateManual`** | `cam` | `scale`(0.05), `originX`(0), `originY`(0), `angle`(0) | `[effect: manual calibration]` | `Capture@CalibrateManual :: cam=Cam0 {scale=0.02, angle=1.5}` |
| **`CoordSet`** | `cam`, `pt` | — | `[effect: sets vision coord]` | `Capture@CoordSet :: cam=Cam0, pt=CalPts` |
| **`CoordSet(2Edge)`** | `cam`, `xPt`, `yPt` | — | `[effect: sets 2-edge coord]` | `Capture@CoordSet(2Edge) :: cam=Cam0, xPt=PtX, yPt=PtY` |
| **`CoordSet(Org+2Edge)`** | `cam`, `orgPt`, `xPt`, `yPt` | — | `[effect: sets org+2-edge coord]` | `Capture@CoordSet(Org+2Edge) :: cam=Cam0, orgPt=Org, xPt=PtX, yPt=PtY` |
| **`FindCircle(InOut)`** | `cam` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindCircle(InOut) :: cam=Cam0 -> pt=CirclePt` |
| **`FindCircle(OutIn)`** | `cam` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindCircle(OutIn) :: cam=Cam0 -> pt=CirclePt` |
| **`FindEdge`** | `cam` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindEdge :: cam=Cam0 -> pt=EdgePt` |
| **`FindGap`** | `cam` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindGap :: cam=Cam0 -> pt=GapPt` |
| **`FindPoint`** | `cam` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindPoint :: cam=Cam0 -> pt=FeatPt` |
| **`GeoMatchOne`** | `cam`, `file` | `angle`("") | `-> var, pt` / `[effect: sets $(pt)#Find?]` | `Capture@GeoMatchOne :: cam=Cam0, file=Mark.png {angle=30} -> var=Score, pt=MarkPt` |
| **`PatternMatchOne`** | `cam`, `file` | `angle`("") | `-> var, pt` / `[effect: sets $(pt)#Find?]` | `Capture@PatternMatchOne :: cam=Cam0, file=Mark.png -> var=Score, pt=MarkPt` |
| **`PatternMatchOne(Fast)`** | `cam`, `file` | `angle`("") | `-> var, pt` / `[effect: sets $(pt)#Find?]` | `Capture@PatternMatchOne(Fast) :: cam=Cam0, file=Mark.png -> var=Score, pt=MarkPt` |

---

## 8. `DAQ` 模組（18 條指令）

> **DAQ 與 Waveform 核心設計理念（現場工程鐵律）**：
> 1. **Driver 內部循環緩衝解耦（Ring Buffer Architecture）**：`AddDAQ` 依 `{rate, sec}` 在 Driver 內部預先配置容量為 `rate * sec` 的循環緩衝區（Circular Ring Buffer）；`Start` 啟動後，由 Driver 背景連續從硬體裝置讀取並覆寫寫入循環緩衝區（與主流程完全解耦，避免漏點、FIFO 溢位或阻塞運動時序）；`Stop` 停止背景寫入並保留既有緩衝供事後讀取。
> 2. **單點即時值 vs. 時間序列波形（Scalar vs. Waveform）**：在背景連續採樣期間，下 `AnalogIn -> var` 僅從循環緩衝區取出**最新單點**寫入 `VariableEngine`，供後續條件邏輯直接比對；下 `AnalogInWaveForm {sec} -> wav` 則從循環緩衝區切出**最近指定 `sec` 秒**（省略則取整個緩衝區）的連續資料，連同 `T0` 與時基 `Dt = 1/rate` 存入 `DaqEngine` 波形容器。
> 3. **純物理時間域、嚴禁以取樣點數入腳本（Time-Domain First）**：所有對外腳本參數一律以**物理時間（秒）**為單位——涵蓋緩衝區長度（`AddDAQ {sec}`）、擷取視窗（`AnalogInWaveForm {sec}`）、波形生成（`WaveFormGenerate {rate, sec}`）以及區間統計/極值檢核（`CompareAvgInRange` / `CompareMaxMinInRange {start, end}`）。點數永遠是內部依 `sec * rate` 換算的衍生量，使製程 Recipe 與硬體取樣率徹底解耦——日後現場為抗噪或更換 DAQ 卡而調整 `rate`（如 1 kHz 升至 10 kHz）時，所有腳本條件完全不需修改，杜絕因索引錯位引發的靜默誤判。
> 4. **波形對波形自動超取樣至最高取樣率（Highest-Rate Oversampling Alignment）**：凡兩條以上波形進行比對或運算（`CompareCoverBy` 包絡線檢測、`Calculate` 四則運算、`MergeWaveForm` 波形串接），絕不可直接以原始陣列索引逐點比對。引擎必先取參與波形中之**最高 Sample Rate（最小 `Dt`）**作為統一時基，將較低取樣率波形沿時間軸以線性插值超取樣對齊後，再逐點比對或計算，完整保留高頻尖峰與窄特徵且不產生 ringing 誤判。

| 指令 (`DAQ@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddDAQ`** | `daq`, `driver` | `device`(""), `rate`(""), `sec`(""), `wave`(""), `options`("") | `[effect: reg DAQ $(daq) ring buffer (rate*sec)]` | `DAQ@AddDAQ :: daq=Daq0, driver=MockDAQ {device=Dev1, rate=1000, sec=5, wave=Sine}` |
| **`Start`** `[err]` | `daq` | — | `[effect: starts ring-buffer acquisition on $(daq)]` | `DAQ@Start :: daq=Daq0` |
| **`Stop`** `[err]` | `daq` | — | `[effect: stops ring-buffer acquisition on $(daq)]` | `DAQ@Stop :: daq=Daq0` |
| **`Wait`** `[err]` | `daq` | `timeout`*(5) | `[effect: waits $(daq)#OK?]` | `DAQ@Wait :: daq=Daq0 {timeout=3} [[ErrD]]採樣逾時` |
| **`AnalogIn`** `[err]` | `daq` | — | `-> var` / `[effect: reads latest sample -> $(var) & sets $(daq)#OK?]` | `DAQ@AnalogIn :: daq=Daq0 -> var=ForceVal` |
| **`AnalogInWaveForm`** `[err]` | `daq` | `sec`("") | `-> [opt] wav` / `[effect: slices recent $(sec)s waveform & sets $(daq)#OK?]` | `DAQ@AnalogInWaveForm :: daq=Daq0 {sec=0.5} -> wav=PressCurve` |
| **`AnalogOut`** `[err]` | `daq`, `val` | — | `[effect: writes AO on $(daq)]` | `DAQ@AnalogOut :: daq=Daq0, val=3.3` |
| **`AnalogOutWaveForm`** `[err]` | `daq`, `wav` | — | `[effect: streams $(wav) to $(daq)]` | `DAQ@AnalogOutWaveForm :: daq=Daq0, wav=DriveWave` |
| **`WaveFormGenerate`** | `wav` | `type`("Sine"), `rate`(1000), `sec`(1), `amp`(5), `offset`(0) | `[effect: generates $(wav) (rate*sec)]` | `DAQ@WaveFormGenerate :: wav=RefW {type=Triangle, rate=1000, sec=0.5, amp=10}` |
| **`WaveFormLoad`** | `wav`, `file` | — | `[effect: loads $(file) into $(wav)]` | `DAQ@WaveFormLoad :: wav=RefW, file=ref.csv` |
| **`WaveFormSave`** | `wav`, `file` | — | `[effect: saves $(wav) to $(file)]` | `DAQ@WaveFormSave :: wav=PressCurve, file=log.csv` |
| **`Calculate`** | `wav`, `expr` | — | `[effect: $(wav)=eval($(expr))]` | `DAQ@Calculate :: wav=DiffW, expr=W1 - W2` |
| **`Filter`** | `wav`, `type` | `window`(3) | `[effect: filters $(wav)]` | `DAQ@Filter :: wav=PressCurve, type=MovingAverage {window=5}` |
| **`MergeWaveForm`** | `wav`, `wavs` | — | `[effect: merges $(wavs) -> $(wav)]` | `DAQ@MergeWaveForm :: wav=MultiW, wavs=[Ch0;Ch1]` |
| **`CompareAvgInRange`** | `wav`, `max`, `min` | `start`(0), `end`(-1) *(單位：秒)* | `[effect: sets $(wav)#OK?]` | `DAQ@CompareAvgInRange :: wav=PressCurve, max=10, min=2 {start=0.1, end=0.5}` |
| **`CompareMaxMinInRange`** | `wav`, `max`, `min` | `start`(0), `end`(-1) *(單位：秒)* | `[effect: sets $(wav)#OK?]` | `DAQ@CompareMaxMinInRange :: wav=PressCurve, max=12, min=0 {start=0.1, end=0.5}` |
| **`CompareCoverBy`** | `wav`, `upper` | `lower`("") | `[effect: sets $(wav)#OK?]` | `DAQ@CompareCoverBy :: wav=PressCurve, upper=UpperW {lower=LowerW}` |
| **`Display2User`** | `wav`, `ui` | — | `[effect: renders $(wav) -> $(ui)]` | `DAQ@Display2User :: wav=PressCurve, ui=WaveChart1` |

---

## 9. `USER` 模組（21 條指令）

| 指令 (`User@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`SwitchUserPanel`** | `panel` | — | `[effect: ActivePanel=$(panel)]` | `User@SwitchUserPanel :: panel=StationA` |
| **`Param2UserPanel`** | — | — | `[effect: refreshes UserPanel]` | `User@Param2UserPanel` |
| **`ShowControl`** | `ui` | — | `[effect: shows $(ui)]` | `User@ShowControl :: ui=[BtnStart;LblStatus]` |
| **`HideControl`** | `ui` | — | `[effect: hides $(ui)]` | `User@HideControl :: ui=BtnDebug` |
| **`EnableControl`** | `ui` | — | `[effect: enables $(ui)]` | `User@EnableControl :: ui=[BtnRun;BtnStop]` |
| **`DisableControl`** | `ui` | — | `[effect: disables $(ui)]` | `User@DisableControl :: ui=BtnRun` |
| **`GetControlValue`** | `ui` | — | `-> [opt] var` / `[effect: default $UserPanel_$(ui)]` | `User@GetControlValue :: ui=TxtSpeed -> var=CurSpeed` |
| **`SetControlValue`** | `ui`, `val` | — | `[effect: sets UI $(ui)]` | `User@SetControlValue :: ui=TxtSpeed, val=150` |
| **`QueryControls`** | — | `panel`("Active"), `role`("All"), `kind`("All") | `-> [opt] var` / `[effect: default $UserPanelControls]` | `User@QueryControls {kind=Button} -> var=BtnList` |
| **`ButtonValue`** | `ui`, `logic` | — | `[effect: sets button $(ui)=eval($(logic))]` | `User@ButtonValue :: ui=BtnAuto, logic=False` |
| **`ButtonTrigger`** | `ui`, `logic` | — | `[effect: fires button $(ui) & UserPanel#$(ui)?]` | `User@ButtonTrigger :: ui=BtnStart, logic=True` |
| **`ButtonText`** | `ui`, `text` | — | `[effect: sets button text]` | `User@ButtonText :: ui=BtnStart, text="運行中"` |
| **`ButtonBlinking`** | `ui`, `logic` | — | `[effect: sets blinking]` | `User@ButtonBlinking :: ui=BtnAlarm, logic=Alarm?` |
| **`TextBoxValue`** | `ui`, `text` | — | `[effect: sets TextBox]` | `User@TextBoxValue :: ui=TxtMsg, text="Ready"` |
| **`TextBoxTrigger`** | `ui`, `text` | — | `[effect: fires TextBox & syncs VAR]` | `User@TextBoxTrigger :: ui=TxtLot, text="LOT-001"` |
| **`TextBoxBlinking`** | `ui`, `logic` | — | `[effect: sets blinking]` | `User@TextBoxBlinking :: ui=TxtMsg, logic=False` |
| **`TextRingValue`** | `ui`, `val` | — | `[effect: sets TextRing]` | `User@TextRingValue :: ui=CmbMode, val=Auto` |
| **`TextRingTrigger`** | `ui`, `val` | — | `[effect: fires TextRing & syncs VAR]` | `User@TextRingTrigger :: ui=CmbMode, val=Manual` |
| **`TextRingBlinking`** | `ui`, `logic` | — | `[effect: sets blinking]` | `User@TextRingBlinking :: ui=CmbMode, logic=True` |
| **`TabValue`** | `ui`, `page` | — | `[effect: selects Tab]` | `User@TabValue :: ui=MainTabs, page=1` |
| **`TabTrigger`** | `ui`, `page` | — | `[effect: fires Tab change]` | `User@TabTrigger :: ui=MainTabs, page=Settings` |

---

## 10. `PYTHON` 模組（10 條指令）

> **多 Session 預設規則**：所有 `Python@*` 指令省略 `{session=...}` 時一律操作當前預設 Session（隨 Section/Thread 隔離），填入 `{session=VisionS1}` 則操作指定 Session；`Python@Reset` 額外支援 `{session=ALL}`（或 `{session=*}`）一次清空所有 Session。

| 指令 (`Python@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`Init`** | — | `session`("") | `[effect: init Python]` | `Python@Init` |
| **`Reset`** | — | `session`("") | `[effect: resets $(session) (empty=default, ALL/*=all)]` | `Python@Reset`<br>`Python@Reset {session=VisionS1}`<br>`Python@Reset {session=ALL}` |
| **`File`** | `file` | `session`("") | `[effect: runs $(file)]` | `Python@File :: file=calc.py {session=VisionS1}` |
| **`Script`** `[err]` | `code` | `session`("") | `[effect: runs inline code]` | `Python@Script :: code="res = x * 2" {session=S1}` |
| **`SetVariable`** | `var` | `val`(""), `py`(""), `session`("") | `[effect: ARCOS var -> Python]` | `Python@SetVariable :: var=Count {py=py_cnt, session=S1}` |
| **`GetVariable`** | `py` | `session`("") | `-> [opt] var` / `[effect: Python -> ARCOS var]` | `Python@GetVariable :: py=res {session=S1} -> var=Result` |
| **`SetPoint`** | `pt` | `py`(""), `session`("") | `[effect: ARCOS pt -> Python]` | `Python@SetPoint :: pt=PickPos {py=p_pick}` |
| **`GetPoint`** | `py` | `session`("") | `-> [opt] pt` / `[effect: Python -> ARCOS pt]` | `Python@GetPoint :: py=p_out -> pt=PlacePos` |
| **`SetCapture`** | `cam` | `py`(""), `session`("") | `[effect: ARCOS cam -> Python]` | `Python@SetCapture :: cam=Cam0 {py=img0}` |
| **`GetCapture`** | `py` | `session`("") | `-> cam` / `[effect: Python -> ARCOS cam]` | `Python@GetCapture :: py=out_img -> cam=CamOut` |

---

## 11. `REDIS` 模組（9 條指令）

> **本機自動啟動與多 Session 規則**：隨附之本機 `redis-server.exe` 與 `redis.conf` 置於 `MODULES/REDIS/server/`。載入 `REDIS` 模組（`System@IncludeModule :: module=Redis` 或首次下達 `Redis@*` 指令）時，系統會自動檢查本機 `127.0.0.1:6379` 是否已啟動；若未啟動則自動於背景啟動，並將此本機實例作為**預設 Session**（所有 `Redis@*` 指令省略 `{session=...}` 時直接操作本機 Redis，無需先呼叫 `CreateSession`）。需要連線其他外部 Redis 伺服器或不同 DB（如 `192.168.1.10:6379`、`127.0.0.1:6379/1`）時，再使用 `Redis@CreateSession` 建立具名 Session 並以 `{session=MES}` 切換。

| 指令 (`Redis@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`CreateSession`** | `session` | `url`("127.0.0.1:6379") | `[effect: sets RedisErr#$(session)=OK]` | `Redis@CreateSession :: session=Line1 {url=192.168.1.10:6379}` |
| **`Set`** | `key`, `val` | `session`("") | `[effect: sets RedisKV[$(key)]]` | `Redis@Set :: key=LotId, val=LOT2026 {session=MES}` |
| **`Get`** | `key` | `session`("") | `-> [opt] var` / `[effect: default $(key)]` | `Redis@Get :: key=LotId {session=MES} -> var=CurLot` |
| **`LPush`** | `key`, `val` | `session`("") | `[effect: pushes to RedisList[$(key)]]` | `Redis@LPush :: key=JobQ, val=Part001 {session=Line1}` |
| **`LPop`** | `key` | `session`("") | `-> [opt] var` / `[effect: default $(key)]` | `Redis@LPop :: key=JobQ {session=Line1} -> var=NextJob` |
| **`BLPop`** `[err]` | `key` | `session`(""), `timeout`*(5) | `-> [opt] var` / `[effect: blocks & pops]` | `Redis@BLPop :: key=JobQ {session=Line1, timeout=3} -> var=NextJob [[ErrQ]]佇列逾時` |
| **`Publish`** | `key`, `val` | `session`("") | `[effect: publishes to $(key)]` | `Redis@Publish :: key=LineEvt, val=CycleDone {session=Line1}` |
| **`Subscribe`** | `key` | `session`("") | `[effect: subscribes $(key)]` | `Redis@Subscribe :: key=LineEvt {session=Line1}` |
| **`CMD`** | `cmd` | `session`("") | `[effect: runs raw Redis $(cmd)]` | `Redis@CMD :: cmd=PING {session=Line1}` |

---

## 12. `MULTISTATION` 模組（12 條指令）

> **工站.工位雙模定址規則**：透過 `MultiStation@AddStation` 建立工站群組，支援**命名工位清單**（`{items=[入料;加工;檢驗;出料]}`）或 **1-based 料盤工位**（`{slots=20}`）。定址採 **`工站.工位`**（如 `FeedInStation.入料`、`TrayA.3`）：有料/無料/OK/NG/Busy 自動映射至 **IO 引擎**（`#On?`/`#Run?`/`#OK?`/`#NG?`/`#Busy?`），工位隨行資料自動映射至 **Variable 引擎**（`工站.工位#欄位`）並隨 `StateShift`（環形緩衝區向前 `+shift` / 向後 `-shift`；直線輸送帶僅需對頭/尾呼叫 `SetOn :: ..., logic=False` 清空即可）與 `Transfer` 同步搬移；`SetOn`（`logic=False` 時預設一併清空 OK/NG 與隨行資料）與 `SetOK`（`True`=OK、`False`=NG）皆直接支援 `$logic` 布林表達式（並於底層相容舊名 `SetStationON`/`OFF`/`OK`/`NG`）。`MultiStation` 與 `Point&Path` 職責完全解耦：`FindFirstOK` 與 `FindEmpty` 輸出目標工位鍵與 **0-based `index`**，再交由 `Point&Path@ResetPalletIndex :: pt=InTrayPts {index=$(SrcIdx)}` 設定對應料盤點位（並更新 `$(station)#Found?`）。

| 指令 (`MultiStation@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddStation`** | `station` | `items`(""), `slots`(0) | `[effect: reg station & IO/Var bindings]` | `MultiStation@AddStation :: station=FeedInStation {items=[入料;加工;檢驗;出料]}`<br>`MultiStation@AddStation :: station=TrayA {slots=20}` |
| **`SetOn`** | `station`, `logic` | `clearData`(true) | `[effect: $(station)#On?=eval($(logic)); clears OK/NG+Data on false]` | `MultiStation@SetOn :: station=FeedInStation.入料, logic=InSensor?`<br>`MultiStation@SetOn :: station=FeedInStation.出料, logic=False` |
| **`SetOK`** | `station`, `logic` | — | `[effect: $(station)#OK?=eval($(logic)), #NG?=!eval($(logic))]` | `MultiStation@SetOK :: station=FeedInStation.檢驗, logic=Cam0#OK? & PressCurve#OK?`<br>`MultiStation@SetOK :: station=TrayA.3, logic=False` |
| **`SetData`** | `station`, `val` | `field`("Data") | `[effect: var $(station)#$(field)=$(val)]` | `MultiStation@SetData :: station=FeedInStation.入料, val=SN001 {field=SN}` |
| **`GetData`** | `station` | `field`("Data") | `-> [opt] var` | `MultiStation@GetData :: station=FeedInStation.加工 {field=SN} -> var=CurSN` |
| **`ChkStationRunState`** | `station` | — | `[effect: updates $(station)#Busy?]` | `MultiStation@ChkStationRunState :: station=FeedInStation.加工` |
| **`StateShift`** | — | `station`(""), `shift`("1") | `[effect: shifts ring-buffer states & data]` | `MultiStation@StateShift {station=FeedInStation, shift=1}`<br>`MultiStation@StateShift {station=FeedInStation, shift=-1}` |
| **`Transfer`** | `from`, `to` | `clearSource`(true) | `[effect: moves state & data $(from)->$(to)]` | `MultiStation@Transfer :: from=FeedInStation.出料, to=OutTray.3` |
| **`FindFirstOK`** | `station` | `allowUnchecked`(false) | `-> [opt] var, index` / `[effect: sets $(station)#Found?]` | `MultiStation@FindFirstOK :: station=InTray -> var=SrcSlot, index=SrcIdx` |
| **`FindEmpty`** | `station` | — | `-> [opt] var, index` / `[effect: sets $(station)#Found?]` | `MultiStation@FindEmpty :: station=OutTray -> var=DstSlot, index=DstIdx` |
| **`ResetDefault`** | — | `station`("") | `[effect: resets station flags & data]` | `MultiStation@ResetDefault {station=FeedInStation}` |
| **`UpdateIndicator`** | — | `station`("") | `[effect: syncs indicator IOs]` | `MultiStation@UpdateIndicator {station=FeedInStation}` |

---

## 13. `AIAGENT` 模組（3 條指令）

| 指令 (`AIAgent@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`SuperviseStart`** | `prompt` | `bypass`*(false) | `[effect: marks AIAgent_Supervising=true]` | `AIAgent@SuperviseStart :: prompt=InspectPart {bypass=true}` |
| **`SuperviseEnd`** `[err]` | — | — | `[effect: invokes AI supervise]` | `AIAgent@SuperviseEnd [[ErrAI]]` |
| **`ErrorHandle`** `[err]` | `prompt` | `bypass`*(false) | `[effect: invokes AI error_handle]` | `AIAgent@ErrorHandle :: prompt=CheckJam {bypass=true} [[Recover]]` |

---

## 14. `HELLO` 模組（2 條指令）

| 指令 (`Hello@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`Init`** | — | — | `[effect: sets Hello_Status@Hello=INITIALIZED]` | `Hello@Init` |
| **`HelloWorld`** | — | `msg`("hello world") | `[effect: logs $(msg)]` | `Hello@HelloWorld {msg="Hi ARCOSharp"}` |

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

## 🛠️ AI Agent 腳本輸出前自我查核清單 (v2.0 Pre-flight Checklist)

1. [ ] 指令是否符合四段式 v2.0 語法（`::` 必填、`{}` 選填、`->` 輸出、`[[Label]]` 錯誤攔截）？
2. [ ] 硬體鍵名是否正確使用 `card` / `mot` / `cam` / `daq`（未誤用 `dev`）？
3. [ ] `logic=` 與 `expr=` 是否已省略不必要的外層雙引號，且 `logic=` 內變數與 IO 皆未帶 `$` 前綴？
4. [ ] Input IO 結尾是否皆帶 `?`、Output IO 皆未帶 `?`？
5. [ ] `Redis@*` 是否正確使用 `key` / `val` 並在多連線時以 `{session=...}` 區隔？
6. [ ] `MultiStation@*` 是否正確採 `工站.工位` 定址，且直線移載已在 `StateShift` 後顯式對頭/尾執行 `SetOn :: ..., logic=False`？
7. [ ] 所有逾時等待與高風險運動指令是否已掛載 `[[ErrLabel]]` 錯誤處理跳轉？
