# 模組驗證報告 06：`CAPTURE`（28 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/CAPTURE/*.csvi`
- **測試程式**：[CaptureModuleVerificationTests.cs](../tests/CaptureModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/CAPTURE/`（目前已產出 29 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 28 / 28 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `AddCam.csvi` | `Capture@AddCam :: cam=Cam0, driver=MockCamera {device=0, width=640, height=480}` | PASS | [AddCam.png](../screenshots/CAPTURE/AddCam.png) |
| 2 | `CalibrateLoad.csvi` | `Capture@CalibrateLoad :: cam=Cam0, file=cal.png` | PASS | [CalibrateLoad.png](../screenshots/CAPTURE/CalibrateLoad.png) |
| 3 | `CalibrateManual.csvi` | `Capture@CalibrateManual :: cam=Cam0 {scale=0.02, angle=1.5}` | PASS | [CalibrateManual.png](../screenshots/CAPTURE/CalibrateManual.png) |
| 4 | `ClearImaq.csvi` | `Capture@ClearImaq :: imaqs=FlyBuf` | PASS | [ClearImaq.png](../screenshots/CAPTURE/ClearImaq.png) |
| 5 | `Config.csvi` | `CAPTURE@Config` | PASS | [Config.png](../screenshots/CAPTURE/Config.png) |
| 6 | `CoordSet(2Edge).csvi` | `CAPTURE@CoordSet(2Edge)` | PASS | [CoordSet(2Edge).png](../screenshots/CAPTURE/CoordSet(2Edge).png) |
| 7 | `CoordSet(Org+2Edge).csvi` | `CAPTURE@CoordSet(Org+2Edge)` | PASS | [CoordSet(Org+2Edge).png](../screenshots/CAPTURE/CoordSet(Org+2Edge).png) |
| 8 | `CoordSet.csvi` | `Capture@CoordSet(Org+2Edge) :: cam=Cam0, orgPt=Org, xPt=PtX, yPt=PtY` | PASS | [CoordSet.png](../screenshots/CAPTURE/CoordSet.png) |
| 9 | `Display2User.csvi` | `Capture@Display2User :: cam=Cam0, ui=ImgView1` | PASS | [Display2User.png](../screenshots/CAPTURE/Display2User.png) |
| 10 | `FindCircle(InOut).csvi` | `CAPTURE@FindCircle(InOut)` | PASS | [FindCircle(InOut).png](../screenshots/CAPTURE/FindCircle(InOut).png) |
| 11 | `FindCircle(OutIn).csvi` | `CAPTURE@FindCircle(OutIn)` | PASS | [FindCircle(OutIn).png](../screenshots/CAPTURE/FindCircle(OutIn).png) |
| 12 | `FindEdge.csvi` | `Capture@FindEdge :: cam=Cam0 -> pt=EdgePt` | PASS | [FindEdge.png](../screenshots/CAPTURE/FindEdge.png) |
| 13 | `FindGap.csvi` | `Capture@FindGap :: cam=Cam0 -> pt=GapPt` | PASS | [FindGap.png](../screenshots/CAPTURE/FindGap.png) |
| 14 | `FindPoint.csvi` | `Capture@FindPoint :: cam=Cam0 -> pt=FeatPt` | PASS | [FindPoint.png](../screenshots/CAPTURE/FindPoint.png) |
| 15 | `GeoMatchOne.csvi` | `Capture@GeoMatchOne :: cam=Cam0, file=Mark.png {angle=30} -> var=Score, pt=MarkPt` | PASS | [GeoMatchOne.png](../screenshots/CAPTURE/GeoMatchOne.png) |
| 16 | `ImageSave.csvi` | `Capture@ImageSave :: imaqs=Cam0, file=out.png {burnOverlays=true}` | PASS | [ImageSave.png](../screenshots/CAPTURE/ImageSave.png) |
| 17 | `LoadFile.csvi` | `Capture@LoadFile :: imaqs=Cam0, file=sample.png` | PASS | [LoadFile.png](../screenshots/CAPTURE/LoadFile.png) |
| 18 | `LoadFile2Imaq.csvi` | `Capture@LoadFile2Imaq :: imaqs=BatchImgs, file="PICTURE/CAM0/img_\d+\.png"` | PASS | [LoadFile2Imaq.png](../screenshots/CAPTURE/LoadFile2Imaq.png) |
| 19 | `LoadNextImaq.csvi` | `Capture@LoadNextImaq :: cam=Cam0, imaqs=BatchImgs` | PASS | [LoadNextImaq.png](../screenshots/CAPTURE/LoadNextImaq.png) |
| 20 | `PatternMatchOne(Fast).csvi` | `CAPTURE@PatternMatchOne(Fast)` | PASS | [PatternMatchOne(Fast).png](../screenshots/CAPTURE/PatternMatchOne(Fast).png) |
| 21 | `PatternMatchOne.csvi` | `Capture@PatternMatchOne(Fast) :: cam=Cam0, file=Mark.png -> var=Score, pt=MarkPt` | PASS | [PatternMatchOne.png](../screenshots/CAPTURE/PatternMatchOne.png) |
| 22 | `ResetImaqIndex.csvi` | `Capture@ResetImaqIndex :: imaqs=BatchImgs {index=0}` | PASS | [ResetImaqIndex.png](../screenshots/CAPTURE/ResetImaqIndex.png) |
| 23 | `ROISave.csvi` | `Capture@ROISave :: cam=Cam0, file=roi.png` | PASS | [ROISave.png](../screenshots/CAPTURE/ROISave.png) |
| 24 | `ROISet.csvi` | `Capture@ROISet :: cam=Cam0, roi="10,20,300,200"` | PASS | [ROISet.png](../screenshots/CAPTURE/ROISet.png) |
| 25 | `ROITranslate.csvi` | `Capture@ROITranslate :: cam=Cam0, basePt=Mark0, newPt=MarkNow` | PASS | [ROITranslate.png](../screenshots/CAPTURE/ROITranslate.png) |
| 26 | `Snap.csvi` | `CAPTURE@Snap` | PASS | [Snap.png](../screenshots/CAPTURE/Snap.png) |
| 27 | `Trigger.csvi` | `CAPTURE@Trigger` | PASS | [Trigger.png](../screenshots/CAPTURE/Trigger.png) |
| 28 | `UpdateImage.csvi` | `Capture@UpdateImage :: cam=Cam0` | PASS | [UpdateImage.png](../screenshots/CAPTURE/UpdateImage.png) |
