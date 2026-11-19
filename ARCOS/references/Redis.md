# ARCOS Redis 模組參考手冊 (v2.0)

本手冊涵蓋 **Redis** 模組全 9 條指令的 **ARCOS Command Syntax v2.0** 規範（同時 100% 向下相容舊版位置參數語法）。

> [!IMPORTANT]
> 本模組為外部擴充模組，使用前須先執行 `System@IncludeModule :: module=Redis` 載入。

## 🛠️ 核心規範 (v2.0)

1. **多 Session 隔離與自動預設 Session**：
   - 第一次執行 `Redis@CreateSession :: session=<Name> {endpoint=<host:port>}` 所建立的 Session，會自動成為**預設 Session**。
   - 其餘所有指令（`Set`、`Get`、`LPush`、`LPop`、`BLPop`、`Publish`、`Subscribe`、`CMD`）皆支援選填 `{session=<Name>}`；省略時自動套用預設 Session。
   - 不同 Session 之間的 KV 與 List 命名空間完全隔離；連線狀態寫入全域變數 `RedisErr#$(session)`（成功為 `OK`）。
2. **統一參數命名**：
   - 資料鍵與 Pub/Sub 頻道統一使用 **`key`**（不再使用 `channel`）。
   - 寫入內容與 Pub/Sub 訊息統一使用 **`val`**（不再使用 `msg`）。
   - 讀出結果統一置於 **`-> var=<目標變數>`**（留空時預設寫入與 `key` 同名之變數）。

---

## 📜 全 9 條指令對照表

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

## 💡 多 Session 實戰範例

```arcos
// 建立預設 Session (Line1) 與第二個隔離 Session (MES)
Redis@CreateSession :: session=Line1 {endpoint=127.0.0.1:6379}
Redis@CreateSession :: session=MES {endpoint=192.168.1.20:6379}

// 寫入與讀取（預設 Session 可省略 {session=...}）
Redis@Set :: key=LotId, val=LOT_LINE1
Redis@Set :: key=LotId, val=LOT_MES {session=MES}
Redis@Get :: key=LotId -> var=CurLineLot
Redis@Get :: key=LotId {session=MES} -> var=CurMesLot

// Pub/Sub 跨站通知
Redis@Publish :: key=LineEvt, val=CycleDone
Redis@Subscribe :: key=LineEvt {session=MES}
```
