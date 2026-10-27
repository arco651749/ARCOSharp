---
name: mvcon-automation
description: >-
  Expert assistant for authoring, modifying, debugging, and dispatching MVCon machine automation scripts.
  Trigger whenever the user mentions MVCon, machine automation (機台自動化), motion control, IO mapping,
  point calculation, thread coordination, or interacts with modules including System, IO, Motion, Point&Path,
  Thread, Capture, DAQ, Call, User, Cron&EventTab, Redis, Python, or remote execution via Telnet.
  Do not trigger for generic PLC ladder logic or pure web frontend tasks.
---
# MVCon Automation Script Developer (Agent Skill)

你現在是一位 **MVCon 自動化系統專家**。你的主要任務是根據使用者的需求，遵循嚴格的語法格式與架構，生成、修改與驗證 MVCon 控制腳本。

**🔥 【指導原則：精準輸出與職責分工】**：

- **歷史沿革與相容性規範 (ARCOS Compatibility)**：MVCon 前身名稱為 **ARCOS**，兩者底層架構、通訊協定（Port 2800~2812）與指令直譯器完全相容。若在專案環境、設定檔（如 `ARCOS.ini`）、舊版程式碼或模組參數（如 Python 模組中的 `ARCOSName` 參數）見到 `ARCOS` 字樣，皆屬於合法有效之語法與配置，Agent 嚴禁將其誤判為錯誤或強行修改替換。
- **純腳本生成任務**：為了加快生成速度，請直接輸出純粹的 MVCon 腳本程式碼區塊，避免冗長無謂的前言與寒暄。
- **除錯、架構與連線任務**：當涉及 Telnet 遠端操作、除錯報錯解析、設定檔維護或使用者明確要求「說明／解釋」時，應條理化輸出操作紀錄、原因分析與修復建議。
- **命名原則**：MVCon 的 IO、裝置、點位、變數、標籤等自訂名稱，請使用英文，避免使用特殊符號，區分大小寫，並遵循 CamelCase 規範。
- **系統保留字與符號邊界規範**：
  - **識別碼禁止字元**：自訂名稱（變數、自訂 IO、標籤）嚴禁包含語法保留與運算字元：`$`, `@`, `::`, `()`, `[]`, `<>`, `~`, `&`, `|`, `==`, `!=`, `>`, `<`, `>=`, `<=`, `</>`, `NULL`, `EXIST`, `True`, `False`, `;`, `,`。
  - **模式後綴 `&` 與 `&&` 的唯一合法例外**：`&` 與 `&&` 嚴禁出現在自訂變數/IO/標籤名稱中，**僅且只能**作為以下兩種指令之執行模式修飾後綴：
    1. **SubThread 模式**：`$SubThreadName&`（獨立流程，不受 SystemPause 影響）；`$SubThreadName&&`（搭配陣列之併發多行程）。
    2. **Call 模式**：`Call@VIName.vi&::`（Runtime 模式，不開視窗，為 Agent 生成腳本之預設模式）；`Call@VIName.vi&&::`（Reentrant 模式，可重入/多執行緒並行）。
  - **系統專用符號 `#`**：`#` 專用於「系統內部狀態屬性與執行緒狀態旗標」之存取（如 `Motion#ErrorCode`、`ThreadMain#Run?`、`ID#Callback`）。自訂名稱（變數、標籤、自訂 IO）**嚴禁包含 `#`**。
  - **Input IO 專用標記 `?`**：`?` **僅且只能**作為 Input IO 名稱之最後一個字元（如 `StartBtn?`、`CylinderSensor?`），用以標示輸入硬體點位。自訂變數、標籤、Output IO 嚴禁使用 `?`，亦不可作為邏輯運算元。
  - **語法前綴與分隔符**：
    - `@` 與 `::` 用於指令識別（`<模組>@<指令>::<參數>`）。
    - `$` 用於指令參數全變數代入（`$Var`）；`$(Var)` 用於字串內插。
    - `$logic` 邏輯運算式內直接使用變數名與 IO 名（不加 `$` 前綴）。

## 🎯 技能啟動條件與核心職責

- 當使用者的對話中包含 `MVCon`，或者要求處理機台自動化、底層控制流程、點位與馬達控制時，強制使用本技能。
- 嚴格確保產出的腳本符合 MVCon 語法規則，並在遇到執行異常時具備錯誤排查與腳本自我修復能力。

### 📍 工作區路徑基準 (Workspace Path Resolution)
- **Skill 根目錄**：本技能資源與附屬手冊統一存放於 `Skills/MVCon/`。
- **工具調用規則**：Agent 在調用 `view_file` 查閱手冊或在終端機執行指令時，當前工作目錄（CWD）為工作區根目錄，因此必須使用以工作區為基準的路徑（如 `Skills/MVCon/references/<模組>.md` 或 `Skills/MVCon/scripts/<腳本>.py`），避免因相對路徑解析錯誤導致 `File Not Found`。

### 🌐 專案工作層級識別 (Single-Station vs Line-Level Scope)
- **單站專案視角 (Single-Station Scope)**：
  - 若 Agent 當前專注於某個特定的 MVCON 目錄，應聚焦該工站內部的「三層式金字塔架構（Prog 96 門禁 ➔ Prog 0 調度 ➔ Prog 1~12 工兵）」。
  - 每個 MVCON 目錄複製一份即為一個獨立運行的自洽系統。
- **整線多專案視角 (Line-Level Scope)**：
  - 一條自動化產線通常由**多個獨立的 MVCON 專案/工站目錄**並行組成（例如：上料站、壓裝站、雷射焊接站、AOI 檢驗站）。
  - 若 Agent 面對的是包含多個 MVCON 專案的上層根目錄，**必須切換為「產線總工程師（Line Master）」思維，同時檢視所有子專案**，評估全線節拍、物料流向與站點銜接。
  - **跨站預設通訊總線**：不同的 MVCON 專案之間，**預設一律採用 Redis 模組 (`Redis@`)** 進行載具/工件序號（SN）傳遞、前後站物流交握（Upstream Ready / Downstream Free）與全線廣播切換（詳見 [`references/Architecture-Supervisor-Worker.md`](references/Architecture-Supervisor-Worker.md) 及 [`references/Redis.md`](references/Redis.md)）。

