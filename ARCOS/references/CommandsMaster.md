# ARCOS 全指令語法速查總表 (CommandsMaster.md — v2.0)

本文件是專為 **AI Agent** 設計的 ARCOS 全指令速查總表 (Machine-Readable Reference)，已與 [docs/COMMAND_SYNTAX_AND_SCHEMA_SPEC.md](../../docs/COMMAND_SYNTAX_AND_SCHEMA_SPEC.md) 100% 同步，涵蓋全系統 **14 大模組共 251 條指令**。

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
7. **`Redis` 多 Session 規則**：首次 `Redis@CreateSession` 自動成為預設 Session；其餘指令皆支援選填 `{session=...}`，統一使用 `key` 與 `val`。
8. **`MultiStation` 統一工位模型**：透過 `MultiStation@AddStation` 建立命名或 1-based 料盤工站，`工站.工位` 自動綁定 IO / Variable / Point；`StateShift` 統一採環形緩衝區（`+shift` / `-shift`），直線輸送帶另以 `SetStationOFF` 清頭/尾即可。
9. **AIAgent 旁路開關**：v2.0 顯式使用 `{bypass=true}`（對應舊版 `~Prompt` 前綴）。
10. **外部 CSVI / VI 呼叫**：外部獨立 CSVI 透過 `Call@<Name>.csvi& :: ...`（Runtime 模式）呼叫；`Thread@Call` 僅限內部 `.pro` 程式區段。

---

## 📑 模組索引 (14 模組 251 條指令 + Telnet)

1. `IO`（14）｜2. `SYSTEM`（21）｜3. `THREAD`（31）｜4. `VARIABLE`（34）｜5. `POINT&PATH`（22）
6. `MOTION`（21）｜7. `CAPTURE`（28）｜8. `DAQ`（18）｜9. `USER`（21）｜10. `PYTHON`（12）
11. `REDIS`（9）｜12. `MULTISTATION`（15）｜13. `AIAGENT`（3）｜14. `HELLO`（2）｜15. `Telnet` 通訊指令集

---

## 1. `IO` 模組（14 條指令）

