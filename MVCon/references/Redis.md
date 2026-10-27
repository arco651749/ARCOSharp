# MVCon Redis 模組參考手冊

這是專為 **Redis** 所設計的 Agent Skill 指令手冊。身為 MVCon 專家，當遇到需要使用`Redis`功能時，請參照以下語法規則與指令。

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到`Redis`，請自動套用本技能。

> [!IMPORTANT]
> 本模組為外部擴充模組，使用前須先執行 `System@IncludeModule::Redis` 載入，才可呼叫以下所有模組指令。
> 本模組會自動啟動本地的 `redis-server.exe` 並使用 `redis.conf` 作為組態檔啟動。請依照 `Redis Configure File` 內容設定 Redis 的功能。

## 🛠️ 基本語法與格式規範
1. **指令句型**：所有的 Redis 指令都必須以 `Redis@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。
3. **Redis 狀態**：`Redis Session 的 Server 狀態`會存在區域變數 `RedisErr#$sessionName` 中。
4. **Redis Session 的 Key 名稱**：
    - 預設以 `CreateSession` 的第一個參數 `$sessionName` 作為 Redis 中的 `Key` 名稱（區分大小寫）。
    - 也可透過 `$key@$host:$port` 語法，指定以 `$key` 作為 Redis 中的 `Key` 名稱（區分大小寫）。
5. **Redis 讀取值**：
    - 預設存入名為 `$sessionName` 的區域變數。（注意：`$key` 僅為 Redis 伺服器端的 Key 名稱，不影響本地變數的命名。）
    - 可使用 `$var@$section` 指定存入區域變數 `$var`。
    - 讀取值都是以文字型態存入（因為 MVCon 的區域變數都是以文字型態存入）。
6. **Redis 通用參數**：
    - `TimeOut=$second`：(選填) 設定取得資料的超時時間（秒）。
    - `Message=[[$label]]$message`：(選填) 超時則顯示 `$message` 並跳轉至 `[[$label]]`。 
---

## 📜 常用指令對照表

### 1. Redis Session 指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **CreateSession** | `$sessionName, $host:$port(, $user:$password)` | 建立 Redis Session。預設以 `$sessionName` 作為 Redis 中的 `Key` 名稱（區分大小寫）。 |
| | `$sessionName, $key@$host:$port(, $user:$password)` | 建立 Redis Session。指定以 `$key` 作為 Redis 中的 `Key` 名稱（區分大小寫）。 |
| **CMD** | `$sessionName, $cmd` | 直接傳送原始 Redis 語法指令（`$cmd`）至指定的 Redis Session 執行。 |
| **Set** | `$sessionName, $value` | 設定指定 Redis Session 的 `Key` 值。 |
| **Get** | `$sessionName` | 從 Redis 取得值並存入區域變數 `$sessionName`。 |
| **Get** | `$sessionName, $var@$section` | 從 Redis 取得值並存入 `$section` 的區域變數 `$var`。 |
| **Publish** | `$sessionName, $value` | 發布訊息至指定的 Redis Session。可用於多執行緒或多台機器的訊息傳遞。 |
| **Subscribe** | `$sessionName` | 訂閱指定的 Redis Session。可用於多執行緒或多台機器的訊息接收。 |

### 2. Redis List 指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **LPush** | `$sessionName, $value(, Dir={Start,End})` | Dir=Start 將 `$value` 加入 List 頭部，Dir=End 將 `$value` 加入 List 尾部。預設為 End。 |
| **LPop** | `$sessionName(, Dir={Start,End})` | Dir=Start 從 List 頭部移除並取得值，Dir=End 從 List 尾部移除並取得值。預設為 End。 |
| **BLPop** | `$sessionName(, Dir={Start,End})` | Dir=Start 阻塞式移除 List 頭部元素並取得值，Dir=End 阻塞式移除 List 尾部元素並取得值。預設為 End。 |