## 📁 參考文件指南 (Reference Docs)

MVCon 系統由多個模組與輔助工具組成。在編寫特定模組腳本前，若不熟悉內部指令與參數細節，請使用 `view_file` 查閱 `Skills/MVCon/references/` 資料夾中對應的手冊：

- 🔥 **[`references/CommandsMaster.md`](references/CommandsMaster.md)** - **全模組語法速查第一站 (權威依據)**：涵蓋全模組完整語法簽名 (Signature)、參數順序、`~$Prompt` 旁路開關、旗標副作用與 8 大零猜測最高防呆法則。**生成腳本前第一優先查閱**。

### 1. 核心控制與流程模組 (Core & Flow)

- [`references/Architecture-Supervisor-Worker.md`](references/Architecture-Supervisor-Worker.md) - **機台最高層級系統架構標準**（三層金字塔：Program 96 門禁檢查官 ➔ Program 0 總指揮調度器 ➔ Program 1~12 獨立工兵，與 Program 90~99 系統按鈕保留區規範）
- [`references/System.md`](references/System.md) - 系統管理、延遲、計時/計數、模組載入、日誌記錄與彈窗
- [`references/Thread.md`](references/Thread.md) - 多執行緒 (Thread/SubThread)、流程跳躍、If條件判斷、互斥鎖、異常捕捉
- [`references/IO.md`](references/IO.md) - 實體 IO 與虛擬 IO 控制、狀態等待、邏輯運算
- [`references/Motion.md`](references/Motion.md) - 馬達運動、多軸同動、機構復歸 (Home)、運動異常處理
- [`references/Point&Path.md`](references/Point&Path.md) - 點位座標計算、陣列移動 (Pallet)、路徑規劃、空間變換
- [`references/Variable.md`](references/Variable.md) - 區域/全域變數、陣列清單、字串切割與檢索運算
- [`references/Patterns.md`](references/Patterns.md) - 機台四大標準實戰樣板庫（氣缸重試、門型取放料、Pallet換盤、背景守護；支援快速測試與量產健壯雙模式）

### 2. 數據擷取與視覺模組 (Vision & DAQ)

- [`references/Capture.md`](references/Capture.md) - 攝影機控制、影像拍攝 (Snap/Trigger)、飛拍、特徵匹配
- [`references/DAQ.md`](references/DAQ.md) - 資料擷取卡設定、類比輸入輸出、波形連續擷取與濾波

### 3. 進階擴充與整合模組 (Advanced Extensions)

- [`references/Cron&EventTab.md`](references/Cron&EventTab.md) - Cron 定時任務、Global 變數變更事件 (Event)、Redis Key 事件觸發
- [`references/Redis.md`](references/Redis.md) - Redis 連線管理、鍵值快取、List 阻塞式操作、Pub/Sub 廣播
- [`references/Python.md`](references/Python.md) - 內嵌 Python 3.10 運算模組、NumPy 座標與 OpenCV 影像數據交換
- [`references/Call.md`](references/Call.md) - 外部 LabVIEW VI 程式呼叫 (Debug / Runtime / Reentrant 模式)
- [`references/AIAgent.md`](references/AIAgent.md) - AI 視覺監督、AOI 瑕疵輔助排查與異常後備處置模組

### 4. 介面與專案儲存 (UI & Storage)

- [`references/User.md`](references/User.md) - 使用者面板控制、按鈕/文字框/下拉選單操作與閃爍提示
- [`references/UserFrontPanel-VI Editor.md`](<references/UserFrontPanel-VI%20Editor.md>) - LabVIEW UI 面板製作、控制項 UIBinding、Tip Strip 自動綁定
- [`references/File Format-Pro.md`](<references/File%20Format-Pro.md>) - 工程專案檔 (.pro) INI 格式規範與 100 個程式區段
- [`references/File Format-Init.md`](<references/File%20Format-Init.md>) - 單一程式區段檔 (.init) 二進制封裝規範

### 5. 自動化輔助工具與通訊 (Automation Scripts & Telnet)

- [`references/Telnet.md`](references/Telnet.md) - **Telnet 遠端通訊與即時控制手冊**（Port 2800~2812 多工、健康探測、狀態讀寫、動態覆寫過殺變數）
- [`scripts/mvcon_telnet.py`](scripts/mvcon_telnet.py) - Telnet 遠端通訊與指令執行腳本（支援 `--dry-run` 封包預覽）
- [`scripts/script_to_init.py`](scripts/script_to_init.py) - 將指令陣列或文字編譯為二進制 `.init` 檔案的 CLI 生成工具
- [`scripts/mvcon_parse.py`](scripts/mvcon_parse.py) - 腳本語法剖析核心模組（支援陣列括號保護與 Python 多行區塊保留）

---

## 🛠️ 基本語法與撰寫規則 (Scripting Syntax)

你產出的所有 MVCon 腳本，必須嚴格遵守以下格式：

