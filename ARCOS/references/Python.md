# ARCOS Python 模組參考手冊

這是專為 **Python** 整合設計的 Agent Skill 指令手冊。身為 ARCOS 專家，當任務涉及 `Python` 指令調用、數據交換或交握時，請參照以下規範與語法。

## 🎯 啟動條件與職責
當任務或討論符合以下條件時，應自動套用本技能：
- 明確提到「**ARCOS Python**」或「**Python 模組**」。
- 涉及 Python 指令調用、多 Session 作用域管理、數據交換或視覺/點位交握。

> [!CAUTION]
> **環境辨別**：本手冊適用於 **ARCOS 內部 Python 模組**。
> 本模組直接動態繫結使用者系統既有安裝之 Python 核心（優先使用 `python312.dll`），確保於 ARCOS 內執行之運算結果與使用者在終端機（CMD / Terminal）中執行完全一致，避免版本分歧與套件環境不一之問題。

> [!IMPORTANT]
> **載入模組與 Init.csvi 生命週期**：本模組為外部擴充功能，使用前必須先執行 System@IncludeModule::Python;。
> 執行 System@IncludeModule::Python; 時，系統會自動探測並調用 MODULES/PYTHON/Init.csvi 完成一次性初始化（動態繫結 python312.dll、切換工作目錄至 ./MODULES/PYTHON/、預熱 
umpy/cv2、並自動執行 init.py 預載自訂演算法或 AI 模型）。
> **環境要求與目錄規範**：
> - **Python 核心**：支援 Python 3.12（自動偵測 `C:\Python312`、系統 PATH、Local AppData 或 Program Files 之 `python312.dll`）。
> - **必要套件**：需安裝 `NumPy` 與 `OpenCV` (`cv2`)。
> - **預設工作目錄**：模組載入後，預設工作目錄與 `sys.path[0]` 一律指向 `./MODULES/PYTHON/`，絕不污染 Python 系統安裝目錄。所有相對路徑（腳本檔、存圖檔）皆以 `./MODULES/PYTHON/` 為基準。

---

## 🧩 多 Session 作用域隔離架構 (Multi-Session Architecture)

ARCOS 具備 13 個主執行緒（Thread 0~12）與多個動態 SubThread。為避免多執行緒共用全域變數造成變數污染與競態問題，Python 模組提供精確的 Session 隔離機制：

1. **執行緒隱式預設 Session (Thread-Implicit Default Session)**：
   - 當指令未明確指定 `Session` 參數時，系統會**自動以當前執行緒或 Section 名稱**作為 Session 鍵值（例如 `ThreadMain`、`Thread1`、`Thread2`...）。
   - 各執行緒擁有獨立的 Python 全域字典 (`PyDict`)，`Thread1` 中的 `a = 10` 與 `Thread2` 中的 `a = 20` 彼此隔離，互不干擾。
2. **顯式指定共用 Session (`Session=SessionName`)**：
   - 跨執行緒若需共用龐大模型（如 YOLO AI 權重或共用常數快取），可在各指令中帶入 `Session=VisionAI`，此時各執行緒將訪問同一個 Python 作用域。
3. **Session 記憶體常駐 (Session Persistence)**：
   - Session 作用域在 SubThread 結束後**不會自動銷毀或回收**，確保主流程可在子執行緒結束後讀取後續結果，並在後續週期重複利用已載入之模型。
4. **手動重置 Session (`Python@Reset`)**：
   - 省略 `{session=...}`：`Python@Reset` 重置當前預設 Session；
   - 指定名稱：`Python@Reset {session=VisionS1}` 僅重置該 Session；
   - 全部重置：`Python@Reset {session=ALL}`（或 `{session=*}`）一次清空所有 Session（底層相容舊名 `Python@ResetSession`）。

---

## 🛠️ 基本語法規範
1. **指令前綴**：所有指令均以 `Python@` 開頭。
2. **參數分隔**：採 ARCOS v2.0 四段式語法（`::` 必填、`{}` 選填、`->` 輸出、`[[Label]]` 錯誤攔截），同時向下相容舊式位置參數與分號結尾。

---

## 📜 全 10 條指令對照表 (v2.0)

