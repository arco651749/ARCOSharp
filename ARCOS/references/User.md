# ARCOS User 模組參考手冊

這是針對 **ARCOS User 模組** 專門設計的 Agent 技能指南。身為 ARCOS 自動化系統專家，當遇到需要控制使用者面板 (User Panel)、操作介面元件 (如按鈕、文字框、下拉選單) 或呼叫外部自訂 VI 時，請參照以下語法規則與指令。

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到「**使用者介面(UI)**」、「**面板/Panel**」、「**按鈕/文字框**」、「**畫面閃爍**」或需要與「**操作者互動**」等關鍵字時，請自動套用本技能。

## 🛠️ 基本語法與格式規範
1. **指令句型**：所有的 User 指令都必須以 `User@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。

---

## 📜 常用指令對照表

### 1. 外部 VI 呼叫與面板管理
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **CallVI** | `$vi(, $parameter.....)` | 呼叫外部自訂 VI (通常為客製化介面或副程式) |
| **CallVI(Test)**| `$vi(, $parameter.....)` | 呼叫外部 VI，測試用 |
| **CallVI(Reentrant)**| `$vi(, $parameter.....)` | 以可重複呼叫 (Reentrant) 的模式呼叫外部 VI |
| **CallVIWait** | `$vi` | 等待多個非同步 (Call & Forget) 執行的 VI 完成 |
| **VIClose** | `$vi` | 關閉目前的 VI 畫面，以利釋放資源或修改 VI 程式 |
| **Param2UserPanel**| (無) | 強制將目前的系統參數更新並顯示到使用者面板上 |

### 2. 按鈕操作 (Button Control)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **ButtonValue** | `$button, $logic` | 直接設定指定按鈕 `$button` 的邏輯狀態 (`ON` 或 `OFF`)，僅改變畫面顯示狀態，不觸發事件 |
| **ButtonTrigger**| `$button, $logic` | 程式化設定指定按鈕 `$button` 的邏輯狀態 (`ON` 或 `OFF`)，等同於使用者點擊按鈕，會觸發對應的變更事件 |
| **ButtonBlinking**| `$button, $logic` | 設定按鍵是否閃爍提示使用者 (`ON` 閃爍 / `OFF` 關閉) |
| **ButtonText** | `$button, $text` | 動態修改按鈕上顯示的文字內容 |

### 3. 文字框與數值輸入 (TextBox Control)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **TextBoxValue** | `$textbox, $text` | 直接寫入 `$text` 到指定的文字框 `$textbox` 中 (僅改變顯示文字，不觸發事件) |
| **TextBoxTrigger**| `$textbox, $text` | 程式化寫入 `$text` 到指定的文字框 `$textbox` 中，並等同於使用者輸入完成，會觸發值的變更事件 |
| **TextBoxBlinking**| `$textbox, $value` | 設定文字框是否閃爍亮起提示 |

### 4. 下拉選單/組合框 (TextRing) 與分頁控制 (Tab)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **TextRingValue** | `$textring, $text` | 設定組合框 (下拉選單) `$textring` 的當前值 |
| **TextRingTrigger**| `$textring` | 程式化觸發一次組合框的變更事件 |
| **TextRingBlinking**| `$textring, $value` | 設定組合框閃爍提示 |
| **TabValue** | `$tab, $value` | 直接切換頁面容器 `$tab` 的分頁 (`$value` 支援傳入索引數字或 `Tab Name` 名稱)，僅改變畫面狀態，不觸發事件 |
| **TabTrigger** | `$tab, $value` | 程式化切換頁面容器 `$tab` 的分頁 (`$value` 支援傳入索引數字或 `Tab Name` 名稱)，等同於使用者點擊，會觸發對應的值變更事件 |

---

## 💡 AI 撰寫範例參考

**範例：與操作者互動的動態介面更新**
當流程執行到某個階段時，我們需要更新面板上的數值、切換顯示特定分頁畫面，並讓對應的提醒區塊和按鈕閃爍，確保操作者不會忽略關鍵動作：

```markdown
// 1. 將所有最新參數同步更新至使用者面板
User@Param2UserPanel::;

// 2. 切換分頁控制器 (假設名為 MainTab) 到索引值 2 的結果頁面
User@TabValue::MainTab,2;

// 3. 更新特定文字框 (ResultBox) 的內容，並讓它閃爍
User@TextBoxValue::ResultBox,Process Completed!;
User@TextBoxBlinking::ResultBox,1;

// 4. 動態修改確認按鈕 (BtnConfirm) 的文字為 "請確認"，並啟動閃爍來吸引操作者點擊
User@ButtonText::BtnConfirm,請確認;
User@ButtonBlinking::BtnConfirm,1
```