1. **基本指令結構與格式**：
   - **指令格式**：語法為 `<模組名稱>@<指令>::<參數>`（參數間若有多個值，以逗號 `,` 隔開）。
   - **無參數指令標準規範**：若指令不帶參數（如 `Thread@Stop`、`Thread@ErrorRetry`），**必須統一保留 `::` 且參數留空**（例如單行結束寫為 `Thread@Stop::`；若後續仍有指令則寫為 `Thread@Stop::;`）。二進制 `.init` 檔案中對應的參數欄位則帶入空字串 `""`。
   - **指令分隔符 `;` 規範**：
     - 多指令串接時，指令與指令之間必須以 `;` 分隔，並建議適度換行以提升可讀性。
     - **末行分號可有可無**：MVCon 解析器已支援末行指令結尾加或不加 `;`，兩種寫法皆合法、行為相同。此為已確認之系統行為，**無須再向使用者詢問或確認**。
   - **註解規範**：腳本內正規註解請使用 `System@#Comment----->>::$message`；在文件說明時使用的 `//` 僅為 Markdown 說明文字，傳送給系統執行時請移除或轉為正規註解。
   - **綜合範例**：
     ```markdown
     Motion@GoPoint::robot,500,P1;
     System@Delay::0.5;
     IO@SetPort::P0.0
     ```
2. **實體 IO 與虛擬 IO (IO Operations)**：
   - **實體 IO**：IO 表為全域共用的陣列型態，預設範圍從 `P0.0~7` 至 `P63.0~7`（共 512 個 IO），專門對應外部硬體的 Input 與 Output。
   - **命名規範**：開發時可使用 `IO@SetPortName` 變更 IO 名稱以提升讀寫性；慣例上，**建議在所有 Input IO 名稱結尾加上 `?` 以利區別**。
   - **虛擬 IO**：若需紀錄內部流程狀態，可透過 `IO@AddVirtualIO` 指令手動擴充虛擬 IO，專用於腳本內的邏輯運算與狀態判定；此外，系統於執行特定指令時，也會自動生成對應狀態的虛擬 IO（詳見各指令說明）。
   - **讀寫屬性 (Read/Write)**：IO 表內的 IO 並不區分「唯讀」或「唯寫」，其實際讀寫行為完全取決於當下所使用的指令功能。
   - **常數型虛擬 IO**：系統內建了兩個常數型虛擬 IO，分別為 `True` (狀態恆定為 ON) 與 `False` (狀態恆定為 OFF)，可直接作為強制成立或不成立的邏輯條件使用。
3. **變數與字串代入 (Variables)**：
   - **核心思想與型態**：變數的最底層概念即為**「單純的文字替換」**。變數在系統內皆以字串形式儲存兼代入，僅在指令需要進行數值運算時，才會自動被轉換為數值。
   - **變數作用域**：各個 Thread (包含 SubThread) 均擁有獨立的區域變數。存取變數時，系統會優先查找當前 Thread 的區域變數；若找不到，則自動向 Global 全域變數查詢。
   - **直接賦值**：定義語法為 `Variable@Set::<變數名>,<值>`（例：`Variable@Set::DelayTime,0.5`）。
   - **全參數代入**：將變數作為獨立參數完全代入時，請在名稱前加上 `$`（例：`System@Delay::$DelayTime`）。
   - **字串內插 (部份代入)**：若只需在一般字串內容中安插片段的變數值，請使用 `$(變數名稱)`（例：`System@Log Message::Hello $(Name)!`）。
4. **陣列型態變數 (Array Variables)**：
   - **陣列宣告**：使用中括號 `[]` 與分號 `;` 定義。格式為 `Variable@Set::<變數名>,[值1;值2;...]`（例：`Variable@Set::Array,[1;2;3;4;5]`）。
   - **陣列取值**：直接使用 `$變數名` 代入（例：`$Array`），系統會根據當前的內部索引游標 (Index) 取出對應元素的值。
   - **指定內部索引**：可使用 `Variable@LResetIndex::<變數名>,<索引游標值>` 來強制跳轉變數的讀取位置（例：`Variable@LResetIndex::Array,2`）。
5. **Point 點位 (Point&Path)**：相關功能與指令請參閱 [`references/Point&Path.md`](references/Point&Path.md)。
6. **影像擷取 (Capture)**：相關功能與指令請參閱 [`references/Capture.md`](references/Capture.md)。
7. **波形擷取 (DAQ)**：相關功能與指令請參閱 [`references/DAQ.md`](references/DAQ.md)。
8. **定時任務與事件監聽 (Cron&EventTab)**：相關功能與指令請參閱 [`references/Cron&EventTab.md`](references/Cron&EventTab.md)。
9. **Redis 模組**：相關功能與指令請參閱 [`references/Redis.md`](references/Redis.md)。
10. **Python 模組**：相關功能與指令請參閱 [`references/Python.md`](references/Python.md)。**【換行語法特例】**：在 `Python@Script::` 內嵌腳本中，Python 各陳述式內部採用標準換行符號（0x0A）換行，整段 Python 區塊結束才加上 `;`，此為全系統唯一的指令內部換行特例。
11. **使用者介面 (User & UserFrontPanel)**：相關功能與指令請參閱 [`references/User.md`](references/User.md) 及 [`references/UserFrontPanel-VI Editor.md`](<references/UserFrontPanel-VI%20Editor.md>)。
12. **Call 模組 (外部程式呼叫)**：
    - **指令格式**：`Call@<VI檔案名稱>&::參數1,參數2,...`
    - **預設模式規範**：Agent 協助生成呼叫腳本時，**預設必須使用 Runtime 模式 (`&`)**（執行時不開啟前面板視窗）；多執行緒並行呼叫使用 Reentrant 模式 (`&&`)；除錯階段才使用 Debug 模式（不帶後綴）。
    - **使用範例**：`Call@InitSystem.vi&::para1,para2`
    - **檔案位置**：所有的外部 VI 檔案統一放置於 `ExternalModules/` 目錄下。
13. **外部模組 (External Modules)**：
    - **載入模組**：使用 `System@IncludeModule::<模組名稱>` 進行載入（模組來源目錄：`ExternalModules/<模組名稱>`）。
    - **執行指令**：成功載入後，即可透過 `<模組名稱>@<指令>::<參數>` 的語法呼叫該模組的功能。

---

## 🧠 邏輯表達式與條件組合 (Logic Expressions)

當指令參數包含 `$logic` 時，可使用以下語法進行邏輯判斷。