| 指令 | v2.0 標準寫法 | 說明 |
| :--- | :--- | :--- |
| **Init** | `Python@Init {session=S1}` | 初始化 Python 執行環境並於存在時自動載入 `init.py`。 |
| **Reset** | `Python@Reset` / `{session=S1}` / `{session=ALL}` | 重置 Python Session 狀態（留空=預設 Session、指定名稱=該 Session、`ALL`/`*`=全部重置）。 |
| **Script** `[err]` | `Python@Script :: code="res = x * 2" {session=S1}` | 執行內嵌單行或多行 Python 程式碼。 |
| **File** | `Python@File :: file=calc.py {session=VisionS1}` | 執行外部 `.py` 腳本檔（相對路徑以 `MODULES/PYTHON/` 為基準）。 |
| **SetVariable** | `Python@SetVariable :: var=Count {val=10, py=py_cnt, session=S1}` | 將 ARCOS 變數或常數寫入 Python Session。 |
| **GetVariable** | `Python@GetVariable :: py=res {session=S1} -> var=Result` | 自 Python Session 讀取變數並回寫至 ARCOS `VariableEngine`。 |
| **SetPoint** | `Python@SetPoint :: pt=PickPos {py=p_pick, session=S1}` | 將 ARCOS 點位（X, Y, Z, U, V, W）送入 Python（`numpy.ndarray`）。 |
| **GetPoint** | `Python@GetPoint :: py=p_out {session=S1} -> pt=PlacePos` | 自 Python 讀取點位並回寫至 ARCOS `PointEngine`。 |
| **SetCapture** | `Python@SetCapture :: cam=Cam0 {py=img0, session=S1}` | 將 ARCOS `ImageEngine` 影像 (`OpenCvSharp.Mat`) 直接透過記憶體橋接至 Python (`numpy.ndarray`)。 |
| **GetCapture** | `Python@GetCapture :: py=out_img {session=S1} -> cam=CamOut` | 自 Python 取得處理後影像並顯式回寫至 ARCOS `ImageEngine` 指定 `cam` 槽位。 |

---

## 💡 使用範例 (Full Workflow)

### 範例 1：基本數值運算與多行腳本
```python
// 1. 載入 Python 擴充模組
System@IncludeModule::Python;

// 2. 將 ARCOS 變數 a 設定為 10
Variable@Set::a,10;
Python@SetVariable::a;

// 3. 執行 Python 多行運算腳本 (結尾加 ;)
Python@Script::b = 20
c = 30
d = a + b + c;

// 4. 從 Python 環境取得運算後的變數 d
Python@GetVariable::d,ARCOSName=ResultD;
```

### 範例 2：多執行緒 Session 隔離與跨執行緒共用
```python
// 於 Thread 1 執行 (自動使用 Thread1 獨立作用域)
Python@Script::temp_val = 100;

// 於 Thread 2 執行 (自動使用 Thread2 獨立作用域，不會覆蓋 Thread 1 的 temp_val)
Python@Script::temp_val = 999;

// 跨執行緒共用 AI 模型 (雙方皆指定 Session=VisionAI)
Python@Script::Session=VisionAI, shared_counter = 1;
Python@GetVariable::shared_counter,Session=VisionAI,ARCOSName=SharedCount;
```

### 範例 3：NumPy 點位座標運算
```python
// 1. 將 ARCOS 示教點 TeachPt 傳入 Python (變數名稱為 pt)
Python@SetPoint::TeachPt,PythonName=pt;

// 2. 在 Python 中透過 NumPy 進行座標平移與旋轉補償
Python@Script::pt[0] += 12.5
pt[1] -= 3.0
pt[2] = 50.0;

// 3. 將計算完的點位寫回 ARCOS 目標點 TargetPt
Python@GetPoint::pt,ARCOSName=TargetPt;
```

### 範例 4：OpenCV 影像處理與特徵萃取
```python
// 1. 將相機取像 CamTop 傳入 Python 為 cv2 影像 py_img
Python@SetCapture::CamTop,PythonName=py_img;

// 2. 呼叫 OpenCV 進行影像二值化或特徵繪製
Python@Script::import cv2
gray = cv2.cvtColor(py_img, cv2.COLOR_BGR2GRAY)
_, thresh = cv2.threshold(gray, 127, 255, cv2.THRESH_BINARY)
py_result = cv2.cvtColor(thresh, cv2.COLOR_GRAY2BGR);

// 3. 將處理後之 py_result 傳回 ARCOS 影像暫存區 CamInspect
Python@GetCapture::py_result,ARCOSName=CamInspect;
```
