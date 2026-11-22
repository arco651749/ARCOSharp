# 模組驗證報告 02：`THREAD`（31 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/THREAD/*.csvi`
- **測試程式**：[ThreadModuleVerificationTests.cs](../tests/ThreadModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/THREAD/`（目前已產出 32 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 31 / 31 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `Call.csvi` | `Thread@Call :: prog=SubInit {logic=NeedInit?}` | PASS | [Call.png](../screenshots/THREAD/Call.png) |
| 2 | `Else.csvi` | `Thread@Else` | PASS | [Else.png](../screenshots/THREAD/Else.png) |
| 3 | `ElseIf.csvi` | `Thread@ElseIf :: logic=Manual?` | PASS | [ElseIf.png](../screenshots/THREAD/ElseIf.png) |
| 4 | `EndIf.csvi` | `Thread@EndIf` | PASS | [EndIf.png](../screenshots/THREAD/EndIf.png) |
| 5 | `ErrorHandler.csvi` | `Thread@ErrorHandler :: label=ErrRecover` | PASS | [ErrorHandler.png](../screenshots/THREAD/ErrorHandler.png) |
| 6 | `ErrorRetry.csvi` | `Thread@ErrorRetry {logic=RetryOK?, offset=0}` | PASS | [ErrorRetry.png](../screenshots/THREAD/ErrorRetry.png) |
| 7 | `ForeachBreak.csvi` | `Thread@ForeachBreak {logic=Found?}` | PASS | [ForeachBreak.png](../screenshots/THREAD/ForeachBreak.png) |
| 8 | `ForeachContinue.csvi` | `Thread@ForeachContinue {logic=Skip?}` | PASS | [ForeachContinue.png](../screenshots/THREAD/ForeachContinue.png) |
| 9 | `ForeachLoop.csvi` | `Thread@ForeachLoop :: label=LoopItems, vars=[ItemList;PosList]` | PASS | [ForeachLoop.png](../screenshots/THREAD/ForeachLoop.png) |
| 10 | `ForeachStart.csvi` | `Thread@ForeachStart :: label=LoopItems` | PASS | [ForeachStart.png](../screenshots/THREAD/ForeachStart.png) |
| 11 | `GoOn.csvi` | `Thread@GoOn {logic=MaterialOK?, msg=缺料中止}` | PASS | [GoOn.png](../screenshots/THREAD/GoOn.png) |
| 12 | `If.csvi` | `Thread@If :: logic=Start? & !Alarm?` | PASS | [If.png](../screenshots/THREAD/If.png) |
| 13 | `Jump.csvi` | `THREAD@Jump` | PASS | [Jump.png](../screenshots/THREAD/Jump.png) |
| 14 | `KeySectionLock.csvi` | `Thread@KeySectionLock :: lock=GantryZone {timeout=10, msg=機構區佔用逾時}` | PASS | [KeySectionLock.png](../screenshots/THREAD/KeySectionLock.png) |
| 15 | `KeySectionUnLock.csvi` | `Thread@KeySectionUnLock :: lock=GantryZone` | PASS | [KeySectionUnLock.png](../screenshots/THREAD/KeySectionUnLock.png) |
| 16 | `Label.csvi` | `Thread@Label :: label=RetryPoint` | PASS | [Label.png](../screenshots/THREAD/Label.png) |
| 17 | `MutePopupMessage.csvi` | `Thread@MutePopupMessage :: enabled=true` | PASS | [MutePopupMessage.png](../screenshots/THREAD/MutePopupMessage.png) |
| 18 | `Repeat.csvi` | `Thread@Repeat {logic=AutoRun?}` | PASS | [Repeat.png](../screenshots/THREAD/Repeat.png) |
| 19 | `Resume.csvi` | `Thread@Resume :: thread=1 {logic=ResumeBtn?}` | PASS | [Resume.png](../screenshots/THREAD/Resume.png) |
| 20 | `Return.csvi` | `Thread@Return {logic=Done?}` | PASS | [Return.png](../screenshots/THREAD/Return.png) |
| 21 | `Run.csvi` | `Thread@Run :: prog=Prog1, thread=1 {startLine=InitStep, logic=Auto?}` | PASS | [Run.png](../screenshots/THREAD/Run.png) |
| 22 | `RunNextCMD.csvi` | `THREAD@RunNextCMD` | PASS | [RunNextCMD.png](../screenshots/THREAD/RunNextCMD.png) |
| 23 | `SkipNextCMD.csvi` | `THREAD@SkipNextCMD` | PASS | [SkipNextCMD.png](../screenshots/THREAD/SkipNextCMD.png) |
| 24 | `Stop.csvi` | `Thread@Stop {logic=Abort?, msg=使用者中止}` | PASS | [Stop.png](../screenshots/THREAD/Stop.png) |
| 25 | `SubThreadEnd.csvi` | `Thread@SubThreadEnd :: sub=CamTask` | PASS | [SubThreadEnd.png](../screenshots/THREAD/SubThreadEnd.png) |
| 26 | `SubThreadScript.csvi` | `Thread@SubThreadScript :: sub=LoadTask, file=Load.init` | PASS | [SubThreadScript.png](../screenshots/THREAD/SubThreadScript.png) |
| 27 | `SubThreadStart.csvi` | `Thread@SubThreadStart :: sub=CamTask {logic=EnCam?}` | PASS | [SubThreadStart.png](../screenshots/THREAD/SubThreadStart.png) |
| 28 | `SubThreadSuspend.csvi` | `Thread@SubThreadSuspend :: sub=CamTask` | PASS | [SubThreadSuspend.png](../screenshots/THREAD/SubThreadSuspend.png) |
| 29 | `SubThreadWait.csvi` | `Thread@SubThreadWait :: subs=[CamTask;IoTask] {skipNotFound=true}` | PASS | [SubThreadWait.png](../screenshots/THREAD/SubThreadWait.png) |
| 30 | `Suspend.csvi` | `Thread@Suspend :: thread=1 {logic=Err?}` | PASS | [Suspend.png](../screenshots/THREAD/Suspend.png) |
| 31 | `Wait.csvi` | `Thread@Wait :: threads=[Thread1;Thread2]` | PASS | [Wait.png](../screenshots/THREAD/Wait.png) |