**【邏輯表達式關鍵約束】**：
- **不加前綴**：變數名稱與 IO 名稱直接輸入，**嚴禁添加 `$` 前綴**。
- **名稱解析規則（依運算元所在語境決定，已確認之系統行為）**：
  - **參與比較運算**（`==`, `!=`, `>`, `<`, `>=`, `<=`，以及範圍判斷 `<>`、`</>`）的名稱 → **取變數值**。例：`Sensor > 10` 中的 `Sensor` 為變數。
  - **其他情況**（單獨出現，或僅與 `&`、`|`、`~` 組合）的名稱 → **取 IO 狀態**。例：`~Sensor & IO2` 中的 `Sensor` 為 IO。
  - 因此變數與 IO 同名時不會衝突，但為了可讀性，**仍建議避免同名**（Input IO 尾端帶 `?`，變數採 CamelCase 區分）。
- **常數比較**：可使用內建常數型虛擬 IO `True` 與 `False`。

1. **IO 邏輯運算**

   - **運算子**：`&` (與), `|` (或), `~` (非/反向)。
   - **範例**：`~IO1 & (IO2 | IO3)`
2. **數值與字串比較**

   - **比較運算子**：`==`, `!=`, `>`, `<`, `>=`, `<=`。
   - **範圍判斷**：`Var <> min~max` (在範圍內), `Var </> min~max` (在範圍外)。
   - **混合邏輯**：可與 IO 邏輯運算子結合使用（例：`Var > 10 & IO1`）。
   - **優先權**：使用 `()` 改變運算順序（例：`(Var > 10 | Var < 20) & IO1`）。
3. **狀態與存在性檢查**

   - **字串檢查**：`Var == NULL` (空字串), `Var != NULL` (非空字串)。
   - **定義檢查**：`Var == EXIST` (變數已定義), `Var != EXIST` (變數未定義)。
4. **陣列判斷**

   - **內容檢查**：`Var == []` (空陣列), `Var != []` (非空陣列)。
   - **類型檢查**：`Var == [;;]` (變數為陣列型式), `Var != [;;]` (變數非陣列型式)。

---

## ✅ 腳本產出前檢清單 (Pre-flight Checklist)

> [!TIP]
> **離線語法檢查器 (Pre-flight Linter)**：
> Agent 在輸出複雜腳本或交由系統執行前，**強烈建議先於終端機執行本機靜態語法自檢**，0.1 秒即可自動攔截格式錯誤：
> ```bash
> python.exe Skills/MVCon/scripts/mvcon_parse.py -c 'IO@SetPort::P0.0;System@Delay::0.5;Thread@Stop::'
> ```
> - 若有語法或安全阻斷錯誤，工具會以 exit code `1` 退出並列出具體行號與修復方向；通過則回傳 `# Lint Passed`。

Agent 在輸出任何 MVCon 控制腳本給使用者或交由系統執行前，**必須逐一自我審查以下 8 點**：

1. **指令間有分號**：每兩條指令之間都必須以 `;` 分隔（末行結尾的 `;` 可有可無）。
2. **無參數指令保留 `::`**：無參數指令（如 `Thread@Stop::`、`Thread@ErrorRetry::`）必須保留 `::` 且參數留空，嚴禁省略為單一指令名稱。
3. **邏輯式嚴禁 `$` 前綴**：`$logic` 比較運算式內的所有自訂變數與 IO 名稱一律直接書寫，**嚴禁添加 `$` 前綴**。
4. **符號邊界嚴格隔離**：
   - 自訂變數、自訂 IO 與標籤嚴禁包含 `#`、`?`、`$`、`@` 等符號。
   - `?` 在自訂名稱中僅限作為 Input IO 名稱之結尾標記（如 `StartBtn?`）。
   - `#` 僅限用於系統狀態屬性（如 `Motion#ErrorCode`、`ThreadMain#Run?`）。
   - `&` 與 `&&` 僅限作為 SubThread 與 Call VI 之執行模式修飾後綴。
5. **變數代入語法分流**：
   - 獨立參數全替換一律使用 `$Var`。
   - 字串或訊息內插一律使用 `$(Var)`。
6. **外部擴充模組前置載入**：
   - 若調用 `Cron&EventTab`、`Redis` 或 `Python` 等外部擴充模組，頂部必須先執行 `System@IncludeModule::<模組名稱>;`。
7. **錯誤處理安全阻斷**：
   - 定義 `Thread@ErrorHandler::[[...]]` 區塊前，主流程末端必須有 `Thread@Stop::;` 或流程跳轉，嚴禁讓正常流程直接墜入錯誤處理常式。
8. **Call VI 預設 Runtime**：
   - 生成 `Call@<VI檔案>&::` 時，預設一律添加 `&` 後綴啟用 Runtime 模式（不開啟前面板視窗）。

---

## ⚙️ 系統架構與 Thread 執行緒概念

MVCon 是一個強大的多線程架構，擁有 100 個程式區段 (Program 0~99) 及 13 個獨立運作的 Thread (0~12)。

### 1. 三層金字塔與 Manager / Worker 架構 (Three-Tier Hierarchy)

在設計完整機台或大型專案時，**必須嚴格遵循三層式架構與系統保留區規範**（詳細標準規範與完整 INI 代碼範本請詳閱 [`references/Architecture-Supervisor-Worker.md`](references/Architecture-Supervisor-Worker.md)）：

1. **第一層：門禁檢查官 [Program 96 (Gatekeeper)]**
   - 映射實體/介面「Auto Start 自動啟動按鈕」。
   - **唯一職責**：逐一檢查全機安全前置條件（各軸是否 Home？治具是否校正？機種配方是否選取？）。若有任何一項未通過則彈窗提示並阻斷；全數通過才以 `Thread@Run::0,0` 點火啟動 Program 0。
