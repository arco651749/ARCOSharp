# 模組驗證報告 03：`VARIABLE`（34 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/VARIABLE/*.csvi`
- **測試程式**：[VariableModuleVerificationTests.cs](../tests/VariableModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/VARIABLE/`（目前已產出 35 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 34 / 34 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `Append.csvi` | `Variable@Append :: var=LogStr, val="_Done"` | PASS | [Append.png](../screenshots/VARIABLE/Append.png) |
| 2 | `Calculate.csvi` | `Variable@Calculate :: var=Total, expr=A + B` | PASS | [Calculate.png](../screenshots/VARIABLE/Calculate.png) |
| 3 | `Dec.csvi` | `Variable@Dec :: var=Count {step=1}` | PASS | [Dec.png](../screenshots/VARIABLE/Dec.png) |
| 4 | `Delete.csvi` | `Variable@Delete :: var=Temp` | PASS | [Delete.png](../screenshots/VARIABLE/Delete.png) |
| 5 | `Flush.csvi` | `Variable@Flush :: var=SyncEvt` | PASS | [Flush.png](../screenshots/VARIABLE/Flush.png) |
| 6 | `GetPointElement.csvi` | `Variable@GetPointElement :: var=PickX, pt=PickPos, axis=X` | PASS | [GetPointElement.png](../screenshots/VARIABLE/GetPointElement.png) |
| 7 | `GetPointString.csvi` | `Variable@GetPointString :: var=PickStr, pt=PickPos` | PASS | [GetPointString.png](../screenshots/VARIABLE/GetPointString.png) |
| 8 | `Inc.csvi` | `Variable@Inc :: var=Count {step=2}` | PASS | [Inc.png](../screenshots/VARIABLE/Inc.png) |
| 9 | `Joint.csvi` | `Variable@Joint :: var=Items, sep="-"` | PASS | [Joint.png](../screenshots/VARIABLE/Joint.png) |
| 10 | `LFindIndexSet.csvi` | `Variable@LFindIndexSet :: var=IdArr, val=ID003 {linked=[NameArr;PosArr]}` | PASS | [LFindIndexSet.png](../screenshots/VARIABLE/LFindIndexSet.png) |
| 11 | `LGetIndexSize.csvi` | `Variable@LGetIndexSize :: var=Arr` | PASS | [LGetIndexSize.png](../screenshots/VARIABLE/LGetIndexSize.png) |
| 12 | `LInsert.csvi` | `Variable@LInsert :: var=Arr, val=5, logic=>0` | PASS | [LInsert.png](../screenshots/VARIABLE/LInsert.png) |
| 13 | `LPop.csvi` | `Variable@LPop :: var=Queue -> out=HeadVal` | PASS | [LPop.png](../screenshots/VARIABLE/LPop.png) |
| 14 | `LPush.csvi` | `Variable@LPush :: var=Queue, val=Item1` | PASS | [LPush.png](../screenshots/VARIABLE/LPush.png) |
| 15 | `LRemove.csvi` | `Variable@LRemove :: var=Arr, val=BadItem` | PASS | [LRemove.png](../screenshots/VARIABLE/LRemove.png) |
| 16 | `LResetIndex.csvi` | `Variable@LResetIndex :: var=Arr {index=0}` | PASS | [LResetIndex.png](../screenshots/VARIABLE/LResetIndex.png) |
| 17 | `LReverse.csvi` | `Variable@LReverse :: var=Arr` | PASS | [LReverse.png](../screenshots/VARIABLE/LReverse.png) |
| 18 | `LSetIndexValue.csvi` | `Variable@LSetIndexValue :: var=Arr, index=2, val=NewVal` | PASS | [LSetIndexValue.png](../screenshots/VARIABLE/LSetIndexValue.png) |
| 19 | `Publish.csvi` | `Variable@Publish :: vars=[SyncEvt]` | PASS | [Publish.png](../screenshots/VARIABLE/Publish.png) |
| 20 | `Rename.csvi` | `Variable@Rename :: var=OldKey, to=NewKey` | PASS | [Rename.png](../screenshots/VARIABLE/Rename.png) |
| 21 | `RPop.csvi` | `Variable@RPop :: var=Queue -> out=TailVal` | PASS | [RPop.png](../screenshots/VARIABLE/RPop.png) |
| 22 | `RPush.csvi` | `Variable@RPush :: var=Queue, val=Item2` | PASS | [RPush.png](../screenshots/VARIABLE/RPush.png) |
| 23 | `SAdd.csvi` | `Variable@SAdd :: var=SetA, val=K1` | PASS | [SAdd.png](../screenshots/VARIABLE/SAdd.png) |
| 24 | `SDiff.csvi` | `Variable@SDiff :: var=SetOut, varA=SetA, varB=SetB` | PASS | [SDiff.png](../screenshots/VARIABLE/SDiff.png) |
| 25 | `Select.csvi` | `Variable@Select :: var=Result, logic=Pass?, true=OK, false=NG` | PASS | [Select.png](../screenshots/VARIABLE/Select.png) |
| 26 | `Set.csvi` | `Variable@Set :: var=Count, val=10 {section=Global}` | PASS | [Set.png](../screenshots/VARIABLE/Set.png) |
| 27 | `Shift.csvi` | `Variable@Shift :: vars=[V1;V2;V3] {shift=1}` | PASS | [Shift.png](../screenshots/VARIABLE/Shift.png) |
| 28 | `SIntersection.csvi` | `Variable@SIntersection :: var=SetOut, varA=SetA, varB=SetB` | PASS | [SIntersection.png](../screenshots/VARIABLE/SIntersection.png) |
| 29 | `Slice.csvi` | `Variable@Slice :: var=CsvRow, sep=","` | PASS | [Slice.png](../screenshots/VARIABLE/Slice.png) |
| 30 | `Subscribe.csvi` | `Variable@Subscribe :: var=SyncEvt` | PASS | [Subscribe.png](../screenshots/VARIABLE/Subscribe.png) |
| 31 | `SUnion.csvi` | `Variable@SUnion :: var=SetOut, varA=SetA, varB=SetB` | PASS | [SUnion.png](../screenshots/VARIABLE/SUnion.png) |
| 32 | `Trim.csvi` | `Variable@Trim :: var=Barcode, start=0, end=8` | PASS | [Trim.png](../screenshots/VARIABLE/Trim.png) |
| 33 | `VariableLoad.csvi` | `Variable@VariableLoad :: file=RecipeA.var` | PASS | [VariableLoad.png](../screenshots/VARIABLE/VariableLoad.png) |
| 34 | `VariableSave.csvi` | `Variable@VariableSave :: file=RecipeA.var {patterns=[Cfg_*;Cal_*]}` | PASS | [VariableSave.png](../screenshots/VARIABLE/VariableSave.png) |
