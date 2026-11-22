# 模組驗證報告 13：`AIAGENT`（3 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/AIAGENT/*.csvi`
- **測試程式**：[AiAgentModuleVerificationTests.cs](../tests/AiAgentModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/AIAGENT/`（目前已產出 4 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 3 / 3 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `ErrorHandle.csvi` | `AIAGENT@ErrorHandle` | PASS | [ErrorHandle.png](../screenshots/AIAGENT/ErrorHandle.png) |
| 2 | `SuperviseEnd.csvi` | `AIAGENT@SuperviseEnd` | PASS | [SuperviseEnd.png](../screenshots/AIAGENT/SuperviseEnd.png) |
| 3 | `SuperviseStart.csvi` | `AIAgent@SuperviseStart :: prompt=InspectPart {bypass=true}` | PASS | [SuperviseStart.png](../screenshots/AIAGENT/SuperviseStart.png) |
