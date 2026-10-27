# MVCon Pro File Format (參考指南)

這是專為 **Pro File** 所設計的 Agent Skill 指令手冊。當遇到需要處理或產生 MVCon 專案腳本（`.pro`）時，請遵循此規範。

## 🎯 啟動條件與職責
當任務涉及 MVCon 的程式腳本的集合體（`.pro`）儲存功能、專案配置或 `.pro` 檔案讀寫時，自動套用此技能。

---

## 🛠️ 基本語法與格式規範

### 🎯 .pro 檔案功能
- **特定應用**：作為「工程專案檔」，是多個程式腳本（Program Sections）的集合體。
- **進階對照**：若僅需儲存「單一程式腳本」，請參考 [File Format-Init.md](File%20Format-Init.md)。

### 📂 檔案路徑與命名
- **儲存位置**：MVCon 系統根目錄。
- **檔案副檔名**：`.pro`
- **檔案結構**：採用標準 Windows **INI** 格式。

---

### 💡 實作範例解析
一個標準的 `.pro` 專案檔預設包含 100 個程式區塊（編號 0~99）。

#### 特殊編號與功能對照表
| 編號 | 功能名稱 | 觸發條件 / 用途 |
| :--- | :--- | :--- |
| **0** | **Main Program** | 建議作為 Manager & Worker 架構中的 **Manager** 主程式。 |
| **94** | **Pause Off** | 當 MVCon UI 釋放 `Pause` 按鈕時觸發。 |
| **95** | **Pause On** | 當 MVCon UI 按下 `Pause` 按鈕時觸發。 |
| **96** | **Execute On** | 當 MVCon UI 按下 `Run` 按鈕時觸發。 |
| **97** | **Execute Off** | 當 MVCon UI 釋放 `Run` 按鈕時觸發。 |
| **98** | **End Script** | 當程式執行結束或主流程終止時觸發。 |
| **99** | **EMG Stop** | 當 MVCon UI 按下 `!! Stop !!` 紅色按鈕時觸發。 |

> [!IMPORTANT]
> **執行環境注意事項**
> - 特殊編號程式 (94-99) 皆以 **SubThread** 形式獨立執行，以避免阻塞主執行緒。
> - **強烈建議**：不要在這些區塊中使用 `Thread@Run` 或 `Thread@Suspend` 等執行緒控制指令。

#### 程式區名稱自定義
- 使用 `System@ProgramSection` 指令可動態變更特定編號的顯示名稱。

---

### 📄 檔案結構範例 (INI 形式)
以下展示 `.pro` 檔案的關鍵結構。為了節省篇幅，中間重複的空區塊（1~93）已省略。

```ini
[Program 0]
Name="MainManager"
Type="0"
Size="2"
1="IO@SetPort	P0.0"
2="System@Delay	1.0"

; ... 中間 Program 1 ~ 93 依此類推 ...

[Program 94]
Name="ResumeHandler"
Type="0"
Size="1"
1="System@SystemResume"

[Program 95]
Name="PauseHandler"
Type="0"
Size="1"
1="System@SystemPause"

[Program 96]
Name="RunStart"
Type="0"
Size="1"
1="Thread@Run	0,0"

[Program 97]
Name="RunStop"
Type="0"
Size="1"
1="Thread@Suspend	0"

[Program 98]
Name="EndScriptHandler"
Type="0"
Size="1"
1="System@Log Message	Program Completed"

[Program 99]
Name="EmergencyHandler"
Type="0"
Size="1"
1="Motion@Stop"
```

> [!TIP]
> **INI 數值說明**：
> - `Name`：區塊名稱（字串）。
> - `Type`：通常固定為 "0"（腳本類型）。
> - `Size`：代表該區塊內的指令行數。
> - `1`, `2`...：指令內容，指令與參數之間通常以 **Tab** 符號分隔。

```
