# 模組驗證報告 07：`DAQ`（18 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/DAQ/*.csvi`
- **測試程式**：[DaqModuleVerificationTests.cs](../tests/DaqModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/DAQ/`（目前已產出 19 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 18 / 18 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `AddDAQ.csvi` | `DAQ@AddDAQ :: daq=Daq0, driver=MockDAQ {device=Dev1, rate=1000, sec=5, wave=Sine}` | PASS | [AddDAQ.png](../screenshots/DAQ/AddDAQ.png) |
| 2 | `AnalogIn.csvi` | `DAQ@AnalogIn` | PASS | [AnalogIn.png](../screenshots/DAQ/AnalogIn.png) |
| 3 | `AnalogInWaveForm.csvi` | `DAQ@AnalogInWaveForm` | PASS | [AnalogInWaveForm.png](../screenshots/DAQ/AnalogInWaveForm.png) |
| 4 | `AnalogOut.csvi` | `DAQ@AnalogOut` | PASS | [AnalogOut.png](../screenshots/DAQ/AnalogOut.png) |
| 5 | `AnalogOutWaveForm.csvi` | `DAQ@AnalogOutWaveForm` | PASS | [AnalogOutWaveForm.png](../screenshots/DAQ/AnalogOutWaveForm.png) |
| 6 | `Calculate.csvi` | `DAQ@Calculate :: wav=DiffW, expr=W1 - W2` | PASS | [Calculate.png](../screenshots/DAQ/Calculate.png) |
| 7 | `CompareAvgInRange.csvi` | `DAQ@CompareAvgInRange :: wav=PressCurve, max=10, min=2 {start=0.1, end=0.5}` | PASS | [CompareAvgInRange.png](../screenshots/DAQ/CompareAvgInRange.png) |
| 8 | `CompareCoverBy.csvi` | `DAQ@CompareCoverBy :: wav=PressCurve, upper=UpperW {lower=LowerW}` | PASS | [CompareCoverBy.png](../screenshots/DAQ/CompareCoverBy.png) |
| 9 | `CompareMaxMinInRange.csvi` | `DAQ@CompareMaxMinInRange :: wav=PressCurve, max=12, min=0 {start=0.1, end=0.5}` | PASS | [CompareMaxMinInRange.png](../screenshots/DAQ/CompareMaxMinInRange.png) |
| 10 | `Display2User.csvi` | `DAQ@Display2User :: wav=PressCurve, ui=WaveChart1` | PASS | [Display2User.png](../screenshots/DAQ/Display2User.png) |
| 11 | `Filter.csvi` | `DAQ@Filter :: wav=PressCurve, type=MovingAverage {window=5}` | PASS | [Filter.png](../screenshots/DAQ/Filter.png) |
| 12 | `MergeWaveForm.csvi` | `DAQ@MergeWaveForm :: wav=MultiW, wavs=[Ch0;Ch1]` | PASS | [MergeWaveForm.png](../screenshots/DAQ/MergeWaveForm.png) |
| 13 | `Start.csvi` | `DAQ@Start` | PASS | [Start.png](../screenshots/DAQ/Start.png) |
| 14 | `Stop.csvi` | `DAQ@Stop` | PASS | [Stop.png](../screenshots/DAQ/Stop.png) |
| 15 | `Wait.csvi` | `DAQ@Wait` | PASS | [Wait.png](../screenshots/DAQ/Wait.png) |
| 16 | `WaveFormGenerate.csvi` | `DAQ@WaveFormGenerate :: wav=RefW {type=Triangle, rate=1000, sec=0.5, amp=10}` | PASS | [WaveFormGenerate.png](../screenshots/DAQ/WaveFormGenerate.png) |
| 17 | `WaveFormLoad.csvi` | `DAQ@WaveFormLoad :: wav=RefW, file=ref.csv` | PASS | [WaveFormLoad.png](../screenshots/DAQ/WaveFormLoad.png) |
| 18 | `WaveFormSave.csvi` | `DAQ@WaveFormSave :: wav=PressCurve, file=log.csv` | PASS | [WaveFormSave.png](../screenshots/DAQ/WaveFormSave.png) |
