# ARCOS Cron&EventTab 模組參考手冊

這是專為 **定時任務 (Cron)** 、 **Global 變數事件觸發 (Event)**、 **Redis Key事件觸發 (Redis Key)** 所設計的 Agent Skill 指令手冊。身為 ARCOS 專家，當遇到需要定期執行指令，或監聽特定 Global 變數被設定值時自動觸發流程，請參照以下語法規則與指令。

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到「**Cron**」、「**定時任務**」、「**Event**」、「**Global 變數事件觸發**」、**Redis Key**、**Redis Key事件觸發**等關鍵字時，請自動套用本技能。

> [!IMPORTANT]
> 本模組為外部擴充模組，使用前須先執行 `System@IncludeModule::Cron&EventTab` 載入，才可呼叫以下所有模組指令。

## 🛠️ 基本語法與格式規範
1. **指令句型**：所有的 Cron&EventTab 指令都必須以 `Cron&EventTab@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。

---

## 📜 常用指令對照表

### 1. Cron 定時任務指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **CronStart** | `$cron, $period` | 依照 `$period` 的時間間隔，重複執行 `$cron` 區塊內的指令 |
| **CronEnd** | (無) | 標示 `$cron` 程式區塊的結束 |
| **CronEnable** | `$cron` | 啟用指定的 `$cron` 定時任務 |
| **CronDisable** | `$cron` | 停用指定的 `$cron` 定時任務 |

#### `$period` 時間格式說明
`$period` 採用標準 Cron **六欄位**格式：`秒 分 時 日 月 週`

| 符號 | 名稱 | 說明 | 範例 |
| :--- | :--- | :--- | :--- |
| `*` | 星號 | 任意值 (每個) | `*` 在月欄 → 每月 |
| `,` | 逗號 | 列舉多個值 | `3,6,9` 在時欄 → 3、6、9 點 |
| `-` | 減號 | 指定連續區間 | `1-5` 在週欄 → 週一至週五 |
| `/` | 斜線 | 指定間隔頻率 | `*/10` 在分欄 → 每 10 分鐘 |

#### `$period` 常用範例
| 說明 | `$period` 值 |
| :--- | :--- |
| 每分鐘執行 | `* * * * * *` |
| 每小時的第 5 分鐘 | `* 5 * * * *` |
| 每天凌晨 2 點 30 分 | `* 30 2 * * *` |
| 每週一早上 8 點 | `* 0 8 * * 1` |
| 每隔 10 分鐘 | `* */10 * * * *` |
| 每月 1 號與 15 號早上 6 點 | `* 0 6 1,15 * *` |
| 工作日 (週一至週五) 晚上 11 點 | `* 0 23 * * 1-5` |
> [!NOTE]
> 觸發的程式區塊以`CronTab#$cron`的**SubThread** 執行。

### 2. Global 變數事件觸發指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **EventStart** | `$event, $varpattern1(, $varpattern2, ...)` | 監聽符合 `$varpattern` 的 Global 變數；當該變數被設定值時，觸發執行 `$event` 區塊內的指令 |
| **EventEnd** | (無) | 標示 `$event` 程式區塊的結束 |
| **EventEnable** | `$event` | 啟用指定的 `$event` 事件監聽 |
| **EventDisable** | `$event` | 停用指定的 `$event` 事件監聽 |

#### `$varpattern` 萬用字元說明
| 符號 | 說明 | 範例 | 符合的變數名稱 |
| :--- | :--- | :--- | :--- |
| `*` | 比對任意數量的字元 | `abc*` | `abc`、`abc12`、`abcdef` ... |
| `?` | 比對單一任意字元 | `a?c` | `aac`、`abc`、`a1c` ... |

> [!NOTE]
> 觸發的程式區塊以`EventTab#$event`的**SubThread** 執行；觸發時，被設定的變數名稱與其值會自動傳入 SubThread 的區域變數 `KEY` 及 `VALUE` 中。

### 3. Redis Key事件觸發指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **RedisStart** | `$redis, $key@$host:$port,$user:$password` | 監聽符合 `$key` 的 Redis Key；當該變數被設定值時，觸發執行 `$redis` 區塊內的指令 |
| **RedisEnd** | (無) | 標示 `$redis` 程式區塊的結束 |
| **RedisEnable** | `$redis` | 啟用指定的 `$redis` 事件監聽 |
| **RedisDisable** | `$redis` | 停用指定的 `$redis` 事件監聽 |

#### 指定 `$key` 型態對應Redis事件指令
| 符號 | Redis對應的事件指令 |
| :--- | :--- | 
| `$key` | `PSubscribe $key` |
| `List:$key` | `BLPOP $key 0` |
| `Stream:$key` | `XREAD $key Block 0` |

> [!NOTE]
> 觸發的程式區塊以名為`RedisTab#$key`的**SubThread** 執行；觸發時，被設定的變數名稱與其值會自動傳入 SubThread 的區域變數 `KEY` 及 `VALUE` 中。