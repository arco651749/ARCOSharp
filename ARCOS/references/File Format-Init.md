# ARCOS Init File Format (參考指南)

這是專為 **Init File** 所設計的 Agent Skill 指令手冊。當遇到需要處理或產生 ARCOS 儲存腳本（`.init`）時，請遵循此規範。

## 🎯 啟動條件與職責
當任務涉及 ARCOS 的腳本儲存功能、初始化設定或 `.init` 檔案讀寫時，自動套用此技能。

---

## 🛠️ 基本語法與格式規範

### 🎯 .init 檔案功能
- **特定應用**：主要用於儲存「初始化腳本」或「單一程式腳本」。
- **進階對照**：若為多個程式腳本的集合體，請參考 [File Format-Pro.md](File%20Format-Pro.md)。

### 📂 檔案路徑與命名
- **儲存位置**：`\Script` 資料夾（一般腳本）或 ARCOS 系統根目錄（系統級啟動腳本）。
- **副檔名**：`.init`

### 📄 二進制格式詳細規範
`Init File` 採用 **Binary (二進制)** 格式，非純文字。其核心結構為一個 **N × 2** 的字串陣列（第 2 維長度固定為 2）。

#### 1. 檔案標頭 (Header)
包含兩個 `INT32` 數值，定義陣列維度：
- **Row Count (N)**：腳本總列數。
- **Column Count (2)**：固定為 2。
- **範例**：10 × 2 的陣列標頭為 `0x0000000A` (10) + `0x00000002` (2)。

#### 2. 資料區塊 (Data Blocks)
遵循「長度標頭 + 文字內容」的規則連續儲存：
- **Length**：`INT32`，表示字串的位元組長度。
- **Content**：字串原始資料。

---

### 💡 實作範例解析

> [!TIP]
> 二進制封裝是將指令（Command）與參數（Parameter）拆開，分別存入同一列的兩個欄位中。

#### 範例 A：單行指令 (1 × 2 陣列)
**腳本邏輯內容：**
```text
IO@SetPort::P0.0
```
**二進制封裝順序：**
1. `0x00 00 00 01` (Row: 1)
2. `0x00 00 00 02` (Col: 2)
3. `0x00 00 00 0A` + `IO@SetPort` (長度 10)
4. `0x00 00 00 04` + `P0.0` (長度 4)

#### 範例 B：兩行指令 (2 × 2 陣列)
**腳本邏輯內容：**
```text
IO@SetPort::P0.0;
System@Delay::0.5
```
**二進制封裝順序：**
1. `0x00 00 00 02` (Row: 2)
2. `0x00 00 00 02` (Col: 2)
3. `0x00 00 00 0A` + `IO@SetPort` (10)
4. `0x00 00 00 04` + `P0.0` (4)
5. `0x00 00 00 0C` + `System@Delay` (12)
6. `0x00 00 00 03` + `0.5` (3)

#### 範例 C：三行指令 (3 × 2 陣列)
**腳本邏輯內容：**
```text
IO@SetPort::P0.0;
System@Delay::0.5;
Thread@Stop::
```
**二進制封裝順序：**
1. `0x00 00 00 03` (Row: 3)
2. `0x00 00 00 02` (Col: 2)
3. `0x00 00 00 0A` + `IO@SetPort` (10)
4. `0x00 00 00 04` + `P0.0` (4)
5. `0x00 00 00 0C` + `System@Delay` (12)
6. `0x00 00 00 03` + `0.5` (3)
7. `0x00 00 00 0B` + `Thread@Stop` (11)
8. `0x00 00 00 00` + (空字串)

---

## 🐍 Python 自動化生成工具 (`script_to_init.py`)

為了方便自動化生成 `.init` 檔案，系統提供了一個 Python 工具腳本：`scripts/script_to_init.py`。

### 使用方式
1. **載入函式**：從腳本中 import `create_init_file`。
2. **準備指令陣列**：使用 `(指令, 參數)` 元組 (Tuple) 的清單 (List)。
3. **執行生成**：指定目標路徑與指令清單。

### 💡 程式碼範例
```python
from script_to_init import create_init_file

# 定義要排入的腳本邏輯
commands = [
    ("IO@SetPort", "P0.0"),
    ("System@Delay", "0.5"),
    ("Thread@Stop", "")  # 若無參數，請帶入空字串
]

# 產生檔案
create_init_file(r"d:\Project\ARCOS2022\Script\test.init", commands)
```