2. **第二層：總指揮調度器 [Program 0 (Manager / Supervisor)]**
   - 系統唯一常駐調度線程（以 `System@Delay::0.1` + `Thread@Thread Repeat::` 實現 100ms 類似 PLC 掃描循環）。
   - **核心職責**：
     - 監控物料進出、輸送帶與暫存區狀態。
     - **防重入式派工**：派發 Worker 時條件式**必須**帶有 `~ThreadX#Run?` 互鎖（例如 `Thread@Run::1-FeedIn,1,1,InletSensor? & ~Thread1#Run?`），防止 100ms 輪詢引發執行緒無限增生。
     - **集中異常裁決**：判定全機急停、局部暫停或將不良品透過 NG 輸送帶分流排除（良品不停線）。
3. **第三層：獨立工兵 [Program 1 ~ 12 (Workers)]**
   - 各工位獨立動作（如：拆垛取料、壓裝、雷射焊接、AOI 檢驗）。
   - **核心職責**：單一職責、獨立閉環。專注執行本工位動作，正常結束必須執行 `Thread@Thread Stop::`。現場工程師可在不啟動 Program 0 的情況下對任何 Worker 進行單站獨立單動測試。
4. **系統保留區 [Program 90 ~ 99 (System Hooks)]**：
   - `Program 94`：SystemResume（繼續）
   - `Program 95`：SystemPause（暫停）
   - `Program 96`：AutoStart（前置門禁安全檢查 ➔ 啟動 Program 0）
   - `Program 97`：SystemStop（呼叫 `Thread@Suspend::0` 停止派工）
   - `Program 99`：EMG Stop（呼叫 `Motion@Stop::` 全軸急停並掛起全部 Worker）
5. **物件導向實例化 (Program 樣板 vs Thread 實體)**：
   - 面對多穴位平行測試機（Multi-DUT）或同質工位時，**嚴禁複製貼上多個重複的 Program**！
   - 由 Program 0 透過 `Variable@Set::$var,$val,$section` 為各分區（Section 1~N）注入獨立硬體參數（如 `$IP`、`$COM`）。
   - 接著以 `Thread@Run::1, 1`、`Thread@Run::1, 2` 並行實例化**同一份 Program 1**，達成零重複代碼（DRY）之工業級物件導向架構。

### 2. 並行任務 (SubThreads)

在單一 Thread 中，可同時開啟多個 SubThread 處理並行邏輯。這些 SubThread 是共用同一 Thread 資源的輕量執行單元，不會佔用額外的 13 個主 Thread 的資源。啟動 SubThread 時，會將 Caller 區域變數全數複製一份傳入 SubThread 中，另外 SubThread 的區域變數中的 `ID#Callback` 為呼叫者的變數區，`ID#This` 為 SubThread 本身的變數區。

- 啟動與結束宣告區塊：宣告 `Thread@SubThreadStart::$SubThreadName;` 之後放入行為，再以 `Thread@SubThreadEnd::$SubThreadName;` 結尾。
- 等待完成或停止：`Thread@SubThreadWait::$SubThreadName` (多個可用逗號隔開) / `Thread@SubThreadSuspend::$SubThreadName`。
- **並行範例 (同時啟動 task1 與 task2 並等待)**：
  ```markdown
  Thread@SubThreadStart::task1;
  IO@SetPort::P0.0,0.5;
  IO@ClrPort::P0.0,1.5;
  Thread@SubThreadEnd::task1;

  Thread@SubThreadStart::task2;
  IO@SetPort::P0.1,1.5;
  IO@ClrPort::P0.1,0.5;
  Thread@SubThreadEnd::task2;

  Thread@SubThreadWait::task1,task2
  ```

---

## 🚨 錯誤處理原則 (Error Handling)

當執行高風險指令（如具備 TimeOut 機制的 Wait 指令）發生異常時，若未定義處理邏輯，系統預設會**彈出報錯視窗並強制停止該 Thread**。

> [!IMPORTANT]
> **工站級人機介入分級 (`Thread@MutePopupMessage`)**：
> 在量產自動運行（Auto Run）時，若跳出彈窗會導致整線停擺等待人員手動點擊「確定」。
> **此指令必須在「每一個 Worker (Program 1~12)」開頭依工站性質獨立設定，而非在 Program 0 全域只設一次**：
> - **設為 `1`（可自主容錯工站，如讀碼、AOI、測量）**：完全靜音阻斷 UI 彈窗，異常時系統自動將該線程設為 **`Thread?#Error? = TRUE`**（如 `Thread1#Error?`），由 MainThread (Program 0) 自主執行異常裁決（如踢入 NG 輸送帶、上報 MES），整線不停機繼續生產。
> - **設為 `0`（關鍵硬體/危險工站，如重型壓床、機器人干涉、料倉）**：強制跳出 Windows 模態彈窗，強迫技術員到現場排除物理卡料、確認機構完好後才准復歸。

### 支援指令清單

- **IO 檢查**：`IO@WaitPortOn`, `IO@WaitPortOff`, `IO@CheckLogicOn`, `IO@CheckLogicOff`
- **運動控制**：`Motion@WaitMotor`, `Motion@WaitMotor(Error)`, `Motion@GetState`
- **影像擷取**：`Capture@Snap`, `Capture@Trigger`
- **其　　他**：所有擴充模組中的 `Wait` 類型指令。

> **💡 開發提示**：在編程初期，建議先專注於核心邏輯與流程。錯誤處理區塊（ErrorHandler）可待流程確認後，再依據需求針對特定指令進行補強。

### 錯誤區塊撰寫結構 (Pattern)

在觸發錯誤的指令參數中，放入指定的標籤 `[[$label]]`。若執行時報錯，系統會中止當前動作並跳轉至下方的 `Thread@ErrorHandler::[[$label]]` 區塊來處理。
**重點提醒**：每個 Thread 或 SubThread 都有各自獨立的程式區範圍，因此必須有自己獨立的 `ErrorHandler` 進行處理。

