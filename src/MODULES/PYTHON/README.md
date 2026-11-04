# ARCOS MODULE/PYTHON 模組手冊：架構精神、指令 API、數據交換與開發維護規範

本文件為 `src/MODULES/PYTHON/` 之核心規範文件，說明 Python 模組的**設計精神**、**隨選載入與 Init.csvi 生命週期**、**多 Session 作用域常駐架構**、**全指令函式簽章**、**API 數據結構對照**以及**後續修改防錯注意事項 (Gotchas)**。

---

## 目錄
1. [一、 核心設計精神與哲學 (Core Philosophy)](#一-核心設計精神與哲學)
2. [二、 隨選載入與 Init.csvi 生命週期機制 (On-Demand Loading & Init.csvi)](#二-隨選載入與-initcsvi-生命週期機制)
3. [三、 多 Session 作用域常駐架構 (Multi-Session Architecture)](#三-多-session-作用域常駐架構)
4. [四、 完整指令規格清單 (Command Specifications)](#四-完整指令規格清單)
5. [五、 API 與數據結構映射規範 (API & Data Structure Mapping)](#五-api-與數據結構映射規範)
6. [六、 後續修改與維護注意事項 (Developer Gotchas & Precautions)](#六-後續修改與維護注意事項)

---

## 一、 核心設計精神與哲學

### 1. 零環境分歧原則 (Zero Discrepancy Principle)
- **直接繫結使用者既有 Python 3.12 核心**：
  ARCOS **絕不**內建封裝一套容易產生相容性問題的閹割版 Python，而是直接以動態原生 C-API（P/Invoke）載入使用者系統安裝的 `python312.dll`（自動搜尋 `C:\Python312`、系統 `PATH`、`LocalAppData`、`ProgramFiles`）。
- **保證 CMD 與 ARCOS 執行一致**：
  使用者在終端機（CMD / PowerShell）用 `python.exe test.py` 測試通過的腳本、模型及第三方套件（如 `numpy`、`cv2`、`torch`、`ultralytics`），在 ARCOS 的 Python 模組中執行時保證 **100% 完全相同**，徹底杜絕兩邊運行結果不一致引發的客訴與除錯困難。

### 2. 開機零延遲與隨選載入 (Zero Boot Overhead)
- **開機時 100% 不載入 Python**：
  ARCOSharp 主程式開機時，**嚴禁**載入 `python312.dll` 或建立 Python 執行環境。對於 90% 純 PLC / IO / 運動控制的機台，ARCOS 開機維持 0 毫秒額外負擔、0 記憶體浪費。
- **僅在配方需要時動態啟動**：
  只有當腳本明確調用 `System@IncludeModule::Python;` 時，系統才動態將 Python 模組掛載至排程器並調用 `Init.csvi`。

### 3. 工作目錄純淨導向 (Clean Working Directory Isolation)
- **預設目錄指到 `MODULES/PYTHON/`**：
  Python 模組載入後，工作目錄 (`os.chdir`) 與 `sys.path[0]` 一律強制設定在 `./MODULES/PYTHON/`，**嚴禁指向 Python 安裝目錄 (`C:\Python312`) 或 ARCOS 根目錄**。
- **相對路徑歸屬**：
  所有外部腳本檔案（`Python@File::algo.py`）與存圖檔案（`Python@SetCaptureFile`）若為相對路徑，皆以 `MODULES/PYTHON/` 為基準，防止運算暫存檔案污染整個系統。

### 4. GIL 全域執行緒安全防護 (GIL Thread-Safety Guard)
- **多執行緒安全調度**：
  模組於初始化時立即調用 `PyEval_SaveThread()` 釋放初始執行緒鎖；後續所有跨執行緒執行一律透過 C# `lock (GilLock)` 與 Python C-API 的 `PyGILState_Ensure()` / `PyGILState_Release()` 雙重防護，徹底排除死鎖（Deadlock）與並行存取衝突。

### 5. 無依賴安全回退機制 (Zero-Dependency Mock Fallback)
- 當系統未安裝 Python 3.12 時，模組會自動安全降級為記憶體 Mock 快取模式，維持單元測試與無 Python 機台之無崩潰運作。

---

## 二、 隨選載入與 Init.csvi 生命週期機制

為符合工業自動化的高效與自給自足原則，ARCOS 實作了基於 `Init.csvi` 的擴充模組標準生命週期協議：

```
[ARCOSharp 開機啟動]
       │
       └── 🚫 完全不載入 Python (Registry 無 Python, 0ms 延遲, 0 記憶體開銷)

[配方腳本執行]
       │
       ▼
System@IncludeModule::Python;
       │
       ├── 1. 動態載入 MODULES/PYTHON/Python.Module.dll 並註冊至 ModuleRegistry
       │
       └── 2. 自動探測並執行 MODULES/PYTHON/Init.csvi (單次生命週期初始化)：
                 │
                 ├── ① 動態載入本機 python312.dll，呼叫 Py_Initialize()
                 ├── ② 呼叫 PyEval_SaveThread() 釋放初始 GIL，讓背景多執行緒自由調度
                 ├── ③ os.chdir("MODULES/PYTHON") & sys.path.insert(0, ...)
                 ├── ④ 預熱匯入 numpy, cv2, json, base64，消除後續指令冷啟動延遲
                 └── ⑤ 自動偵測並執行 MODULES/PYTHON/init.py (若存在，預載自訂模型/函式)
       │
       ▼
[初始化完成] 後續 Python@Script、SetVariable、SetCapture 全速執行！
```

### 模組重複載入之 Release-then-Reload 重置規範 (比照驅動 AddDevice 慣例)：
在 ARCOS 架構中，`IO@AddIODriver`、`Motion@AddMotion`、`Capture@AddCam` 與 `DAQ@AddDAQ` 具備一項統一設計哲學：**「若裝置名稱已存在，先 Release 既有驅動實例 (Shutdown & Dispose)，再重新載入 Init」**。
`System@IncludeModule::Python;` 完全遵循這項哲學：
- 當系統初次調用 `IncludeModule::Python;` 時，隨選載入 DLL 並觸發 `Init.csvi`。
- **若系統已載入 Python 模組**，再次執行 `IncludeModule::Python;` 將自動執行：
  1. **Release (Reset)**：調用既有實例之 `Reset()` / `Dispose()`，清空所有執行緒之 Session 字典池 (`SessionScopes`)、釋放全域變數與 Mock 快取，並自 `ModuleRegistry` 解除舊 Handler 實例。
  2. **Reload**：自 Assembly 實例化全新乾淨的 `PythonModuleHandler`。
  3. **Init**：重新觸發 `Init.csvi` 與 `init.py`，完成出廠級乾淨重啟（Clean Hot Reload）。

### 模組重複載入之 Release-then-Reload 重置規範 (比照驅動 AddDevice 慣例)：
在 ARCOS 架構中，`IO@AddIODriver`、`Motion@AddMotion`、`Capture@AddCam` 與 `DAQ@AddDAQ` 具備一項統一設計哲學：**「若裝置名稱已存在，先 Release 既有驅動實例 (Shutdown & Dispose)，再重新載入 Init」**。
`System@IncludeModule::Python;` 完全遵循這項哲學：
- 當系統初次調用 `IncludeModule::Python;` 時，隨選載入 DLL 並觸發 `Init.csvi`。
- **若系統已載入 Python 模組**，再次執行 `IncludeModule::Python;` 將自動執行：
  1. **Release (Reset)**：調用既有實例之 `Reset()` / `Dispose()`，清空所有執行緒之 Session 字典池 (`SessionScopes`)、釋放全域變數與 Mock 快取，並自 `ModuleRegistry` 解除舊 Handler 實例。
  2. **Reload**：自 Assembly 實例化全新乾淨的 `PythonModuleHandler`。
  3. **Init**：重新觸發 `Init.csvi` 與 `init.py`，完成出廠級乾淨重啟（Clean Hot Reload）。

### `Init.csvi` 的五大核心功能與職責：
1. **動態原生繫結**：探測並裝載本機 `python312.dll`，完成 C-API 函式指標繫結。
2. **工作目錄鎖定**：切換進程目錄與 Python 路徑至 `./MODULES/PYTHON/` 沙盒環境。
3. **常用套件預熱**：預先載入 `numpy` 與 `cv2`，避免在時間敏感的檢測指令（Snap/Capture）中遭遇初次 import 延遲。
4. **Session 字典池就緒**：初始化預設的 `ThreadMain` 與各執行緒 `PyDict` 作用域。
5. **使用者自訂引導腳本 (`init.py`) 自動執行**：
   - 若使用者在 `MODULES/PYTHON/` 目錄下放置了 `init.py`，`Init.csvi` 在執行時會自動載入並執行它。
   - **應用場景**：可在 `init.py` 中預先宣告全域自訂演算法、初始化 PyTorch / ONNX Runtime / YOLO 模型權重，讓正式生產流程零等待。

---

## 三、 多 Session 作用域常駐架構

ARCOS 具備 13 個主執行緒（Thread 0~12）與多個動態 SubThread。Python 模組實作了企業級的隔離與常駐機制：

```
+-----------------------------------------------------------------------------------------+
|                                ARCOS Python 模組核心管理池                                |
+-----------------------------------------------------------------------------------------+
|  [Session: ThreadMain]  --> 獨立 PyDict 作用域 (Thread 0 獨享, 變數 a=10)                 |
|  [Session: Thread1]     --> 獨立 PyDict 作用域 (Thread 1 獨享, 變數 a=20, 互不衝突)       |
|  [Session: Thread2]     --> 獨立 PyDict 作用域 (Thread 2 獨享)                           |
|  ...                                                                                    |
|  [Session: VisionAI]    --> 顯式共用 PyDict 作用域 (跨執行緒共用 YOLO / AI 權重模型)       |
+-----------------------------------------------------------------------------------------+
```

1. **執行緒隱式預設 Session**：
   若指令未指明 `Session` 參數，自動以當前執行緒名稱（`ThreadMain`、`Thread1`...）作為 Session 名稱，各執行緒擁有完全隔離的變數字典。
2. **顯式指定共用 Session (`Session=SessionName`)**：
   當多個執行緒需要共用耗時載入的大型 AI 深度學習模型或全域快取時，在指令參數中加上 `Session=VisionAI`，即可共用同一個 Session 作用域。
3. **Session 永續常駐 (Persistence)**：
   **SubThread 結束時嚴禁回收或銷毀 Session！**
   - 理由一：主執行緒在子執行緒結束後，常需回溯調用 `Python@GetVariable` 讀取子執行緒運算產生的數據。
   - 理由二：大型 AI 模型載入耗時數秒，重複銷毀與重建會嚴重拖垮產線週期時間 (Cycle Time)。
4. **手動重置 Session (`ResetSession`)**：
   僅在使用者明確調用 `Python@ResetSession::[SessionName];` 時，才清空該 Session 字典並重新注入標準環境。

---

## 四、 完整指令規格清單

所有指令以 `Python@` 為前綴，參數以逗號 `,` 分隔，具名參數採用 `Key=Value` 格式，指令結尾帶分號 `;`。

| 指令名稱 | 參數語法 | 參數說明 | 行為說明 |
| :--- | :--- | :--- | :--- |
| **`Init`** | （無參數） | 對應 `Init.csvi` | 初始化 Python 環境與預熱（通常由 `System@IncludeModule::Python;` 自動調用）。 |
| **`Reset`** | （無參數） | 模組全量重置 | 清空所有 Session 字典池與變數，恢復出廠狀態（由 `IncludeModule` 重複調用時自動觸發）。 |
| **`Reset`** | （無參數） | 模組全量重置 | 清空所有 Session 字典池與變數，恢復出廠狀態（由 `IncludeModule` 重複調用時自動觸發）。 |
| **`Script`** | `$script(;)`<br>`Session=$sess, $script(;)` | `$script`: Python 程式碼字串<br>`Session`: 指定 Session (可選) | 執行 Python 程式碼。支援多行腳本（各行以 `\n` 分隔，最後一行加 `;`）。 |
| **`File`** | `$file.py(, Session=$sess);` | `$file.py`: 腳本檔案路徑<br>`Session`: 指定 Session (可選) | 執行 Python 腳本檔。若為相對路徑，自動以 `MODULES/PYTHON/` 為基準。 |
| **`SetVariable`** | `$var(, $val)(, Session=$sess)(, PythonName=$py);` | `$var`: ARCOS 變數名<br>`$val`: 數值/字串 (若省略則自 VariableEngine 讀取)<br>`PythonName`: Python 端變數名稱 | 將數值或 ARCOS 變數寫入 Python 變數。自動判定型別（數字、JSON、字串）。 |
| **`GetVariable`** | `$pyvar(, Session=$sess)(, ARCOSName=$var);` | `$pyvar`: Python 變數名<br>`ARCOSName`: ARCOS 變數名稱 | 將 Python 變數值（以 `str(obj)` 轉換）取回存入 ARCOS `VariableEngine`。 |
| **`SetPoint`** | `$point(, Session=$sess)(, PythonName=$py);` | `$point`: ARCOS Point 點位名<br>`PythonName`: Python 端變數名稱 | 將 ARCOS Point 轉為 1D `numpy.ndarray` (`[X, Y, Z, U, V, W]`, `dtype=float64`)。 |
| **`GetPoint`** | `$pyvar(, Session=$sess)(, ARCOSName=$point);` | `$pyvar`: Python 點位變數名<br>`ARCOSName`: ARCOS Point 點位名 | 自 Python 提取 NumPy 陣列數值，填入 ARCOS `PointEngine` 的 `MachinePoint`。 |
| **`SetCapture`** | `$capture(, Session=$sess)(, PythonName=$py);` | `$capture`: ARCOS Image 名稱<br>`PythonName`: Python 端變數名稱 | 將 ARCOS `ImageEngine` 之 `OpenCvSharp.Mat` 轉為 Python `cv2` 影像陣列。 |
| **`GetCapture`** | `$pyvar(, Session=$sess)(, ARCOSName=$capture);` | `$pyvar`: Python 影像變數名<br>`ARCOSName`: ARCOS Image 名稱 | 將 Python 端 `cv2` 影像陣列轉回 ARCOS `ImageEngine`，供前面板即時顯示。 |
| **`SetCaptureFile`**| `$capture, $file.png, $pyvar(, Session=$sess);` | `$capture`: ARCOS Image<br>`$file.png`: 檔案路徑<br>`$pyvar`: Python 變數名 | 將 ARCOS 影像儲存至 `MODULES/PYTHON/$file.png`，並以 `cv2.imread` 載入 Python。 |
| **`ResetSession`** | `($session);` | `$session`: 欲重設的 Session 名稱 (若省略則為當前執行緒) | 清空指定 Session 字典並重新初始化標準環境變數。 |

---

## 五、 API 與數據結構映射規範

```
+---------------------------+-------------------+---------------------------------------------+
| ARCOS 引擎與結構           | 資料流向           | Python 3.12 數據結構                         |
+---------------------------+-------------------+---------------------------------------------+
| VariableEngine (String)   | <===============> | Python PyObject (數字/字串/JSON，以 str() 導回) |
| PointEngine (MachinePoint)| <===============> | 1D numpy.ndarray (shape=(6,), float64)       |
| ImageEngine (OpenCvSharp) | <===============> | OpenCV cv2 影像 (2D/3D uint8 numpy.ndarray)  |
+---------------------------+-------------------+---------------------------------------------+
```

1. **變數 (VariableEngine ⟷ Python PyObject)**：
   - `SetVariable`：透過 JSON 反序列化與字串相容機制，若傳入 `"100"` 則 Python 建立 `int(100)`；若傳入 `"[1,2]"` 則建立 `list`；普通字串則維持 `str`。
   - `GetVariable`：呼叫 `PyObject_Str(val)` 轉為 UTF-8 字串存回 ARCOS，兼顧任何 Python 自訂型別的表示能力。
2. **點位 (PointEngine ⟷ NumPy 1D Array)**：
   - 點位映射固定為 6 軸：`[X, Y, Z, U, V, W]`。
   - 在 Python 內可任意進行向量運算（如 `pt[0] += 10.0` 或矩陣旋轉）。
3. **影像 (ImageEngine ⟷ OpenCV cv2 ndarray)**：
   - 支援灰階 (`CV_8UC1`) 與彩色 (`CV_8UC3` BGR)。
   - 透過記憶體無損 PNG 編解碼串流，確保不論圖形解析度或通道數，兩端皆可零誤差重構。

---

## 六、 後續修改與維護注意事項

> [!CAUTION]
> **開發警戒與禁忌 (Gotchas)**：
> 1. **切勿開機預先載入 Python**：`ModuleRegistry.CreateDefault()` 必須維持乾淨，絕不可在開機時載入 `PythonModuleHandler`，必須一律由 `System@IncludeModule::Python;` 動態載入。
> 2. **切勿自動回收 Session**：後續維護者切勿出於「節省記憶體」的直覺，在 SubThread 結束處加入 Session 銷毀程式碼，這會破壞跨執行緒數據傳遞與 AI 模型複用。
> 3. **切勿更改預設目錄到安裝路徑**：`MODULES/PYTHON/` 是 ARCOS 與 Python 唯一的沙盒工作區，嚴禁指回 `C:\Python312`。
> 4. **GIL 鎖定必須配對**：在調用任何 Python C-API 前必須先取 C# `lock (GilLock)`，進入前 `PyGILState_Ensure()`，`finally` 區塊內必須執行 `PyGILState_Release()`，缺一不可。
> 5. **UTF-8 編碼強制要求**：C-API 回傳字串指標時，必須使用 `Marshal.PtrToStringUTF8` 解析，嚴禁用 ANSI 或預設編碼，避免繁體中文（CP950）亂碼。
> 6. **多行腳本結尾分號契約**：`ProgramTable.cs` 依據 `IsCommandHeader` 判斷新指令 Header，多行腳本中間行不可帶有 `Module@Action` 特徵，結尾必須帶 `;`。
> 7. **發行打包閉環**：若有新增 CSVI 或修改 Handler，務必同步確認 `tools/pack_runtime.ps1`，執行後確認 `dist/ARCOS_Runtime/MODULES/PYTHON/Init.csvi` 等檔案齊全。
