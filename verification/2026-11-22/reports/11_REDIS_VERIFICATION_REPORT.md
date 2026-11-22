# 模組驗證報告 11：`REDIS`（9 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/REDIS/*.csvi`
- **測試程式**：[RedisModuleVerificationTests.cs](../tests/RedisModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/REDIS/`（目前已產出 8 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 9 / 9 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `BLPop.csvi` | `REDIS@BLPop` | PASS | [BLPop.png](../screenshots/REDIS/BLPop.png) |
| 2 | `CMD.csvi` | `Redis@CMD :: cmd=PING {session=Line1}` | PASS | [CMD.png](../screenshots/REDIS/CMD.png) |
| 3 | `CreateSession.csvi` | `Redis@CreateSession :: session=Line1 {url=192.168.1.10:6379}` | PASS | [CreateSession.png](../screenshots/REDIS/CreateSession.png) |
| 4 | `Get.csvi` | `Redis@Get :: key=LotId {session=MES} -> var=CurLot` | PASS | [Get.png](../screenshots/REDIS/Get.png) |
| 5 | `LPop.csvi` | `Redis@LPop :: key=JobQ {session=Line1} -> var=NextJob` | PASS | [LPop.png](../screenshots/REDIS/LPop.png) |
| 6 | `LPush.csvi` | `Redis@LPush :: key=JobQ, val=Part001 {session=Line1}` | PASS | [LPush.png](../screenshots/REDIS/LPush.png) |
| 7 | `Publish.csvi` | `Redis@Publish :: key=LineEvt, val=CycleDone {session=Line1}` | PASS | [Publish.png](../screenshots/REDIS/Publish.png) |
| 8 | `Set.csvi` | `Redis@Set :: key=LotId, val=LOT2026 {session=MES}` | PASS | `Set.png` (截圖排程中) |
| 9 | `Subscribe.csvi` | `Redis@Subscribe :: key=LineEvt {session=Line1}` | PASS | `Subscribe.png` (截圖排程中) |