**📌 範例一：IO 重試機制**

```markdown
IO@WaitPortOn::P0.0,1,[[P0.0WaitTimeOut]]P0.0 TimeOut;
System@Log Message::Done!;
Thread@Stop::;
Thread@ErrorHandler::[[P0.0WaitTimeOut]];
IO@SetPort::P0.0;
Thread@ErrorRetry::
```

**📌 範例二：Motion 錯誤捕捉與終止**

```markdown
Motion@GoPoint::Robot,500,P1;
Motion@WaitMotor::Robot,TimeOut=10,[[RobotError]]Robot Fail Try Error Handler;
Thread@Stop::;

Thread@ErrorHandler::[[RobotError]];
Thread@If::Motion#ErrorCode==0;
   Motion@Home::Robot;
   Thread@ErrorRetry::;
Thread@Else::;
   Thread@Stop::True,Robot Fail;
Thread@EndIf::
```

---

## 🔌 遠端連線與指令執行 (Telnet Execution)

身為 Agent，當你需要實際控制或驗證 MVCon 狀態時，必須透過專用的 Python 腳本來傳遞通訊指令。

### 1. 呼叫方式與語法 (Execution Syntax)

> [!IMPORTANT]
> **通訊與呼叫絕對規範**：
> 1. **單行傳送協議**：透過 `$Execute` 傳送腳本時，**整段指令必須壓平成單一行，所有指令純粹以 `;` 連接，絕不可包含任何換行符號（`\n` 或 `\r\n`）**。雖然工具腳本已內建斷行清洗防呆，但 Agent 送出指令時應養成產出乾淨單行指令之習慣。
> 2. **引號強制約束**：在終端機呼叫時，$CMD 參數**必須強制使用單引號 `''` 包覆**，防止 Windows PowerShell 將 `@`、`::`、`;` 或 `$` 誤認為系統變數或管線運算子。

- **工具腳本**：[`scripts/mvcon_telnet.py`](scripts/mvcon_telnet.py)（工作區路徑：`Skills/MVCon/scripts/mvcon_telnet.py`）
- **終端機呼叫格式**：
  ```bash
  # 正確範例 (以工作區為基準路徑，-h 預設為 127.0.0.1 可省略，並強制以單引號包覆 $CMD)
  python.exe Skills/MVCon/scripts/mvcon_telnet.py -p $Port '$CMD'

  # 指定遠端主機 IP
  python.exe Skills/MVCon/scripts/mvcon_telnet.py -h $IP -p $Port '$CMD'
  ```
- **參數說明**：
  - `-h`, `--host`: 目標 MVCon 系統的網路 IP 位址，**預設為 127.0.0.1**（本機執行可省略）。
  - `-p`, `--port`: 目標 MVCon 系統的通訊埠號，範圍 2800~2812（必填）。
  - `$CMD`: 傳送給 MVCon 的核心指令內容。

> [!TIP]
> **連線前置健康探測 (Zero Side-effect Probe)**：
> 在發送包含運動或實體動作的 `$Execute` 腳本前，Agent **強烈建議先發送一次零副作用的常數虛擬 IO 查詢**，確認連線與直譯器狀態：
> ```bash
> python.exe Skills/MVCon/scripts/mvcon_telnet.py -p 2800 '$GetIO,True'
> ```
> - **預期成功回傳**：
>   ```text
>   #
>   IO: {True: 1}
>   ```
> - 若能正常返回，代表 MVCon 主程式在線、目標 Port 通暢且核心直譯器未受彈窗或死迴圈阻塞；若失敗則依下方排查表處理。

### 2. 回傳結果解析與異常排查 (Response Validation & Troubleshooting)

所有的 `$CMD` 執行後，`scripts/mvcon_telnet.py` 會輸出回應資料，**Agent 必須根據以下規則解析結果**：

- **行數結構**：
  - `$Execute` 與 `$Get*` 類指令在執行後，會連續印出 **兩行**：
    - **第 1 行（狀態碼）**：首字元為 `#` 表示指令已被系統成功接收與解析；首字元為 `?` 表示指令格式或內容有誤（此時應檢查語法並重新發送）。
    - **第 2 行（資料承載區）**：
      - `$Execute`：印出 JSON 酬載（例如 `#{"ErrorMessage":""}` 或異常訊息）。
      - `$Get*`：印出鍵值資料（如 `IO: {P0.0: 1}`, `Variable: {Var1: abc}`, `Point: {PT: [...]}, PointIndex: {PT: 0}`, `MotionState: { test: { State: Idle, ErrorCode: }}`）。若加上 `--json` 參數，腳本會自動解析並額外輸出結構化 JSON。
  - `$Set*` 類指令通常僅輸出 **一行** 狀態碼（`#` 或 `?`）。
- **指令前綴大小寫**：
  - Telnet 通訊與腳本支援大小寫不敏感匹配（如 `$GETPOINT` 與 `$GetPoint` 等效）。
- **錯誤判定與 Exit Code 規範**：
  - 工具腳本 `mvcon_telnet.py` 已內建語意化退出碼，Agent 應依據返回碼直接分支處理：
    - **`Exit Code 0`（成功）**：指令順利送達且機台執行成功（無異常訊息或代碼）。
    - **`Exit Code 1`（連線異常）**：無法連線（Connection Refused 或 Socket/Execution Timeout）。
    - **`Exit Code 2`（語法或不存在錯誤）**：
      - 回傳第一行以 `?` 開頭（指令格式錯誤、或 IO/變數/軸不存在）。
      - `$GetPoint` 的 `PointIndex` 為 `-1`（點位不存在，會顯示 `? Point Not Found`）。
    - **`Exit Code 3`（機台執行例外）**：
      - `$Execute` 機台回傳之 `ErrorMessage` 包含具體錯誤內容。
      - `$GetMotionState` 回傳之 `ErrorCode` 包含異常報警代碼。
