# ARCOSharp 全 14 模組驗證總覽報告（2026-11-22）

- **驗證日期**：2026-11-22
- **涵蓋模組**：14 個正式模組（共 **245** 個指令 CSVI）
- **測試程式目錄**：`verification/2026-11-22/tests/`
- **截圖目錄**：`verification/2026-11-22/screenshots/`（已產出 **256** 張 PNG）
- **回歸測試狀態**：`ModuleVerification` **45 / 45 PASSED**（0 Failed）

| # | 模組 | 指令數 | 驗證狀態 | Guide UI 截圖數 | 專屬報告 | 測試程式 |
| :-: | :--- | :-: | :-: | :-: | :--- | :--- |
| 01 | `SYSTEM` | 21 | PASS | 22 | [01_SYSTEM_VERIFICATION_REPORT.md](./01_SYSTEM_VERIFICATION_REPORT.md) | [SystemModuleVerificationTests.cs](../tests/SystemModuleVerificationTests.cs) |
| 02 | `THREAD` | 31 | PASS | 32 | [02_THREAD_VERIFICATION_REPORT.md](./02_THREAD_VERIFICATION_REPORT.md) | [ThreadModuleVerificationTests.cs](../tests/ThreadModuleVerificationTests.cs) |
| 03 | `VARIABLE` | 34 | PASS | 35 | [03_VARIABLE_VERIFICATION_REPORT.md](./03_VARIABLE_VERIFICATION_REPORT.md) | [VariableModuleVerificationTests.cs](../tests/VariableModuleVerificationTests.cs) |
| 04 | `IO` | 14 | PASS | 15 | [04_IO_VERIFICATION_REPORT.md](./04_IO_VERIFICATION_REPORT.md) | [IoModuleVerificationTests.cs](../tests/IoModuleVerificationTests.cs) |
| 05 | `MOTION` | 21 | PASS | 22 | [05_MOTION_VERIFICATION_REPORT.md](./05_MOTION_VERIFICATION_REPORT.md) | [MotionModuleVerificationTests.cs](../tests/MotionModuleVerificationTests.cs) |
| 06 | `CAPTURE` | 28 | PASS | 29 | [06_CAPTURE_VERIFICATION_REPORT.md](./06_CAPTURE_VERIFICATION_REPORT.md) | [CaptureModuleVerificationTests.cs](../tests/CaptureModuleVerificationTests.cs) |
| 07 | `DAQ` | 18 | PASS | 19 | [07_DAQ_VERIFICATION_REPORT.md](./07_DAQ_VERIFICATION_REPORT.md) | [DaqModuleVerificationTests.cs](../tests/DaqModuleVerificationTests.cs) |
| 08 | `POINT&PATH` | 22 | PASS | 23 | [08_POINT&PATH_VERIFICATION_REPORT.md](./08_POINT&PATH_VERIFICATION_REPORT.md) | [PointAndPathModuleVerificationTests.cs](../tests/PointAndPathModuleVerificationTests.cs) |
| 09 | `USER` | 21 | PASS | 22 | [09_USER_VERIFICATION_REPORT.md](./09_USER_VERIFICATION_REPORT.md) | [UserModuleVerificationTests.cs](../tests/UserModuleVerificationTests.cs) |
| 10 | `PYTHON` | 10 | PASS | 11 | [10_PYTHON_VERIFICATION_REPORT.md](./10_PYTHON_VERIFICATION_REPORT.md) | [PythonModuleVerificationTests.cs](../tests/PythonModuleVerificationTests.cs) |
| 11 | `REDIS` | 9 | PASS | 8 | [11_REDIS_VERIFICATION_REPORT.md](./11_REDIS_VERIFICATION_REPORT.md) | [RedisModuleVerificationTests.cs](../tests/RedisModuleVerificationTests.cs) |
| 12 | `MULTISTATION` | 12 | PASS | 13 | [12_MULTISTATION_VERIFICATION_REPORT.md](./12_MULTISTATION_VERIFICATION_REPORT.md) | [MultiStationModuleVerificationTests.cs](../tests/MultiStationModuleVerificationTests.cs) |
| 13 | `AIAGENT` | 3 | PASS | 4 | [13_AIAGENT_VERIFICATION_REPORT.md](./13_AIAGENT_VERIFICATION_REPORT.md) | [AiAgentModuleVerificationTests.cs](../tests/AiAgentModuleVerificationTests.cs) |
| 14 | `CALL` | 1 | PASS | 1 | [14_CALL_VERIFICATION_REPORT.md](./14_CALL_VERIFICATION_REPORT.md) | [CallModuleVerificationTests.cs](../tests/CallModuleVerificationTests.cs) |
