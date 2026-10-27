# 🛠️ MVCon User Front Panel 模組技能手冊

這是針對 **MVCon User Front Panel 模組** 專門設計的 Agent 技能指南。當需要設計或控制使用者介面時，請遵循以下規範。

---

## 🎯 啟動條件與核心職責

當任務涉及以下關鍵字時，自動套用本技能：
- **使用者介面**、**User Front Panel**、**設計 UI**、**面板開發**。

---

## 🏗️ UI 設計基礎 (LabVIEW)

1.  **檔案定位**：將 LabVIEW 製作的 UI 面板 (`VI`) 放置於 `MVCon 根目錄`。
2.  **編輯規範**：
    *   開啟範本 `UserFrontPanel-UIBinding.vi` 進行編輯，完成後**另存新檔**為機台專屬的 `VI` 名稱。
    *   可放置：`IO Control`、`IO Indicator`、`Combo Box`、`String Indicator`、`Image Display`、`Waveform Graph`、`Tab Control` 等。
    *   **核心邏輯**：系統透過控制項的 **Label (標籤)** 進行指令綁定，請確保 Label 命名具唯一性。
3.  **按鈕行為設定 (Mechanical Action)**：
    *   在控制項上按右鍵 -> **`Properties` -> `Operation` -> `Mechanical action`**。
    *   常用設定：`Switch when press` (Latch) 或 `Switch when release` (Not Latch)。

---

## 📜 核心指令集

| 指令類別 | 指令語法 | 說明 |
| :--- | :--- | :--- |
| **載入/顯示** | `System@UserPanelOpen::<vi>` | 載入指定的 UI (VI)。修改 VI 後可重新載入以除錯。 |
| **視窗控制** | `System@UserPanelOpen::MaxUserPanel` | 將 User Panel 視窗最大化。 |
| **功能綁定** | `System@UserPanelCMD::UIBinding,$controlLabel,$function[, $params]` | 將 UI 控制項與特定功能連結，支援選用參數。 |
| **Capture 顯示** | `Capture@Display2User` | 更新 UI 中的 **Image Display** 圖片。 |
| **DAQ 顯示** | `DAQ@Display2User` | 更新 UI 中的 **Waveform Graph** 圖表。 |

> [!IMPORTANT]
> **重新載入提醒**：重新載入 VI 後，必須重新執行 `UIBinding` 指令才能恢復功能連結。

---

## 🔗 綁定功能 (`$function`) 詳解

### 1. IO 顯示與控制
用於直接與硬體或系統 IO 狀態對接：
*   **`IOShow`**：顯示指定 IO 的目前狀態。
*   **`IOSwitch`**：手動控制 IO 開關切換。
*   *參數註記：預設自動連結與 `$controlLabel` 同名的 IO，或於 `$param` 指定特定 IO 名稱。*

### 2. 腳本觸發功能 (`.init`)
用於按鈕操作時自動執行一系列腳本指令。所有的腳本皆以對應名稱的 **`SubThread`** 模式獨立執行。

*   **`OnOff`**：狀態切換觸發。
    *   **支援模式**：`Latch` 或 `Not Latch`。
    *   **觸發規則**：
        - 切換至 On 時執行 `$controlLabel-On.init`。
        - 切換至 Off 時執行 `$controlLabel-Off.init`。
*   **`Trigger`**：單次點擊觸發。
    *   **支援模式**：限 `Latch`。
    *   **執行邏輯**：點擊後執行 `$controlLabel.init`。觸發時會將控制項設為 On，待腳本執行完成後自動切回 Off，才可進行下一次觸發。

### 3. 變數與分頁控制
| 控制項 | $function | 說明 |
| :--- | :--- | :--- |
| **Combo Box** | `VariableSetting` | 將選擇內容寫入同名 `Variable`，或於參數指定目標變數。 |
| **String Indicator**| `VariableShow` | 顯示指定 `Variable` 的內容。 |
| **String Indicator**| `VariableShowNum` | 將變數視為數值顯示，支援格式化參數（如 `Format=%07.3f`）。 |
| **Tab Control** | `TabChange` | 切換時將分頁名稱寫入 Global `Tab` 變數，並觸發 `$controlLabel-TabChange.init`。其腳本同樣以 **`SubThread`** 模式獨立執行。 |

---

## 🤖 自動化綁定 (Tip Strip)

利用 LabVIEW 控制項屬性內建綁定資訊，可極大簡化腳本維護：

*   **設定方式**：在 LabVIEW 中對控制項按右鍵 -> **`Properties` -> `Documentation` -> `Tip strip`**。
*   **填寫規則**：在欄位中直接輸入 `$function` 及 `$params`（例如：`IOSwitch,IO1`）。
*   **指令調用**：使用 **`Auto`** 關鍵字。
    > [!TIP]
    > 使用 `Auto` 時，系統會使用正規表示式匹配指定的控制項，並自動提取其 `Tip strip` 內的設定完成綁定。

---

## 📝 綜合範例

```markdown
// 1. 綁定 IO 開關：手動指定目標 IO1
System@UserPanelCMD::UIBinding,Btn_Power,IOSwitch,IO1;

// 2. 綁定 腳本觸發：單次執行控制項同名腳本 (.init)
System@UserPanelCMD::UIBinding,Btn_Start,Trigger;

// 3. 綁定 數值顯示：從變數 V_Read 讀取並以指定格式顯示
System@UserPanelCMD::UIBinding,Txt_Voltage,VariableShowNum,V_Read,Format=%05.2f;

// 4. 分頁自動切換腳本：分頁名稱標籤為 Tab
System@UserPanelCMD::UIBinding,Tab,TabChange;

// 5. 全自動綁定：遍歷所有具備 Tip strip 設定的控制項
System@UserPanelCMD::UIBinding,.*,Auto
```

---

## 💡 開發提示
- MVCon 中的 **`Variable`** 統一以「字串型態」儲存，顯示時可透過 `VariableShowNum` 轉換為數值格式。
- 確保 `$controlLabel` 具有唯一性，並與對應的 `.init` 腳本名稱精確匹配。