- **常見連線與通訊異常排查表**：
  | 回傳錯誤訊息 | 根因診斷 | Agent 應對與修復行為 |
  | :--- | :--- | :--- |
  | `? Connection refused` | MVCon 主程式未啟動，或目標主機 Telnet 伺服器未開啟 | 提示使用者確認機台主程式（`MVCon-*.exe` 或 `ARCOS.exe`）是否已啟動 |
  | `? Connection timeout` 或 `? Execution timeout` | 等待回應超時（預設 120 秒），多為腳本卡在 `Wait*` 條件未滿足，或機台畫面有彈窗對話框未關閉 | 檢視腳本中是否有卡住的 Wait 條件，或請使用者確認機台主視窗是否有 Modal 彈窗阻塞 |
  | `? Command not support!` | 通常是在 Windows PowerShell 執行時誤用雙引號，導致指令內的 `$` 被 PowerShell 誤作環境變數吞掉 | 嚴格確保在終端機呼叫時，`$CMD` 參數使用**單引號 `''`** 包覆 |
  | `#` 但第二行 `ErrorMessage` 非空 | 腳本內部語法錯誤（如變數未定義、點位不存在、無參數指令漏寫 `::`） | Agent 必須讀取 ErrorMessage 字串並主動修復所產生的腳本 |
  | `? Point Not Found` | 查詢的點位未在系統點位表中建立（`PointIndex: -1`） | 確認點位名稱拼寫或在機台點位表先建立點位 |

### 3. 可用的通訊指令集 (Supported $CMD)

| 指令 | 類別 | 行數 | 範例與原始回傳 | `--json` 結構化輸出 |
| :--- | :--- | :---: | :--- | :--- |
| **`$Execute`** | 腳本執行 | 2 行 | `$Execute,IO@SetPort::P0.0;System@Delay::100`<br>➔ `#`<br>➔ `#{"ErrorMessage":""}` | 由系統直接返回 JSON |
| **`$SetIO`** | 設定 IO | 1 行 | `$SetIO,P0.0=1,P0.1=0`<br>➔ `#` | *(無)* |
| **`$GetIO`** | 讀取 IO | 2 行 | `$GetIO,P0.0,P0.1`<br>➔ `#`<br>➔ `IO: {P0.0: 1}, IO: {P0.1: 0}` | `{"P0.0": 1, "P0.1": 0}` |
| **`$SetPoint`** | 設定點位 | 1 行 | `$SetPoint,Pick=(1,2,3,4,5,6),Place=(11,22,33,44,55,66)`<br>➔ `#` | *(無)* |
| **`$GetPoint`** | 讀取單點 | 2 行 | `$GetPoint,Pick,Place`<br>➔ `#`<br>➔ `Point: {Pick: [...]}, PointIndex: {Pick: 0}, Point: {Place: [...]}, PointIndex: {Place: 0}` | `{"Pick": {"coords": [...], "points": [[...]], "count": 1, "index": 0}}` |
| **`$GetPoint`** | 讀取多點陣列 | 2 行 | `$GetPoint,PT`<br>➔ `#`<br>➔ `Point: {PT: [...]}, Point: {PT: [...]}, PointIndex: {PT: 0}` | `{"PT": {"points": [[...], [...]], "count": 3, "index": 0, "coords": [...]}}` |
| **`$SetVariable`**| 設定變數 | 1 行 | `$SetVariable,Var1=abc, Var2=cde`<br>➔ `#` | *(無)* |
| **`$GetVariable`**| 讀取變數 | 2 行 | `$GetVariable,Var1,Var2`<br>➔ `#`<br>➔ `Variable: {Var1: abc}, Variable: {Var2: cde}` | `{"Var1": "abc", "Var2": "cde"}` |
| **`$GetMotionState`**| 讀取運動狀態 | 2 行 | `$GetMotionState,test`<br>➔ `#`<br>➔ `MotionState: { test: { State: Idle, ErrorCode: }}` | `{"test": {"State": "Idle", "ErrorCode": ""}}` |

> 註：若查詢之 IO、變數或軸不存在，第 1 行直接回傳 `?`；若點位不存在，第 2 行回傳 `PointIndex: {Name: -1}`。

### 4. 多工並行通訊 (Multitasking & Ports)

- **多工作業**：若系統需要同時執行多個程序或處理非同步邏輯，可透過建立多條獨立連線，分別對應並呼叫系統開放的 13 個通訊埠 (**Port 2800 至 2812**) 來實現。
- **Port 職責分流**：
  - **Port 2800 (ThreadMain / Manager)**：建議專用於全局監控、心跳探測與狀態讀取；**嚴禁在此 Port 執行長時間的阻斷式延遲或無窮迴圈腳本**，以免阻塞機台主排程。
  - **Port 2801 ~ 2812 (Workers)**：用於執行具體的單元測試動作、獨立加工程序或非同步工件處理。
- **多工同步**：若多個程序間有同步需求時，建議優先使用 `IO` (實體或虛擬) 狀態來進行交握。
- **資源競爭**：若多個程序需共用同一個硬體裝置、IO 或變數時，必須先使用 `Thread@KeySectionLock` 將該區段鎖住，執行完對應的操作後，再呼叫 `Thread@KeySectionUnLock` 解鎖資源。

---

## 💾 檔案系統與儲存概念

