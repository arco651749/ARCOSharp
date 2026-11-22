# 模組驗證報告 08：`POINT&PATH`（22 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/POINT&PATH/*.csvi`
- **測試程式**：[PointAndPathModuleVerificationTests.cs](../tests/PointAndPathModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/POINT&PATH/`（目前已產出 23 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 22 / 22 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `AddUVW.csvi` | `Point&Path@AddUVW :: pt=P1, delta=(0,0,90)` | PASS | [AddUVW.png](../screenshots/POINT&PATH/AddUVW.png) |
| 2 | `AddXYZ.csvi` | `Point&Path@AddXYZ :: pt=P1, delta=(10,20,0)` | PASS | [AddXYZ.png](../screenshots/POINT&PATH/AddXYZ.png) |
| 3 | `Calculate.csvi` | `Point&Path@Calculate :: pt=P_Out, expr=P1 + P2 {append=true}` | PASS | [Calculate.png](../screenshots/POINT&PATH/Calculate.png) |
| 4 | `ChkInRangeXY.csvi` | `Point&Path@ChkInRangeXY :: pt=P1, rangePt=SafeBox {axis=XYZ} -> io=IsSafe?` *(1點圓/球、2點矩形/長方體、3點立體或三角、多點多邊形/稜柱)*` | PASS | [ChkInRangeXY.png](../screenshots/POINT&PATH/ChkInRangeXY.png) |
| 5 | `Circle2DPath.csvi` | `Point&Path@Circle2DPath :: pt=ArcPath, startDeg=0, endDeg=180, segments=12` | PASS | [Circle2DPath.png](../screenshots/POINT&PATH/Circle2DPath.png) |
| 6 | `ClearPoint.csvi` | `Point&Path@ClearPoint :: pt=Tray` | PASS | [ClearPoint.png](../screenshots/POINT&PATH/ClearPoint.png) |
| 7 | `CoordWS(2Edge).csvi` | `POINT&PATH@CoordWS(2Edge)` | PASS | [CoordWS(2Edge).png](../screenshots/POINT&PATH/CoordWS(2Edge).png) |
| 8 | `EnableAxis.csvi` | `Point&Path@EnableAxis :: pt=P1, axis=XY` | PASS | [EnableAxis.png](../screenshots/POINT&PATH/EnableAxis.png) |
| 9 | `InsertOnePoint.csvi` | `Point&Path@InsertOnePoint :: pt=Tray, val=(10,20,30)` | PASS | [InsertOnePoint.png](../screenshots/POINT&PATH/InsertOnePoint.png) |
| 10 | `Pallet1D.csvi` | `Point&Path@Pallet1D :: pt=Tray1D, count=10` | PASS | [Pallet1D.png](../screenshots/POINT&PATH/Pallet1D.png) |
| 11 | `Pallet2D.csvi` | `Point&Path@Pallet2D :: pt=Tray2D, countX=4, countY=5` | PASS | [Pallet2D.png](../screenshots/POINT&PATH/Pallet2D.png) |
| 12 | `Point2WS.csvi` | `Point&Path@Point2WS :: pt=P1, ws=WS1` | PASS | [Point2WS.png](../screenshots/POINT&PATH/Point2WS.png) |
| 13 | `ReplaceH.csvi` | `Point&Path@ReplaceH :: pt=P1, h={"speed":80}` | PASS | [ReplaceH.png](../screenshots/POINT&PATH/ReplaceH.png) |
| 14 | `ReplaceUVW.csvi` | `Point&Path@ReplaceUVW :: pt=P1, val=(0,0,180)` | PASS | [ReplaceUVW.png](../screenshots/POINT&PATH/ReplaceUVW.png) |
| 15 | `ReplaceXYZ.csvi` | `Point&Path@ReplaceXYZ :: pt=P1, val=(100,50,10)` | PASS | [ReplaceXYZ.png](../screenshots/POINT&PATH/ReplaceXYZ.png) |
| 16 | `ResetPalletIndex.csvi` | `Point&Path@ResetPalletIndex :: pt=Tray2D {index=0}` | PASS | [ResetPalletIndex.png](../screenshots/POINT&PATH/ResetPalletIndex.png) |
| 17 | `Select.csvi` | `Point&Path@Select :: pt=P_Target, logic=Left?, true=P_Left, false=P_Right` | PASS | [Select.png](../screenshots/POINT&PATH/Select.png) |
| 18 | `SelfMotion.csvi` | `Point&Path@SelfMotion :: pt=PickPt, basePt=TeachMark, newPt=CamMark` | PASS | [SelfMotion.png](../screenshots/POINT&PATH/SelfMotion.png) |
| 19 | `SubUVW.csvi` | `Point&Path@SubUVW :: pt=P1, delta=(0,0,45)` | PASS | [SubUVW.png](../screenshots/POINT&PATH/SubUVW.png) |
| 20 | `SubXYZ.csvi` | `Point&Path@SubXYZ :: pt=P1, delta=OffsetPt` | PASS | [SubXYZ.png](../screenshots/POINT&PATH/SubXYZ.png) |
| 21 | `TransImaqPoint.csvi` | `Point&Path@TransImaqPoint :: imaqs=Cam0, pt=P_Vision` | PASS | [TransImaqPoint.png](../screenshots/POINT&PATH/TransImaqPoint.png) |
| 22 | `WS2Point.csvi` | `Point&Path@WS2Point :: pt=P1, ws=WS1` | PASS | [WS2Point.png](../screenshots/POINT&PATH/WS2Point.png) |