| 指令 (`IO@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`AddIODriver`** | `card`, `driver` | `port`(""), `outStart`("P0.0"), `outPorts`(8), `inStart`("P1.0"), `inPorts`(8) | `[effect: reg driver $(card)]` | `IO@AddIODriver :: card=IO1, driver=MockIO {port=COM1, outStart=P0.0, outPorts=8, inStart=P1.0, inPorts=8}` | `IO1, MockIO, DEVICE=..., ...` |
| **`InitIOPort`** | `card` | — | `[effect: open $(card)]` | `IO@InitIOPort :: card=IO1` | `$io` |
| **`ForceUpdate`** | `card` | — | `[effect: sync HW $(card)]` | `IO@ForceUpdate :: card=IO1` | `$io` |
| **`SetPortName`** | `io`, `alias` | — | `[effect: alias $(io)<->$(alias)]` | `IO@SetPortName :: io=P0.0, alias=OUT1` | `$physicalPort, $portName` |
| **`AddVirtualIO`** | `io` | `logic`(False) | `[effect: create $(io) = eval($(logic))]` | `IO@AddVirtualIO :: io=CycleOK? {logic=Start? & !Alarm?}` | `$name, $logic` |
| **`SetPort`** | `io` | `holdSec`(0), `forceUpdate`*(false) | `[effect: $(io)=ON]` | `IO@SetPort :: io=OUT1 {holdSec=0.5, forceUpdate=true}` | `$port[, $second[, ForceUpdate]]` |
| **`ClrPort`** | `io` | `holdSec`(0), `forceUpdate`*(false) | `[effect: $(io)=OFF]` | `IO@ClrPort :: io=OUT1 {holdSec=0.2}` | `$port[, $second[, ForceUpdate]]` |
| **`Flash(OnOff)`** | `io`, `onSec`, `offSec` | — | `[effect: pulse $(io) 1->0]` | `IO@Flash(OnOff) :: io=OUT1, onSec=0.2, offSec=0.5` | `$port, $onSec, $offSec` |
| **`Flash(OffOn)`** | `io`, `offSec`, `onSec` | — | `[effect: pulse $(io) 0->1]` | `IO@Flash(OffOn) :: io=OUT1, offSec=0.5, onSec=0.2` | `$port, $offSec, $onSec` |
| **`Shift`** | `ios` | `shift`(1) | `[effect: shift $(ios)]` | `IO@Shift :: ios=[OUT1;OUT2;OUT3] {shift=1}` | `OUT1,OUT2,OUT3,Shift=1` |
| **`WaitPortOn`** `[err]` | `io` | `timeoutSec`*(0=無限) | `[effect: wait $(io)==ON]` | `IO@WaitPortOn :: io=Start? {timeoutSec=5} [[Err1]]啟動逾時` | `$port, $timeout, [[$label]]$msg` |
| **`WaitPortOff`** `[err]` | `io` | `timeoutSec`*(0=無限) | `[effect: wait $(io)==OFF]` | `IO@WaitPortOff :: io=Busy? {timeoutSec=3} [[Err1]]未復位` | `$port, $timeout, [[$label]]$msg` |
| **`CheckLogicOn`** `[err]` | `logic` | `timeoutSec`*(0=無限) | `[effect: wait $(logic)==true]` | `IO@CheckLogicOn :: logic=Start? & !Door? {timeoutSec=5} [[Err]]條件不滿足` | `$logic, $timeout, [[$label]]$msg` |
| **`CheckLogicOff`** `[err]` | `logic` | `timeoutSec`*(0=無限) | `[effect: wait $(logic)==false]` | `IO@CheckLogicOff :: logic=Alarm1 \| Alarm2 {timeoutSec=3}` | `$logic, $timeout, [[$label]]$msg` |

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
| **`TimerReset`** | `timer` | `timeoutSec`(0) | `[effect: reset $(timer)#Timeout?]` | `System@TimerReset :: timer=T_Heat {timeoutSec=30}` | `$timerName[, $timeout]` |
| **`TimerCheck`** | `timer` | — | `[effect: update $(timer)#Timeout?]` | `System@TimerCheck :: timer=T_Heat` | `$timerName` |
| **`CounterReset`** | `counter` | `target`(0) | `[effect: reset $(counter)#Over?]` | `System@CounterReset :: counter=C_Retry {target=3}` | `$cntName[, $targetCount]` |
| **`CounterCheck`** | `counter` | — | `[effect: dec & update $(counter)#Over?]` | `System@CounterCheck :: counter=C_Retry` | `$cntName` |
| **`Record`** | `rec` | `msg`("") | `[effect: timestamp $(rec)]` | `System@Record :: rec=CT_Pick {msg=Start}` | `$recName[, $msg]` |
| **`LogRecordCycleTime`** | `rec` | `prefix`("") | `-> [opt] var` / `[effect: log CT $(rec)]` | `System@LogRecordCycleTime :: rec=CT_Pick {prefix="Pick CT:"} -> var=CycleSec` | `$recName[, $prefix]` |
| **`ProgramLoad`** | `path` | — | `[effect: reload prog slot(s)]` | `System@ProgramLoad :: path=Master.prog` | `$filePath` |
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
| **`Return`** | — | — | `[effect: pop call stack]` | `Thread@Return` | (無參) |
| **`Run`** | `prog`, `thread` | `startLine`(1), `logic`("True") | `[effect: start Thread$(thread)#Run?]` | `Thread@Run :: prog=Prog1, thread=1 {startLine=1, logic=Auto?}` | `$prog, $threadNo[, $startLine[, $logic]]` |
| **`Wait`** | `threads` | — | `[effect: wait !$(threads)#Run?]` | `Thread@Wait :: threads=[Thread1;Thread2]` | `$t1[, $t2...]` |
| **`Suspend`** | `thread` | `logic`("True") | `[effect: stop Thread$(thread)]` | `Thread@Suspend :: thread=1 {logic=Err?}` | `$threadNo[, $logicExpr]` |
| **`Resume`** | `thread` | `logic`("True") | `[effect: resume Thread$(thread)]` | `Thread@Resume :: thread=1` | `$threadNo[, $logicExpr]` |
| **`Stop`** | — | `logic`("True"), `msg`("") | `[effect: halt current thread]` | `Thread@Stop {logic=Abort?, msg=使用者中止}` | `[$logicExpr[, $msg]]` |
| **`SubThreadStart`** | `sub` | `logic`("True") | `[effect: spawn $(sub)#Run?]` | `Thread@SubThreadStart :: sub=CamTask {logic=EnCam?}` | `$subName[, $logicExpr]` |
| **`SubThreadEnd`** | `sub` | — | `[effect: end inline SubThread $(sub)]` | `Thread@SubThreadEnd :: sub=CamTask` | `$subName` |
| **`SubThreadScript`** | `sub`, `file` | — | `[effect: spawn $(sub) from $(file)]` | `Thread@SubThreadScript :: sub=LoadTask, file=Load.init` | `$subName, $initFile` |
| **`SubThreadSuspend`** | `sub` | — | `[effect: stop SubThread $(sub)]` | `Thread@SubThreadSuspend :: sub=CamTask` | `$subName` |
| **`SubThreadWait`** | `subs` | `skipNotFound`(false) | `[effect: wait SubThread $(subs)]` | `Thread@SubThreadWait :: subs=[CamTask;IoTask] {skipNotFound=true}` | `$sub1[, $sub2...][, SKIPNOTFIND=1]` |

#### C. 互斥鎖、單步控制與異常處理

| 指令 (`Thread@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`KeySectionLock`** | `lock` | `timeoutSec`*(5.0), `msg`("") | `[effect: acquire mutex $(lock)]` | `Thread@KeySectionLock :: lock=GantryZone {timeoutSec=10, msg=機構區佔用逾時}` | `$lockName[, $timeout[, $msg]]` |
| **`KeySectionUnLock`** | `lock` | — | `[effect: release mutex $(lock)]` | `Thread@KeySectionUnLock :: lock=GantryZone` | `$lockName` |
| **`GoOn`** | — | `logic`("True") | `[effect: SystemPause if !$(logic)]` | `Thread@GoOn {logic=MaterialOK?}` | `[$logicExpr]` |
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
| **`Calculate`** | `var`, `expr` | `section`("") | `[effect: $(var)=eval($(expr))]` | `Variable@Calculate :: var=Total, expr=$(A) + $(B)` |
| **`Select`** | `var`, `logic`, `true`, `false` | `section`("") | `[effect: $(var)=?:]` | `Variable@Select :: var=Result, logic=Pass?, true=OK, false=NG` |
| **`Append`** | `var`, `val` | `section`("") | `[effect: $(var)+=$(val)]` | `Variable@Append :: var=LogStr, val="_Done"` |
| **`Trim`** | `var`, `start`, `end` | `section`("") | `[effect: trim $(var)]` | `Variable@Trim :: var=Barcode, start=0, end=8` |
| **`Slice`** | `var`, `sep` | `section`("") | `[effect: slice $(var)]` | `Variable@Slice :: var=CsvRow, sep=","` |
| **`Joint`** | `var`, `sep` | `section`("") | `[effect: join $(var)]` | `Variable@Joint :: var=Items, sep="-"` |
| **`LPush`** | `var`, `val` | `section`("") | `[effect: lpush $(var)]` | `Variable@LPush :: var=Queue, val=Item1` |
| **`RPush`** | `var`, `val` | `section`("") | `[effect: rpush $(var)]` | `Variable@RPush :: var=Queue, val=Item2` |
| **`LPop`** | `var` | `section`("") | `-> [opt] out` / `[effect: lpop $(var)]` | `Variable@LPop :: var=Queue -> out=HeadVal` |
| **`RPop`** | `var` | `section`("") | `-> [opt] out` / `[effect: rpop $(var)]` | `Variable@RPop :: var=Queue -> out=TailVal` |
| **`LInsert`** | `var`, `val`, `condition` | `section`("") | `[effect: linsert $(var)]` | `Variable@LInsert :: var=Arr, val=5, condition=">0"` |
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
| **`VariableLoad`** | `path` | — | `[effect: load vars]` | `Variable@VariableLoad :: path=RecipeA.var` |
| **`VariableSave`** | `path` | `patterns`("") | `[effect: save vars]` | `Variable@VariableSave :: path=RecipeA.var {patterns=[Cfg_*;Cal_*]}` |

---

## 5. `POINT&PATH` 模組（22 條指令）

| 指令 (`Point&Path@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddXYZ`** | `pt`, `delta` | — | `[effect: $(pt).XYZ += $(delta)]` | `Point&Path@AddXYZ :: pt=P1, delta="(10,20,0)"` |
| **`AddUVW`** | `pt`, `delta` | — | `[effect: $(pt).UVW += $(delta)]` | `Point&Path@AddUVW :: pt=P1, delta="(0,0,90)"` |
| **`SubXYZ`** | `pt`, `delta` | — | `[effect: $(pt).XYZ -= $(delta)]` | `Point&Path@SubXYZ :: pt=P1, delta=OffsetPt` |
| **`SubUVW`** | `pt`, `delta` | — | `[effect: $(pt).UVW -= $(delta)]` | `Point&Path@SubUVW :: pt=P1, delta="(0,0,45)"` |
| **`ReplaceXYZ`** | `pt`, `val` | — | `[effect: $(pt).XYZ = $(val)]` | `Point&Path@ReplaceXYZ :: pt=P1, val="(100,50,10)"` |
| **`ReplaceUVW`** | `pt`, `val` | — | `[effect: $(pt).UVW = $(val)]` | `Point&Path@ReplaceUVW :: pt=P1, val="(0,0,180)"` |
| **`ReplaceH`** | `pt`, `h` | — | `[effect: $(pt).H = $(h)]` | `Point&Path@ReplaceH :: pt=P1, h="{\"speed\":80}"` |
| **`Calculate`** | `pt`, `expr` | `append`(false) | `[effect: $(pt)=calc($(expr))]` | `Point&Path@Calculate :: pt=P_Out, expr=P1 + P2 {append=true}` |
| **`InsertOnePoint`** | `pt`, `val` | — | `[effect: $(pt) <= $(val)]` | `Point&Path@InsertOnePoint :: pt=Tray, val="(10,20,30)"` |
| **`ClearPoint`** | `pt` | — | `[effect: clear $(pt)]` | `Point&Path@ClearPoint :: pt=Tray` |
| **`EnableAxis`** | `pt`, `axes` | — | `[effect: mask $(pt) axes=$(axes)]` | `Point&Path@EnableAxis :: pt=P1, axes=XY` |
| **`Select`** | `pt`, `logic`, `true`, `false` | — | `[effect: $(pt)=?:]` | `Point&Path@Select :: pt=P_Target, logic=Left?, true=P_Left, false=P_Right` |
| **`Pallet1D`** | `pt`, `count` | — | `[effect: gen 1D pallet $(pt)]` | `Point&Path@Pallet1D :: pt=Tray1D, count=10` |
| **`Pallet2D`** | `pt`, `countX`, `countY` | — | `[effect: gen 2D pallet $(pt)]` | `Point&Path@Pallet2D :: pt=Tray2D, countX=4, countY=5` |
| **`ResetPalletIndex`** | `pt` | `index`(0) | `[effect: $(pt).index=$(index)]` | `Point&Path@ResetPalletIndex :: pt=Tray2D {index=0}` |
| **`Circle2DPath`** | `pt`, `startDeg`, `endDeg`, `segments` | — | `[effect: gen circle $(pt)]` | `Point&Path@Circle2DPath :: pt=ArcPath, startDeg=0, endDeg=180, segments=12` |
| **`CoordWS(2Edge)`** | `ws`, `xEnd`, `yEnd` | — | `[effect: build WS $(ws)]` | `Point&Path@CoordWS(2Edge) :: ws=WS1, xEnd=PtX, yEnd=PtY` |
| **`Point2WS`** | `pt`, `ws` | — | `[effect: $(pt)=Point2WS($(ws))]` | `Point&Path@Point2WS :: pt=P1, ws=WS1` |
| **`WS2Point`** | `pt`, `ws` | — | `[effect: $(pt)=WS2Point($(ws))]` | `Point&Path@WS2Point :: pt=P1, ws=WS1` |
| **`SelfMotion`** | `pt`, `basePt`, `newPt` | — | `[effect: compensate $(pt)]` | `Point&Path@SelfMotion :: pt=PickPt, basePt=TeachMark, newPt=CamMark` |
| **`ChkInRangeXY`** | `pt`, `rangePt` | — | `-> [opt] var` / `[effect: sets $(pt)#InRange?]` | `Point&Path@ChkInRangeXY :: pt=P1, rangePt=SafeBox -> var=IsSafe` |
| **`TransImaqPoint`** | `imaq`, `pt` | — | `[effect: transform $(pt) via $(imaq)]` | `Point&Path@TransImaqPoint :: imaq=Cam0, pt=P_Vision` |

---

## 6. `MOTION` 模組（21 條指令）

| 指令 (`Motion@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddMotion`** | `mot`, `driver` | `axes`(""), `speed`(""), `acc`(""), `dec`(""), `options`("") | `[effect: reg driver $(mot)]` | `Motion@AddMotion :: mot=Motor1, driver=MockMotion {axes=4, speed=100}` |
| **`Home`** | `mot` | `axes`(""), `mode`("") | `[effect: sets $(mot)#Homed?]` | `Motion@Home :: mot=Motor1` |
| **`Stop`** | — | `mot`*("") | `[effect: clears $(mot)#Busy?]` | `Motion@Stop {mot=Motor1}` |
| **`Go`** | `mot`, `axes`, `speed`*, `coords` | — | `[effect: sets $(mot)#Busy?]` | `Motion@Go :: mot=Motor1, axes=XY, speed=100, coords="50,80"` |
| **`Go(SingleAxis)`** | `mot`, `axes`, `speed`*, `coords` | — | `[effect: sets $(mot)#Busy?]` | `Motion@Go(SingleAxis) :: mot=Motor1, axes=Z, speed=50, coords=10` |
| **`GoPoint`** | `mot`, `speed`*, `pt` | — | `[effect: sets $(mot)#Busy?]` | `Motion@GoPoint :: mot=Motor1, speed=100, pt=PickPos` |
| **`GoPoint(SingleAxis)`** | `mot`, `speed`*, `pt` | — | `[effect: sets $(mot)#Busy?]` | `Motion@GoPoint(SingleAxis) :: mot=Motor1, speed=80, pt=PickPos` |
| **`GoPallet`** | `mot`, `speed`*, `pt` | — | `[effect: advances $(pt).index & #Busy?]` | `Motion@GoPallet :: mot=Motor1, speed=100, pt=Tray2D` |
| **`GoSafe`** `[err]` | `mot`, `speed`*, `pt` | `safeAxis`*("Z"), `safePos`*(0) | `[effect: retreat $(safeAxis)=$(safePos)]` | `Motion@GoSafe :: mot=Motor1, speed=100, pt=PickPos {safePos=20}` |
| **`GoSafe(Pallet)`** `[err]` | `mot`, `speed`*, `pt` | `safeAxis`*("Z"), `safePos`*(0) | `[effect: retreat & advance $(pt)]` | `Motion@GoSafe(Pallet) :: mot=Motor1, speed=100, pt=Tray2D {safePos=20}` |
| **`AddPath`** | `mot`, `speed`*, `pt` | — | `[effect: enqueues $(pt)]` | `Motion@AddPath :: mot=Motor1, speed=100, pt=WayPt1` |
| **`ClearPath`** | `mot` | — | `[effect: clears $(mot).Path]` | `Motion@ClearPath :: mot=Motor1` |
| **`RunPath`** | `mot` | `options`("") | `[effect: runs $(mot).Path]` | `Motion@RunPath :: mot=Motor1` |
| **`GetPos`** | `mot` | — | `[effect: updates $(mot) & $(mot)#Joint]` | `Motion@GetPos :: mot=Motor1` |
| **`GetState`** `[err]` | `mot` | — | `[effect: updates #Busy?/#Alarm?/ErrorCode]` | `Motion@GetState :: mot=Motor1 [[ErrM]]馬達異常` |
| **`WaitMotion`** `[err]` | `mot` | `timeoutSec`*(30) | `[effect: waits !$(mot)#Busy?]` | `Motion@WaitMotion :: mot=Motor1 {timeoutSec=10} [[ErrM]]到位逾時` |
| **`WaitMotion(Error)`** `[err]` | `mot` | `timeoutSec`*(30) | `[effect: waits !#Busy? & !#Alarm?]` | `Motion@WaitMotion(Error) :: mot=Motor1 {timeoutSec=10}` |
| **`WaitPosition`** | `mot`, `tol`, `pt` | — | `[effect: waits \|$(mot)-$(pt)\|<=$(tol)]` | `Motion@WaitPosition :: mot=Motor1, tol=0.1, pt=PickPos` |
| **`SetTool`** `[err]` | `mot`, `pt` | — | `[effect: sets $(mot)#Tool]` | `Motion@SetTool :: mot=Motor1, pt=Nozzle1` |
| **`Config`** `[err]` | `mot`, `key`, `val` | — | `[effect: config $(mot)[$(key)]]` | `Motion@Config :: mot=Motor1, key=Accel, val=2000` |
| **`ExtraCMD`** `[err]` | `mot`, `cmd` | — | `[effect: native $(cmd)]` | `Motion@ExtraCMD :: mot=Motor1, cmd="PWR ON"` |

---

## 7. `CAPTURE` 模組（28 條指令）

| 指令 (`Capture@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddCam`** | `cam`, `driver` | `width`(""), `height`(""), `exposure`(""), `file`(""), `options`("") | `[effect: reg camera $(cam)]` | `Capture@AddCam :: cam=Cam0, driver=MockCamera {width=640, height=480}` |
| **`Config`** `[err]` | `cam`, `key`, `val` | — | `[effect: config $(cam)[$(key)]]` | `Capture@Config :: cam=Cam0, key=Exposure, val=15` |
| **`Snap`** `[err]` | `cam` | — | `-> [opt] imaqs` / `[effect: sets $(cam)#OK?]` | `Capture@Snap :: cam=Cam0 -> imaqs=FlyBuf` |
| **`Trigger`** `[err]` | `cam` | — | `-> [opt] imaqs` / `[effect: sets $(cam)#OK?]` | `Capture@Trigger :: cam=Cam0 -> imaqs=FlyBuf` |
| **`UpdateImage`** | `cam` | — | `[effect: refresh preview $(cam)]` | `Capture@UpdateImage :: cam=Cam0` |
| **`LoadFile`** | `imaq`, `path` | — | `[effect: loads $(path) into $(imaq)]` | `Capture@LoadFile :: imaq=Cam0, path=sample.png` |
| **`ImageSave`** | `imaq`, `path` | `burnOverlays`(false) | `[effect: saves $(imaq)]` | `Capture@ImageSave :: imaq=Cam0, path=out.png {burnOverlays=true}` |
| **`ClearImaq`** | — | `imaq`("") | `[effect: clears $(imaq)]` | `Capture@ClearImaq {imaq=FlyBuf}` |
| **`LoadFile2Imaq`** | `imaqs`, `path` | — | `[effect: appends to $(imaqs)]` | `Capture@LoadFile2Imaq :: imaqs=BatchImgs, path=img1.png` |
| **`LoadNextImaq`** | `imaqs`, `imaq` | — | `[effect: advances $(imaqs) -> $(imaq)]` | `Capture@LoadNextImaq :: imaqs=BatchImgs, imaq=Cam0` |
| **`ResetImaqIndex`** | `imaqs` | `index`(0) | `[effect: $(imaqs).index=$(index)]` | `Capture@ResetImaqIndex :: imaqs=BatchImgs {index=0}` |
| **`Display2User`** | `imaq`, `ctrl` | — | `[effect: renders $(imaq) -> $(ctrl)]` | `Capture@Display2User :: imaq=Cam0, ctrl=ImgView1` |
| **`ROISet`** | `imaq`, `roi` | — | `[effect: sets ROI on $(imaq)]` | `Capture@ROISet :: imaq=Cam0, roi="10,20,300,200"` |
| **`ROISave`** | `imaq`, `path` | — | `[effect: saves ROI]` | `Capture@ROISave :: imaq=Cam0, path=roi.png` |
| **`ROITranslate`** | `imaq`, `basePt`, `newPt` | — | `[effect: translates ROI]` | `Capture@ROITranslate :: imaq=Cam0, basePt=Mark0, newPt=MarkNow` |
| **`CalibrateLoad`** | `imaq`, `path` | — | `[effect: loads calibration]` | `Capture@CalibrateLoad :: imaq=Cam0, path=cal.png` |
| **`CalibrateManual`** | `imaq` | `scale`(0.05), `originX`(0), `originY`(0), `angle`(0) | `[effect: manual calibration]` | `Capture@CalibrateManual :: imaq=Cam0 {scale=0.02, angle=1.5}` |
| **`CoordSet`** | `imaq`, `pt` | — | `[effect: sets vision coord]` | `Capture@CoordSet :: imaq=Cam0, pt=CalPts` |
| **`CoordSet(2Edge)`** | `imaq`, `xPt`, `yPt` | — | `[effect: sets 2-edge coord]` | `Capture@CoordSet(2Edge) :: imaq=Cam0, xPt=PtX, yPt=PtY` |
| **`CoordSet(Org+2Edge)`** | `imaq`, `orgPt`, `xPt`, `yPt` | — | `[effect: sets org+2-edge coord]` | `Capture@CoordSet(Org+2Edge) :: imaq=Cam0, orgPt=Org, xPt=PtX, yPt=PtY` |
| **`FindCircle(InOut)`** | `imaq` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindCircle(InOut) :: imaq=Cam0 -> pt=CirclePt` |
| **`FindCircle(OutIn)`** | `imaq` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindCircle(OutIn) :: imaq=Cam0 -> pt=CirclePt` |
| **`FindEdge`** | `imaq` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindEdge :: imaq=Cam0 -> pt=EdgePt` |
| **`FindGap`** | `imaq` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindGap :: imaq=Cam0 -> pt=GapPt` |
| **`FindPoint`** | `imaq` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindPoint :: imaq=Cam0 -> pt=FeatPt` |
| **`GeoMatchOne`** | `imaq`, `template` | `angle`("") | `-> var, pt` / `[effect: sets $(pt)#Find?]` | `Capture@GeoMatchOne :: imaq=Cam0, template=Mark.png {angle=30} -> var=Score, pt=MarkPt` |
| **`PatternMatchOne`** | `imaq`, `template` | `angle`("") | `-> var, pt` / `[effect: sets $(pt)#Find?]` | `Capture@PatternMatchOne :: imaq=Cam0, template=Mark.png -> var=Score, pt=MarkPt` |
| **`PatternMatchOne(Fast)`** | `imaq`, `template` | `angle`("") | `-> var, pt` / `[effect: sets $(pt)#Find?]` | `Capture@PatternMatchOne(Fast) :: imaq=Cam0, template=Mark.png -> var=Score, pt=MarkPt` |

---

## 8. `DAQ` 模組（18 條指令）

| 指令 (`DAQ@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddDAQ`** | `daq`, `driver` | `rate`(""), `count`(""), `wave`(""), `options`("") | `[effect: reg DAQ $(daq)]` | `DAQ@AddDAQ :: daq=Daq0, driver=MockDAQ {rate=1000, count=500, wave=Sine}` |
| **`Start`** `[err]` | `daq` | — | `[effect: starts $(daq)]` | `DAQ@Start :: daq=Daq0` |
| **`Stop`** `[err]` | `daq` | — | `[effect: stops $(daq)]` | `DAQ@Stop :: daq=Daq0` |
| **`Wait`** `[err]` | `daq` | `timeoutSec`*(5) | `[effect: waits $(daq)#OK?]` | `DAQ@Wait :: daq=Daq0 {timeoutSec=3} [[ErrD]]採樣逾時` |
| **`AnalogIn`** `[err]` | `daq` | — | `-> [opt] wav` / `[effect: sets $(daq)#OK?]` | `DAQ@AnalogIn :: daq=Daq0 -> wav=ForceSamples` |
| **`AnalogInWaveForm`** `[err]` | `daq` | `sec`("") | `-> [opt] wav` / `[effect: sets $(daq)#OK?]` | `DAQ@AnalogInWaveForm :: daq=Daq0 {sec=0.5} -> wav=PressCurve` |
| **`AnalogOut`** `[err]` | `daq`, `val` | — | `[effect: writes AO on $(daq)]` | `DAQ@AnalogOut :: daq=Daq0, val=3.3` |
| **`AnalogOutWaveForm`** `[err]` | `daq`, `wav` | — | `[effect: streams $(wav) to $(daq)]` | `DAQ@AnalogOutWaveForm :: daq=Daq0, wav=DriveWave` |
| **`WaveFormGenerate`** | `wav` | `type`("Sine"), `rate`(1000), `count`(200), `amp`(5), `offset`(0) | `[effect: generates $(wav)]` | `DAQ@WaveFormGenerate :: wav=RefW {type=Triangle, count=100, amp=10}` |
| **`WaveFormLoad`** | `wav` | `path`("") | `[effect: loads $(wav)]` | `DAQ@WaveFormLoad :: wav=RefW {path=ref.csv}` |
| **`WaveFormSave`** | `wav` | `path`("") | `[effect: saves $(wav)]` | `DAQ@WaveFormSave :: wav=PressCurve {path=log.csv}` |
| **`Calculate`** | `wav`, `expr` | — | `[effect: $(wav)=eval($(expr))]` | `DAQ@Calculate :: wav=DiffW, expr=W1 - W2` |
| **`Filter`** | `wav`, `type` | `window`(3) | `[effect: filters $(wav)]` | `DAQ@Filter :: wav=PressCurve, type=MovingAverage {window=5}` |
| **`MergeWaveForm`** | `wav`, `wavs` | — | `[effect: merges $(wavs) -> $(wav)]` | `DAQ@MergeWaveForm :: wav=MultiW, wavs=[Ch0;Ch1]` |
| **`CompareAvgInRange`** | `wav`, `max`, `min` | `start`(0), `end`(-1) | `[effect: sets $(wav)#OK?]` | `DAQ@CompareAvgInRange :: wav=PressCurve, max=10, min=2` |
| **`CompareMaxMinInRange`** | `wav`, `max`, `min` | `start`(0), `end`(-1) | `[effect: sets $(wav)#OK?]` | `DAQ@CompareMaxMinInRange :: wav=PressCurve, max=12, min=0` |
| **`CompareCoverBy`** | `wav`, `upper` | `lower`("") | `[effect: sets $(wav)#OK?]` | `DAQ@CompareCoverBy :: wav=PressCurve, upper=UpperW {lower=LowerW}` |
| **`Display2User`** | `wav`, `ctrl` | — | `[effect: renders $(wav) -> $(ctrl)]` | `DAQ@Display2User :: wav=PressCurve, ctrl=WaveChart1` |

---

## 9. `USER` 模組（21 條指令）

| 指令 (`User@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`SwitchUserPanel`** | `panel` | — | `[effect: ActivePanel=$(panel)]` | `User@SwitchUserPanel :: panel=StationA` |
| **`Param2UserPanel`** | — | — | `[effect: refreshes UserPanel]` | `User@Param2UserPanel` |
| **`ShowControl`** | `ctrls` | — | `[effect: shows $(ctrls)]` | `User@ShowControl :: ctrls=[BtnStart;LblStatus]` |
| **`HideControl`** | `ctrls` | — | `[effect: hides $(ctrls)]` | `User@HideControl :: ctrls=BtnDebug` |
| **`EnableControl`** | `ctrls` | — | `[effect: enables $(ctrls)]` | `User@EnableControl :: ctrls=[BtnRun;BtnStop]` |
| **`DisableControl`** | `ctrls` | — | `[effect: disables $(ctrls)]` | `User@DisableControl :: ctrls=BtnRun` |
| **`GetControlValue`** | `ctrl` | — | `-> [opt] var` / `[effect: default $UserPanel_$(ctrl)]` | `User@GetControlValue :: ctrl=TxtSpeed -> var=CurSpeed` |
| **`SetControlValue`** | `ctrl`, `val` | — | `[effect: sets UI $(ctrl)]` | `User@SetControlValue :: ctrl=TxtSpeed, val=150` |
| **`QueryControls`** | — | `panel`("Active"), `role`("All"), `kind`("All") | `-> [opt] var` / `[effect: default $UserPanelControls]` | `User@QueryControls {kind=Button} -> var=BtnList` |
| **`ButtonValue`** | `ctrl` | `on`(true) | `[effect: sets button $(ctrl)]` | `User@ButtonValue :: ctrl=BtnAuto {on=false}` |
| **`ButtonTrigger`** | `ctrl` | `on`(true) | `[effect: fires button & UserPanel#$(ctrl)?]` | `User@ButtonTrigger :: ctrl=BtnStart` |
| **`ButtonText`** | `ctrl`, `text` | — | `[effect: sets button text]` | `User@ButtonText :: ctrl=BtnStart, text="運行中"` |
| **`ButtonBlinking`** | `ctrl` | `on`(true) | `[effect: sets blinking]` | `User@ButtonBlinking :: ctrl=BtnAlarm {on=true}` |
| **`TextBoxValue`** | `ctrl`, `text` | — | `[effect: sets TextBox]` | `User@TextBoxValue :: ctrl=TxtMsg, text="Ready"` |
| **`TextBoxTrigger`** | `ctrl`, `text` | — | `[effect: fires TextBox & syncs VAR]` | `User@TextBoxTrigger :: ctrl=TxtLot, text="LOT-001"` |
| **`TextBoxBlinking`** | `ctrl` | `on`(true) | `[effect: sets blinking]` | `User@TextBoxBlinking :: ctrl=TxtMsg {on=false}` |
| **`TextRingValue`** | `ctrl`, `val` | — | `[effect: sets TextRing]` | `User@TextRingValue :: ctrl=CmbMode, val=Auto` |
| **`TextRingTrigger`** | `ctrl`, `val` | — | `[effect: fires TextRing & syncs VAR]` | `User@TextRingTrigger :: ctrl=CmbMode, val=Manual` |
| **`TextRingBlinking`** | `ctrl` | `on`(true) | `[effect: sets blinking]` | `User@TextRingBlinking :: ctrl=CmbMode {on=true}` |
| **`TabValue`** | `ctrl`, `page` | — | `[effect: selects Tab]` | `User@TabValue :: ctrl=MainTabs, page=1` |
| **`TabTrigger`** | `ctrl`, `page` | — | `[effect: fires Tab change]` | `User@TabTrigger :: ctrl=MainTabs, page=Settings` |

---

## 10. `PYTHON` 模組（12 條指令）

| 指令 (`Python@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`Init`** | — | `session`("") | `[effect: init Python]` | `Python@Init` |
| **`Reset`** | — | — | `[effect: resets all sessions]` | `Python@Reset` |
| **`ResetSession`** | — | `session`("") | `[effect: resets $(session)]` | `Python@ResetSession {session=VisionS1}` |
| **`File`** | `path` | `session`("") | `[effect: runs $(path)]` | `Python@File :: path=calc.py {session=VisionS1}` |
| **`Script`** `[err]` | `code` | `session`("") | `[effect: runs inline code]` | `Python@Script :: code="res = x * 2" {session=S1}` |
| **`SetVariable`** | `var` | `val`(""), `py`(""), `session`("") | `[effect: ARCOS var -> Python]` | `Python@SetVariable :: var=Count {py=py_cnt, session=S1}` |
| **`GetVariable`** | `py` | `session`("") | `-> [opt] var` / `[effect: Python -> ARCOS var]` | `Python@GetVariable :: py=res {session=S1} -> var=Result` |
| **`SetPoint`** | `pt` | `py`(""), `session`("") | `[effect: ARCOS pt -> Python]` | `Python@SetPoint :: pt=PickPos {py=p_pick}` |
| **`GetPoint`** | `py` | `session`("") | `-> [opt] pt` / `[effect: Python -> ARCOS pt]` | `Python@GetPoint :: py=p_out -> pt=PlacePos` |
| **`SetCapture`** | `imaq` | `py`(""), `session`("") | `[effect: ARCOS imaq -> Python]` | `Python@SetCapture :: imaq=Cam0 {py=img0}` |
| **`SetCaptureFile`** | `imaq`, `path`, `py` | `session`("") | `[effect: saves & binds Python]` | `Python@SetCaptureFile :: imaq=Cam0, path=snap.png, py=img0` |
| **`GetCapture`** | `py` | `session`("") | `-> [opt] imaq` / `[effect: Python -> ARCOS imaq]` | `Python@GetCapture :: py=out_img -> imaq=CamOut` |

---

## 11. `REDIS` 模組（9 條指令）

> **多 Session 預設規則**：第一個呼叫 `Redis@CreateSession` 建立的 Session 自動成為預設 Session；其餘所有 Redis 指令省略 `{session=...}` 時操作預設 Session，填寫 `{session=MES}` 時則隔離操作指定 Session。

| 指令 (`Redis@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`CreateSession`** | `session` | `endpoint`("127.0.0.1:6379") | `[effect: sets RedisErr#$(session)=OK]` | `Redis@CreateSession :: session=Line1 {endpoint=192.168.1.10:6379}` |
| **`Set`** | `key`, `val` | `session`("") | `[effect: sets RedisKV[$(key)]]` | `Redis@Set :: key=LotId, val=LOT2026 {session=MES}` |
| **`Get`** | `key` | `session`("") | `-> [opt] var` / `[effect: default $(key)]` | `Redis@Get :: key=LotId {session=MES} -> var=CurLot` |
| **`LPush`** | `key`, `val` | `session`("") | `[effect: pushes to RedisList[$(key)]]` | `Redis@LPush :: key=JobQ, val=Part001 {session=Line1}` |
| **`LPop`** | `key` | `session`("") | `-> [opt] var` / `[effect: default $(key)]` | `Redis@LPop :: key=JobQ {session=Line1} -> var=NextJob` |
| **`BLPop`** `[err]` | `key` | `session`(""), `timeoutSec`*(5) | `-> [opt] var` / `[effect: blocks & pops]` | `Redis@BLPop :: key=JobQ {session=Line1, timeoutSec=3} -> var=NextJob [[ErrQ]]佇列逾時` |
| **`Publish`** | `key`, `val` | `session`("") | `[effect: publishes to $(key)]` | `Redis@Publish :: key=LineEvt, val=CycleDone {session=Line1}` |
| **`Subscribe`** | `key` | `session`("") | `[effect: subscribes $(key)]` | `Redis@Subscribe :: key=LineEvt {session=Line1}` |
| **`CMD`** | `cmd` | `session`("") | `[effect: runs raw Redis $(cmd)]` | `Redis@CMD :: cmd=PING {session=Line1}` |

---

## 12. `MULTISTATION` 模組（15 條指令）

> **工站.工位雙模定址規則**：透過 `MultiStation@AddStation` 建立工站群組，支援**命名工位清單**（`{items=[入料;加工;檢驗;出料]}`）或 **1-based 料盤工位＋點位綁定**（`{slots=20, pt=Tray2D}`）。定址採 **`工站.工位`**（如 `FeedInStation.入料`、`TrayA.3`）：有料/無料/OK/NG/Busy 自動映射至 **IO 引擎**（`#On?`/`#Run?`/`#OK?`/`#NG?`/`#Busy?`），工位隨行資料自動映射至 **Variable 引擎**（`工站.工位#欄位`）並隨 `StateShift`（環形緩衝區向前 `+shift` / 向後 `-shift`；直線輸送帶僅需對頭/尾呼叫 `SetStationOFF` 清空即可）與 `Transfer` 同步搬移；`SetStationOFF` 預設一併清空 OK/NG 與隨行資料。`FindFirstOK` 與 `FindEmpty` 專用於進/出料 Tray 盤快速定位取放工位與座標並更新 `$(station)#Found?`。

| 指令 (`MultiStation@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddStation`** | `station` | `items`(""), `slots`(0), `pt`("") | `[effect: reg station & IO/Var bindings]` | `MultiStation@AddStation :: station=FeedInStation {items=[入料;加工;檢驗;出料], pt=TurnPts}`<br>`MultiStation@AddStation :: station=TrayA {slots=20, pt=Tray2D}` |
| **`SetStationON`** | `station` | — | `[effect: sets $(station)#On?=true]` | `MultiStation@SetStationON :: station=FeedInStation.入料` |
| **`SetStationOFF`** | `station` | `clearData`(true) | `[effect: #On?=false & clears OK/NG+Data]` | `MultiStation@SetStationOFF :: station=FeedInStation.出料` |
| **`SetStationOK`** | `station` | — | `[effect: sets $(station)#OK?=true]` | `MultiStation@SetStationOK :: station=FeedInStation.檢驗` |
| **`SetStationNG`** | `station` | — | `[effect: sets $(station)#NG?=true]` | `MultiStation@SetStationNG :: station=TrayA.3` |
| **`SetData`** | `station`, `val` | `field`("Data") | `[effect: var $(station)#$(field)=$(val)]` | `MultiStation@SetData :: station=FeedInStation.入料, val=SN001 {field=SN}` |
| **`GetData`** | `station` | `field`("Data") | `-> [opt] var` | `MultiStation@GetData :: station=FeedInStation.加工 {field=SN} -> var=CurSN` |
| **`GetPoint`** | `station` | — | `-> [opt] pt` | `MultiStation@GetPoint :: station=TrayA.3 -> pt=TargetPt` |
| **`ChkStationRunState`** | `station` | — | `[effect: updates $(station)#Busy?]` | `MultiStation@ChkStationRunState :: station=FeedInStation.加工` |
| **`StateShift`** | — | `station`(""), `shift`("1") | `[effect: shifts ring-buffer states & data]` | `MultiStation@StateShift {station=FeedInStation, shift=1}`<br>`MultiStation@StateShift {station=FeedInStation, shift=-1}` |
| **`Transfer`** | `from`, `to` | `clearSource`(true) | `[effect: moves state & data $(from)->$(to)]` | `MultiStation@Transfer :: from=FeedInStation.出料, to=OutTray.3` |
| **`FindFirstOK`** | `station` | `allowUnchecked`(false) | `-> [opt] var, pt` / `[effect: sets $(station)#Found?]` | `MultiStation@FindFirstOK :: station=InTray -> var=SrcSlot, pt=PickPt` |
| **`FindEmpty`** | `station` | — | `-> [opt] var, pt` / `[effect: sets $(station)#Found?]` | `MultiStation@FindEmpty :: station=OutTray -> var=DstSlot, pt=PlacePt` |
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
6. [ ] `MultiStation@*` 是否正確採 `工站.工位` 定址，且直線移載已在 `StateShift` 後顯式對頭/尾執行 `SetStationOFF`？
7. [ ] 所有逾時等待與高風險運動指令是否已掛載 `[[ErrLabel]]` 錯誤處理跳轉？