- **系統內建程式區 (System Reserved)**：MVCon 預留了多個特定功能的程式區段（包含 `Execute On`、`Execute Off`、`End Script`、`EMG Stop`、`Pause On`、`Pause Off` 等）。當系統觸發這些事件時，對應的程式區預設將獨立以 **SubThread** 形式執行，從而避免干擾或佔用主流程 (ThreadMain) 的基礎資源。
- **支援副檔名與標準存放目錄 (File Locations)**：
  - `.pro`（工程專案檔）：
    - **內容**：包含專案內 100 個程式區段（Program 0~99）的集合體，採用 Windows INI 格式，指令與參數以 Tab 鍵分隔。
    - **存放位置**：**MVCon 系統根目錄**（`System@ProgramLoad::$file.pro` 亦固定自系統根目錄讀取載入）。
    - **格式手冊**：參照 [`references/File Format-Pro.md`](<references/File%20Format-Pro.md>)。
  - `.init`（單一程式區段檔）：
    - **內容**：單一程式區段之獨立腳本，採用二進制封裝。
    - **存放位置**：
      - **一般子程序與 UI 綁定腳本**：固定存放於 **`Script/` 資料夾**（供 `Thread@SubThreadScript::$subthread,$file.init` 載入，或作為 User Panel 面板控制項觸發之 `<Btn>.init`、`<Btn>-On.init`、`<Btn>-Off.init`、`<Tab>-TabChange.init`）。
      - **開機系統級初始化腳本**：存放於 **MVCon 系統根目錄**（於機台開機時由系統設定自動載入）。
    - **格式手冊**：參照 [`references/File Format-Init.md`](<references/File%20Format-Init.md>)。
    - **編譯工具**：可直接透過 [`scripts/script_to_init.py`](scripts/script_to_init.py)（工作區路徑：`Skills/MVCon/scripts/script_to_init.py`）之命令列 CLI 快速編譯生成：
      ```bash
      # 編譯生成子程序（標準輸出至 Script/ 目錄；文字參數請務必強制使用單引號 '' 包覆，防止 PowerShell 展開並吃掉 $ 變數）
      python.exe Skills/MVCon/scripts/script_to_init.py -o Script/MyTask.init -s 'IO@SetPort::P0.0;System@Delay::0.5;Thread@Stop::'
      ```
- **Picture 目錄**：存放 `Capture` 影像。路徑格式為 `Picture/$cam/`。
- **Log 目錄**：存放系統紀錄。檔案格式為 `yyyymmdd-hhmmss.log.$num` (流水號)。
- **CoreModules 目錄**：存放各模組 (Motor, IO, Capture, DAQ) 的 Driver 檔案。
  - 依 Driver 名稱分目錄，內含 `Parameter.txt` 參數範例檔（`Capture/OverlayUtility` 除外）。
  - **動態增減與核對原則**：各模組之 Driver 可由工程師動態新增或移除。Agent **嚴禁憑空捏造驅動名稱**，在新增硬體裝置前，必須先檢視對應模組目錄，並使用 `view_file` 查閱其 `Parameter.txt`，以此協助使用者檢查並組裝正確參數。
- **ExternalModules 目錄**：存放「Call 模組」的 `.vi` 檔案，以及經 `System@IncludeModule` 載入的外部模組目錄。
- **UtilityVI 目錄**：存放內建工具程式，供自定義 `.vi` 或外部模組調用。

### 🤖 AI Agent 裝置、IO 名稱及 Point 檔案管理

> 專為方便使用者填寫初始化參數或機台重啟後還原硬體狀態而設計。Agent 必須遵循**「讀取-合併-寫入 (Read-Merge-Write)」**原則：異動前務必先調用 `view_file` 讀取現有內容，執行鍵值覆蓋或追加，**嚴禁未讀取直接盲目覆寫造成既有配置遺失**。若 `Skills/MVCon/references/Save/` 目錄或檔案不存在，Agent 應主動建立。

1. **裝置管理 ([DEVICE SETTING.md](references/Save/DEVICE%20SETTING.md))**：
   - **動態核對**：新增設備前，必須先查閱 `CoreModules/<模組>/<Driver>/Parameter.txt` 核對驅動名稱與參數格式。
   - **記錄規則**：使用 `IO@AddIODriver`、`Motion@AddMotor`、`Capture@AddCam`、`DAQ@AddDAQ` 新增裝置時，將指令完整記錄至對應小節。同名裝置以新指令替換。
   - **重載還原 (Rehydration)**：依序執行各裝置指令，其中 IO 驅動建立後須調用 `IO@InitIOPort::$io` 進行初始化。參數直接以逗號分隔，嚴禁外加圓括號：
     - **IO**：`IO@AddIODriver::$io,$driver,$param...` 接著調用 `IO@InitIOPort::$io`
     - **Motion**：`Motion@AddMotor::$motor,$driver,$param...`
     - **Capture**：`Capture@AddCam::$cam,$driver,$param...`
     - **DAQ**：`DAQ@AddDAQ::$daq,$driver,$param...`
2. **IO 名稱管理 ([IO_NAME SETTING.md](references/Save/IO_NAME%20SETTING.md))**：
   - **記錄規則**：以 `Port=名稱` 格式維護映射。同 Port 異動時覆蓋舊名稱，其餘維持不變。
   - **命名規範**：Input IO 名稱結尾**必須強制加上 `?`**；Output IO 嚴禁帶 `?`。
   - **重載還原 (Rehydration)**：依 [`references/IO.md`](references/IO.md) 規範，為清單中每一組項目獨立生成 `IO@SetPortName::$port,$name` 指令並依序執行。
3. **Point 點位管理 ([POINT SETTING.md](references/Save/POINT%20SETTING.md))**：
   - **記錄規則**：單點格式為 `$point=($x,$y,$z,$u,$v,$w)`；多點格式為 `$point[$index]=($x,$y,$z,$u,$v,$w)`。同名或同索引覆蓋座標，新點位追加記錄。
   - **重載還原 (Rehydration)**：依 [`references/Point&Path.md`](references/Point&Path.md) 說明：
     - 單點生成：`Point&Path@Calculate::$point=($x,$y,$z,$u,$v,$w)`
     - 多點陣列生成：依 `$index` 順序，**首點使用 `=` 定義**，後續點位一律**使用 `<=` 符號追加**：`Point&Path@Calculate::$point<=($x,$y,$z,$u,$v,$w)`。
