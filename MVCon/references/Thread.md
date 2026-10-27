# MVCon Thread 模組參考手冊

這是針對 **MVCon Thread 模組** 專門設計的 Agent 技能指南。身為 MVCon 自動化系統專家，當遇到需要進行多線程控制、條件判斷跳躍、呼叫子程式、或者處理流程同步與卡控時，請參照以下語法規則與指令。

## 🎯 啟動條件與職責

當使用者的請求、任務或討論內容中明確提到「**流程控制**」、「**執行緒/Thread**」、「**SubThread**」、「**等待/卡控**」或「**錯誤處理(ErrorRetry)**」等關鍵字時，請自動套用本技能。

## 🛠️ 基本語法與格式規範

1. **指令句型**：所有的 Thread 指令都必須以 `Thread@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。
3. **條件執行**：若指令參數中包含 `$logic`，表示該指令為條件執行：只有當 `$logic` 的狀態為 ON (True) 時，指令才會被觸發執行。

---

## 📜 常用指令對照表

### 1. 程式流程控制與跳躍 (Flow Control & Jump)

| 指令                      | 參數範例                                                                                                     | 說明                                                            |
| :------------------------ | :----------------------------------------------------------------------------------------------------------- | :-------------------------------------------------------------- |
| **Label**           | `$label`                                                                                                   | 設定一個程式標記點 (Label)，供跳躍指令使用                      |
| **Jump**            | `$label(, $logic)` | 跳躍到指定的 `$label` (若有帶入 `$logic` 則條件成立才跳躍)                        |                                                                 |
| **Call**            | `$program(, $logic)`                                                                                       | 呼叫另一個程式區段當作副程式執行                                |
| **Return**          | `($logic)`                                                                                                 | 呼叫副程式後，使用此指令回到原`Call` 所在的下一個指令         |
| **Repeat**          | `($logic)`                                                                                                 | 將當前程式區段從頭開始重新執行一次                              |
| **Stop**            | `($logic, $ErrorMessage)` | 停止執行當前程式。若帶有 `$ErrorMessage`，停止時會發出錯誤紀錄               |                                                                 |
| **GoOn**            | `($logic)`                                                                                                 | 程式繼續執行                                                    |
| **RunNextCMD**      | `($logic)`                                                                                                 | 條件成立時，才執行下一行指令                                    |
| **SkipNextCMD**     | `($logic)`                                                                                                 | 條件成立時，跳過 (忽略) 下一行指令                              |
| **If**              | `$logic`                                                                                                   | 條件成立時，執行以下程式                                        |
| **ElseIf**          | `$logic`                                                                                                   | 條件成立時，執行以下程式                                        |
| **Else**            | `(無)`                                                                                                     | 其他條件都不成立時，執行以下程式                                |
| **EndIf**           | `(無)`                                                                                                     | If 區塊結束                                                     |
| **ForeachStart**    | `$label`                                                                                                   | `Foreach` 迴圈區塊開始                                        |
| **ForeachLoop**     | `$label, $Var1(, $Var2...)` | 當變數為陣列時，內部索引值加 1 並跳回 `$label`，直到所有元素皆讀取完畢為止 |                                                                 |
| **ForeachBreak**    | `$logic`                                                                                                   | 條件成立時，強制中斷並離開`Foreach` 迴圈區塊                  |
| **ForeachContinue** | `$logic`                                                                                                   | 條件成立時，直接進行下一次循環（跳到區塊結尾的`ForeachLoop`） |

### 2. 主執行緒 (Thread) 管理

| 指令              | 參數範例                                                                                    | 說明                                                                                  |
| :---------------- | :------------------------------------------------------------------------------------------ | :------------------------------------------------------------------------------------ |
| **Run**     | `$program, $thread` | 指派編號為 `$thread` 的執行緒 (0~12) 去運行 `$program` 程式區段 |                                                                                       |
| **Run**     | `$program, $thread, $index, $logic`                                                       | 條件式啟動執行緒，並可帶入一個 Index指定程式區段的第幾個指令或使用Label來指定跳轉位置 |
| **Wait**    | `$thread(, $thread2, ...)`                                                                | 等待指定的執行緒運行結束                                                              |
| **Suspend** | `$ThreadNo(, $logic)`                                                                     | 強制停止 (掛起) 指定的執行緒                                                          |
| **Resume**  | `$ThreadNo(, $logic)`                                                                     | 重新啟動被掛起的執行緒                                                                |

### 3. 子執行緒 (SubThread) 並行控制

> **注意**：子執行緒不佔用 13 個主執行緒的資源，通常用於區塊內部的非同步輔助任務 (如同時代亮指示燈、並行監測等)。
> $SubThreadName中，若最後沒有`&`或`&&`符號，代表該SubThread為單行程，同時只能有一個`$SubThreadName`在執行。 $SubThreadName中，若最後有`&`符號，代表該SubThread為獨立流程，不會受到`System@SystemPause`的影響, 會一直執行，否則會停止執行，直到`System@SystemResume`。 $SubThreadName中，若最後有`&&`符號，代表該SubThread為併發多行程，會同時啟動多個`$SubThreadName`+`$index`，常配合陣列型式變數(Array Variable)使用及imaqarray使用以並行處理多筆資料或圖片。

| 指令                       | 參數範例                                                                    | 說明                                                                                                     |
| :------------------------- | :-------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------- |
| **SubThreadStart**   | `$subthread(, $logic)` | 宣告並同時啟動名為 `$subthread` 的子執行緒區塊 |                                                                                                          |
| **SubThreadEnd**     | `$subthread` | 標記 `$subthread` 子執行緒區塊的結尾範圍                 |                                                                                                          |
| **SubThreadWait**    | `$subthread1(, $subthread2, ..., SKIPNOTFIND=1)`                          | 同步等待指定的子執行緒執行結束 (多個可用逗號隔開)，若未找到會跳錯誤訊息，但可以透過 SKIPNOTFIND=1 來忽略 |
| **SubThreadSuspend** | `$subthread`                                                              | 強制停止子執行緒的運行                                                                                   |
| **SubThreadScript**  | `$subthread, $file.init`                                                  | 開啟子執行緒，並直接讓它運行指定的`.init` 獨立程式檔                                                   |

### 4. 錯誤處理與資源上鎖 (Error Handling & Locks)

| 指令                       | 參數範例                                                                                                                  | 說明                                                                    |
| :------------------------- | :------------------------------------------------------------------------------------------------------------------------ | :---------------------------------------------------------------------- |
| **ErrorHandler**     | `[[$ErrorHandler]]`                                                                                                     | 功能和**Label**相同，但僅用於錯誤發生時、指定程式跳躍位置         |
| **ErrorRetry**       | `($logic, $offset)`                                                                                                     | 專門用於`ErrorHandler` 區塊內，使流程跳回原先發生錯誤的指令並重試一次 |
| **MutePopupMessage** | `(0 或 1)`                                                                                                              | 禁止程式的錯誤彈出視窗顯示 (`1`為禁止，`0`為恢復預設顯示)           |
| **KeySectionLock**   | `$lock, $timeout, $message` | 互斥資源鎖。確保同名的 `$lock` 區段在多個 Thread 中不會被同時進入。若等待解鎖超時則報錯 |                                                                         |
| **KeySectionUnLock** | `$lock`                                                                                                                 | 解除互斥資源鎖，允許其他等待中的 Thread 進入                            |

---

## 💡 AI 撰寫範例參考

**範例 1：條件式跳轉與程式重頭執行**
若邏輯變數為 True 則跳到指定 Label，若需要重試狀態，則讓整個區塊重頭執行：

```markdown
// 若邏輯 L_Skip 為 ON，跳到 SkipProcess 標籤
Thread@Jump::SkipProcess,L_Skip;

