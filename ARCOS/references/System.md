# ARCOS System 模組參考手冊

這是針對 **ARCOS System 模組** 專門設計的 Agent 技能指南。身為 ARCOS 自動化系統專家，當遇到需要記錄 Log 訊息、延遲等待 (Delay)、顯示對話框、存取系統參數、外掛模組載入或設定執行程式區段時，請參照以下語法規則與指令。

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到「**延遲/Delay**」、「**Log紀錄/報錯**」、「**顯示視窗**」、「**計時/計數**」或「**存取參數/載入模組**」等系統操作關鍵字時，請自動套用本技能。

## 🛠️ 基本語法與格式規範
1. **指令句型**：所有的 System 指令都必須以 `System@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。
3. **程式註解**：若為純註解，請使用 `#Comment----->>` 指令。

---

## 📜 常用指令對照表

### 1. 訊息提示與日誌記錄 (Logging & Messages)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **#Comment----->>** | `$message` | 寫入程式區段的註解文字 |
| **Log Message** | `$message` | 記錄一般系統訊息到 Log 欄位 |
| **Log Error** | `$message` | 發出錯誤 `$message`，並觸發系統錯誤處理 `EMG Stop` 程序 |
| **Clear Message** | (無) | 清除畫面上的 Log 訊息欄 |
| **Clear Error** | (無) | 清除畫面上的錯誤欄 |
| **MessageBox** | `$message` | 跳出系統對話框顯示 `$message` |
| **WarningBox** | `$logic, $message` | 若 `$logic` 狀態為 ON，將顯示 Warning 視窗以詢問使用者是否繼續往下執行 |
| **Record** | `$record, $message` | 記錄自定義訊息至檔案或特定紀錄器 |
| **LogRecordCycleTime** | `$record` | 記錄指定 `$record` 的 Cycle time (循環週期時間) |

### 2. 時間與流程控制 (Flow Control)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **Delay** | `$second` | 讓當前執行緒延遲指定的秒數 (單純等待) |
| **TimerReset** | `$timer, $second` | 初始化計時器 `$timer` 並設定計時秒數 |
| **TimerCheck** | `$timer` | 檢查計時器是否已超時。若超時，系統自動將 IO `$(timer)#Timeout?` 設為 On |
| **CounterReset** | `$counter, $times` | 初始化計數器 `$counter` 並設定計數總次數 |
| **CounterCheck** | `$counter` | 檢查計數器是否已歸零 (執行減 1)。若歸零，系統自動將 IO `$(counter)#Over?` 設為 On |
| **SystemPause** | (無) | 全域暫停 ARCOS 系統運作，並自動執行 `Pause On` 程式區 |
| **SystemResume** | (無) | 全域恢復 ARCOS 系統運作，並自動執行 `Pause Off` 程式區 |

### 3. 資源載入與參數儲存 (Parameters & External Loading)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **ProgramSection** | `$section, $name` | 設定當前程式區段的識別編號 `$section` 與其名稱 `$name` |
| **ProgramLoad** | `$file.pro` | 載入並切換至指定的 ARCOS 工程檔 (.pro) |
| **UserPanelOpen** | `$vi` | 載入並彈出指定的使用者自訂面板 (User Panel) VI 檔案 |
| **UserPanelCMD** | `$cmd` | 執行指定的使用者自訂面板 (User Panel) VI 指令，多個參數時，參數之間用逗號分隔 |
| **ParameterSet** | `$key, $value(, $section)` | 設定系統參數：可選擇將 `$key=$value` 存入指定 `$section` 變數區內 |
| **ParameterSave** | `$file` | 將目前參數設定備份至檔案內 |
| **ParameterLoad** | `$file` | 從檔案內讀取並覆寫參數設定 |

### 4. 擴充模組與驅動程式對接 (Modules & Drivers)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **IncludeModule** | `$module` | 從 `ExternalModules/$module` 目錄下載入指令模組 |
| **AddExternalDriver** | `$module, $drive` | 從 `ExternalModules/$module/$driver` 目錄下載入驅動程式 |
| **AddExternalDevice** | `$device, $driver, $parameter` | 以指定的驅動程式註冊並新增一個硬體作動設備 |

---

## 💡 AI 撰寫範例參考

**範例 1：基本訊息記錄與延遲控制**
當您需要讓動作緩衝時，可以插入 Delay 並顯示相關 Log：

```markdown
System@#Comment----->>::開始執行汽缸下壓動作;
System@Log Message::Cylinder is moving down;
System@Delay::0.5
```

**範例 2：宣告程式區段並透過 MessageBox 警告使用者**
定義一個叫做 "MainProcess" 的專屬程式區，並判斷若硬體未就位，跳出警告給使用者看：

```markdown
// 宣告目前的程式區段為第 1 區 "MainProcess"
System@ProgramSection::1,MainProcess;

// 當門沒關好 (門閂 IO logic L_DoorClose 為 OFF) 時，顯示警告框讓使用者確認是否要硬跑
System@WarningBox::L_DoorClose,The door is open! Do you want to continue running?
```

**範例 3：載入參數檔並讀取計時**
在每次機台初始化時，載入參數設置，並測量某段手動調整程式段的時間：

```markdown
// 載入機台預設參數檔
System@ParameterLoad::DefaultConfig.ini;

// 開始計時
System@TimerReset::ProcessTimer,10;

// ... (省略中間處理邏輯) ...

// 檢查計時器是否超時
System@TimerCheck::ProcessTimer
```