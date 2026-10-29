# ARCOS Python 模組參考手冊

這是專為 **Python** 整合設計的 Agent Skill 指令手冊。身為 ARCOS 專家，當任務涉及 `Python` 指令調用、數據交換或交握時，請參照以下範例與規範。

## 🎯 啟動條件與職責
當任務或討論符合以下條件時，應自動套用本技能：
- 明確提到「**ARCOS Python**」或「**Python 模組**」。
- 涉及 Python 指令調用、數據交換或系統交握。

> [!CAUTION]
> **環境辨別**：本手冊僅適用於 **ARCOS 內部 Python 模組**。
> 這並非調用系統全域的 `python.exe` 或 AI Agent 本身的執行環境，而是透過 `python310.dll` 運作於 ARCOS 框架下的整合功能。

> [!IMPORTANT]
> **載入模組**：本模組為外部擴充功能，使用前必須先執行 `System@IncludeModule::Python`。
> **環境要求**：
> - Python 核心：需安裝於 `C:\Python310`（使用 `python310.dll`）。
> - 必要套件：必須安裝 `NumPy`。
> - 工作目錄為 `./Python`。

## 🛠️ 基本語法規範
1. **指令前綴**：所有指令均以 `Python@` 開頭。
2. **參數分隔**：
   - 指令與第一個參數間使用 `::` 分隔。
   - 多個參數之間使用逗號 `,` 隔開。
3. **語法結構**：`Python@指令名稱::參數1,參數2,...`

---

## 📜 常用指令對照表

### 1. Python Session 指令
| 指令 | 參數 | 說明 |
| :--- | :--- | :--- |
| **Script** | `$script` | 執行 Python Script。Script每一行用newline character(0x0A)分隔，最後一行加';'來符合ARCOS的語法。 |
| **File** | `$file.py` | 執行 Python Script。 |
| **SetVariable** | `$var(, $section)(, PythonName=$pyvar)` 或 `$var, $value` | 使 ARCOS 變數設定到 Python 變數，亦可直接給值同步。 |
| **GetVariable** | `$pyvar(, $section)(, ARCOSName=$var)` | 取得 Python 變數的值並存入 ARCOS 變數。 |
| **SetPoint** | `$point(, PythonName=$pyvar)` | 將 ARCOS 的 $Point 設定到 Python 的變數中，使用 numpyArray 格式。 |
| **GetPoint** | `$pyvar(, ARCOSName=$point)` | 取得 Python 的 numpyArray 格式存在 ARCOS 的 $point。 |
| **SetCapture** | `$capture(, PythonName=$pyvar)` | 將 ARCOS 的 $Capture 設定到 Python 的變數中，使用 opencv 格式。 |
| **GetCapture** | `$pyvar(, ARCOSName=$capture)` | 取得 Python 的 opencv 格式存在 ARCOS 的 $capture。 |
| **SetCaptureFile** | `$capture, $file.png, $pyvar` | 將 ARCOS 的 $Capture 中的圖像保存到文件中，設定到 Python 的變數中，使用 opencv 格式。 |
### 2. 使用範例 (Full Workflow)
以下展示如何在腳本中載入 Python 模組、同步變數、執行一段運算腳本並取得結果。

```python
// 1. 載入 Python 擴充模組
System@IncludeModule::Python;

// 2. 將 ARCOS 變數 a 設定為 1 並同步至 Python
Python@SetVariable::a,1;

// 3. 執行 Python 多行腳本 (注意：ARCOS 中需以 newline character (0x0A)  換行，結尾加 ;)
Python@Script::b=2
c=3
d=a+b+c;

// 4. 從 Python 環境取得運算後的變數 d
Python@GetVariable::d
```