// (需要略過的動作)
System@Delay::1;

// 定義標籤點
Thread@Label::SkipProcess;

// 若邏輯 L_Retry 為 ON，則整個程式段重頭執行
Thread@Repeat::L_Retry
```

**範例 2：並行的子執行緒 (SubThread) 控制**
同時啟動兩個平行的子任務（如等待兩個不同的汽缸作動），並等待它們都完成：

```markdown
// 定義並啟動 Task1
Thread@SubThreadStart::Task1;
IO@SetPort::P0.0,0.5;
IO@WaitPortOn::P0.1,5,Cylinder 1 Timeout;
Thread@SubThreadEnd::Task1;

// 定義並啟動 Task2
Thread@SubThreadStart::Task2;
IO@SetPort::P0.2,0.5;
IO@WaitPortOn::P0.3,5,Cylinder 2 Timeout;
Thread@SubThreadEnd::Task2;

// 同步等待 Task1 與 Task2 都結束才繼續往下
Thread@SubThreadWait::Task1,Task2
```

**範例 3：多線程間的互斥鎖 (KeySectionLock / UnLock)**
當多個 Thread 同時有可能去呼叫共用硬體（例如同一把條碼掃描模組），確保同一時間內只有一個 Thread 可以發送掃描指令：

```markdown
// 鎖定名稱為 "ScannerLock" 的資源，等待超時時間設為 5 秒
Thread@KeySectionLock::ScannerLock, 5, Scanner is busy waiting timeout;

// --- 以下為獨佔使用的安全區段 (預設使用 Runtime 模式 &) ---
Call@TriggerBarcodeScanner.vi&::;
System@Delay::0.5;
// ---------------------------------

// 操作完畢，釋放資源供其他等待中的 Thread 使用
Thread@KeySectionUnLock::ScannerLock
```

**範例 4：陣列與 SubThread 併發處理**

此範例展示如何利用迴圈配合子執行緒處理陣列資料。

### A. 單行程循序處理 (Sequential)

在此模式下，每個陣列元素會依序執行（執行完一個才開始下一個），總耗時約為 `元素個數 * Delay 時間`。

```markdown
System@Record::TIME,Start;
Variable@Set::Data,[aaa;bbb;ccc;ddd;eee;fff;ggg];

Thread@ForeachStart::Loop;
    // 啟動單行程子執行緒 (無 &&)
    Thread@SubThreadStart::process;
    System@Log Message::$Data;
    System@Delay::2.0;
    Thread@SubThreadEnd::process;
Thread@ForeachLoop::Loop,Data;

// 等待子執行緒全部結束
Thread@SubThreadWait::process;

System@Record::TIME,End;
System@LogRecordCycleTime::TIME,Total time:
```

### B. 多行程併發處理 (Concurrent)

使用 `&&` 符號後，迴圈會迅速啟動所有元素的子執行緒並併行執行，總耗時約僅為 `Delay 時間`。

```markdown
System@Record::TIME,Start;
Variable@Set::Data,[aaa;bbb;ccc;ddd;eee;fff;ggg];

Thread@ForeachStart::Loop;
    // 啟動併發子執行緒 (帶有 &&)
    Thread@SubThreadStart::process&&;
    System@Log Message::$Data;
    System@Delay::2.0;
    Thread@SubThreadEnd::process&&;
Thread@ForeachLoop::Loop,Data;

// 同步等待所有 process&& 實例結束
Thread@SubThreadWait::process&&;

System@Record::TIME,End;
System@LogRecordCycleTime::TIME,Total time:
```
