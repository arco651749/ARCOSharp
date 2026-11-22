# 模組驗證報告 10：`PYTHON`（10 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/PYTHON/*.csvi`
- **測試程式**：[PythonModuleVerificationTests.cs](../tests/PythonModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/PYTHON/`（目前已產出 11 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 10 / 10 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `File.csvi` | `Python@File :: file=calc.py {session=VisionS1}` | PASS | [File.png](../screenshots/PYTHON/File.png) |
| 2 | `GetCapture.csvi` | `Python@GetCapture :: py=out_img -> cam=CamOut` | PASS | [GetCapture.png](../screenshots/PYTHON/GetCapture.png) |
| 3 | `GetPoint.csvi` | `Python@GetPoint :: py=p_out -> pt=PlacePos` | PASS | [GetPoint.png](../screenshots/PYTHON/GetPoint.png) |
| 4 | `GetVariable.csvi` | `Python@GetVariable :: py=res {session=S1} -> var=Result` | PASS | [GetVariable.png](../screenshots/PYTHON/GetVariable.png) |
| 5 | `Init.csvi` | `Python@Init` | PASS | [Init.png](../screenshots/PYTHON/Init.png) |
| 6 | `Reset.csvi` | `Python@Reset`<br>`Python@Reset {session=VisionS1}`<br>`Python@Reset {session=ALL}` | PASS | [Reset.png](../screenshots/PYTHON/Reset.png) |
| 7 | `Script.csvi` | `PYTHON@Script` | PASS | [Script.png](../screenshots/PYTHON/Script.png) |
| 8 | `SetCapture.csvi` | `Python@SetCapture :: cam=Cam0 {py=img0}` | PASS | [SetCapture.png](../screenshots/PYTHON/SetCapture.png) |
| 9 | `SetPoint.csvi` | `Python@SetPoint :: pt=PickPos {py=p_pick}` | PASS | [SetPoint.png](../screenshots/PYTHON/SetPoint.png) |
| 10 | `SetVariable.csvi` | `Python@SetVariable :: var=Count {py=py_cnt, session=S1}` | PASS | [SetVariable.png](../screenshots/PYTHON/SetVariable.png) |
