# ARCOSharp 指令語法與參數 Schema 規格書 (Command Syntax v2.0 & Schema Spec)

> **版本**：v2.0 Complete (全部 14 模組、251 條指令已完成)  
> **適用範圍**：`ARCOS.Core` 解析器/格式化器、`CSVI 3.0` 模組、Sequencer 編輯器、MVCON 參數導引視窗，以及後續 `ARCOS.Repl` / AI Agent 閉環驗證。  
> **核心定位**：**AI-Authored, Human-Auditable（AI 編寫、人類易於複查與簽核的受控執行語法）**。

---

## 一、設計哲學與四大原則

舊版 ARCOS 承襲自 LabVIEW 表格單欄排版，採用位置式逗號分隔（如 `IO@SetPort\tP0.0,1,ForceUpdate`）。該格式在「AI 生成程式碼、人類負責審查」的時代存在三大缺陷：位置參數無語意、隱性副作用不可見、引號與多行轉義容易損毀。

本規格確立以下四大設計原則：

1. **顯式勝於隱式（Explicit Over Concise）**：AI 撰寫無須節省字元，一律採用具名參數（`key=value`），明確區分**指令語義**、**必填輸入**、**選填覆寫**與**顯式輸出**。
2. **儲存與顯示分離（Canonical Storage vs. View Projection）**：底層檔案保存乾淨且語意完整的純文字單行規範格式；UI / REPL 顯示層可依閱讀目的切換三種顯示密度，且參數欄排版換行與存檔脫鉤。
3. **防隱藏安全鐵律（Anti-Hiding Safety Rule）**：精簡顯示**僅允許折疊「等於安全預設值」或低風險選填參數**；凡被改為非預設值、或於 Schema 標記為 `Critical`（高風險/安全相關）的參數，在任何顯示模式皆強制展開並高亮。
4. **100% 舊版向下相容（Zero-Break Migration）**：所有既有 `.prog` / `.pro` / `.init` 位置參數腳本無須重寫即可照常執行，並可依 Schema 一鍵無損升級為 v2.0 標準格式。

---

## 二、指令標準語法規範 (Syntax v2.0 EBNF)

### 2.1 整體四段式骨架

```text
Module@Action [:: <RequiredArgs>] [{<OptionalOverrides>}] [-> <OutputBindings>] [[[ErrorLabel]]ErrorMsg]
```

| 段位 | 分隔符 | 語意職責 | 存檔原則 |
| :--- | :---: | :--- | :--- |
| **1. 指令語義** | `Module@Action` | 指定領域模組與原子動作 | 必填（唯一動作識別鍵） |
| **2. 必填輸入** | `::` | 缺少則指令無法成立的核心參數 | 必填全列（若指令無必填參數則省略 `::`） |
| **3. 選填覆寫** | `{ ... }` | 僅列出**刻意覆寫預設值**的選填參數 | 未覆寫預設值者**不寫入檔案**（`AlwaysExplicit` 除外） |
| **4. 附加/可選輸出** | `->` | 動作本體非賦值，但額外吐出/跨型別接收之回傳目標（如 `LogRecordCycleTime -> var=...`、`LPop -> out=...`、`ChkInRangeXY -> var=...`） | 有額外接收目標時才列；若指令本體即賦值/運算（如 `Set`、`Select`、`Calculate`），目標對象直接置於 `::` 第一欄 |
| **5. 錯誤攔截** | `[[Label]]Msg` | 既有的異常跳轉標籤與客製訊息（相容 ARCOS 自癒語法） | 可選，置於最尾端 |

> **為何使用 `{...}` 而非 `[...]`？**  
> ARCOS 既有語法已大量將方括號 `[...]` 用於點位/影像索引（`Tray[3]`）、陣列字面值（`[A;B;C]`）與錯誤標籤（`[[Err1]]`）。改用 `{...}` 作為選填覆寫容器可徹底消除詞法與視覺衝突。

### 2.2 形式化 EBNF 定義

```ebnf
CommandLine     = Module "@" Action
                  [ "::" WS RequiredList ]
                  [ WS "{" WS OptionalList WS "}" ]
                  [ WS "->" WS OutputList ]
                  [ WS ErrorTrap ] ;

RequiredList    = Binding { "," WS Binding } ;
OptionalList    = Binding { "," WS Binding } ;
OutputList      = Binding { "," WS Binding } ;

Binding         = Key "=" Value ;
Key             = Identifier ;                       (* [A-Za-z_][A-Za-z0-9_]* ，大小寫不敏感比對，格式化統一採 camelCase/snake_case *)
Value           = QuotedString | BareValue ;

QuotedString    = '"' { EscapedChar | NotQuoteOrBackslash } '"' ;
EscapedChar     = '\"' | '\\' | '\n' | '\r' | '\t' ;
BareValue       = { AnyCharExcept( ',' | '{' | '}' | '"' | '\n' | "->" | "[[" ) } ;

ErrorTrap       = "[[" LabelName "]]" [ RestOfLine ] ;
```

### 2.3 合法省略型態範例

```text
# 1. 無任何參數
System@Clear Error

# 2. 僅必填（全走安全預設，無 {...}）
IO@SetPort :: io=OUT1
System@Delay :: sec=0.5

# 3. 必填 + 選填覆寫
IO@SetPort :: io=OUT1 {holdSec=0.2, forceUpdate=true}
IO@WaitPortOn :: io=Start? {timeoutSec=5} [[ErrStart]]等待啟動訊號逾時

# 4. 無必填、僅選填覆寫（省略 ::）
System@Pause {logic=Alarm?, threads="Thread1;Thread2"}
Thread@Repeat {logic=LoopEn?}

# 5. 含顯式輸出（型別化鍵名：rec -> var，寫入目標不加 $）
System@LogRecordCycleTime :: rec=StationA {prefix="Cycle:"} -> var=CycleSec
```

### 2.4 核心資料型別統一鍵名規約（Domain-Typed Resource Keys）

為讓審查者「光看參數鍵名就一眼辨識內部操作的 BasicEngine 資料型別」，並讓 AI、Guide UI 與未來 REPL 自動補全可直接依鍵名定位資源池，所有模組統一採用以下型別化標準鍵名（禁止在資源操作指令中泛用 `name=`）：

| 內部引擎 / 資料類別 | 單數標準鍵名 | 複數標準鍵名 | 範例 |
| :--- | :---: | :---: | :--- |
| **`IoEngine`（實體/別名/虛擬點位）** | `io` | `ios` | `IO@SetPort :: io=OUT1`、`IO@Shift :: ios=[OUT1;OUT2]` |
| **`LogicEvaluator`（`$logic` 邏輯運算）** | `logic` | — | `IO@AddVirtualIO :: io=OK? {logic=A? & !B?}`、`Thread@If :: logic=A? & B?` |
| **`TimerEngine`（計時器）** | `timer` | — | `System@TimerReset :: timer=T_Heat {timeoutSec=30}` |
| **`CounterEngine`（計數器）** | `counter` | — | `System@CounterReset :: counter=C_Retry {target=3}` |
| **`RecordEngine`（時間戳紀錄）** | `rec` | — | `System@Record :: rec=CT_Pick {msg=Start}` |
| **`VariableEngine`（變數/陣列）** | `var` | `vars` | `Variable@Set :: var=Count, val=10`、`-> var=CycleSec` |
| **`PointEngine`（點位/料盤）** | `pt` | `pts` | *(Motion / Point&Path)* `Motion@GoSafe :: pt=Tray[3]` |
| **`ImageEngine`（影像/飛拍）** | `imaq` | `imaqs` | *(Capture)* `Capture@FindCircle :: imaq=Cam0 -> pt=ResPt` |
| **`DaqEngine`（波形採樣）** | `wav` | `wavs` | *(DAQ)* `DAQ@Read :: wav=Ch0` |
| **硬體裝置實例（軸控 / 相機 / 採集 / IO 卡）** | `mot` / `cam` / `daq` / `card` | — | `Motion@Home :: mot=Motor1`、`Capture@Snap :: cam=Cam0`、`DAQ@Start :: daq=Daq0`、`IO@InitIOPort :: card=IO1` |
| **人機面板 / 控制項** | `panel` / `ctrl` | `ctrls` | `System@UserPanelClose :: panel=StationB`、`User@ShowControl :: ctrls=[Btn1;Btn2]` |
| **流程控制對象** | `prog` / `thread` / `sub` / `lock` / `label` | `threads` / `subs` | `Thread@SubThreadStart :: sub=CamTask`、`Thread@KeySectionLock :: lock=Z1` |

### 2.5 顯式目標、附加輸出（`->`）與系統隱式副作用（`[effect]`）三層劃分規則

為確保人類由左至右掃描腳本時視線連貫、不產生「目標忽左忽右」的割裂感，所有模組設計新指令時必須嚴格遵守以下三層劃分：

| 層級 | 語法歸屬 | 適用判定條件 | 範例 |
| :--- | :---: | :--- | :--- |
| **1. 顯式目標主詞（Explicit Target）** | 置於 `::` **第一欄**（`Required`） | 指令本體即為「對某對象進行賦值、計算、條件選擇或集合/座標寫入」，缺少該目標則指令無法成立 | `Variable@Set :: var=Res, val=10`<br>`Variable@Calculate :: var=Total, expr=$(A) + $(B)`<br>`Variable@Select :: var=Res, logic=Pass?, true=OK, false=NG`<br>`Point&Path@Select :: pt=P_Dst, logic=Left?, true=P_L, false=P_R`<br>`Point&Path@Calculate :: pt=P_Out, expr=P1 + P2 {append=true}` |
| **2. 附加/可選/隱式產出（Side-Channel Output）** | 置於 **`->`** 區段（`Direction = Output`） | 指令本身有獨立職責（如記錄日誌、彈出佇列、範圍檢測、影像量測），**額外可選或跨領域吐出**一個回傳值供後續使用 | `System@LogRecordCycleTime :: rec=CT_Pick {prefix="CT:"} -> var=CycleSec`<br>`Variable@LPop :: var=Queue -> out=HeadVal`<br>`Point&Path@ChkInRangeXY :: pt=P1, rangePt=SafeBox -> var=IsSafe`<br>`Capture@FindCircle :: imaq=Cam0, roi=ROI_1 -> pt=CirclePt` |
| **3. 系統固定隱式副作用（Implicit Effect）** | **不寫入原始碼**；由 Schema 宣告並於 L2 審查視圖以 `[effect: ...]` 灰字呈現 | 引擎固定自動維護的系統旗標或狀態（如 `#OK?`、`#Over?`、`#Timeout?`、`#InRange?`、`#Last?`） | `[effect: sets HitCnt#Over?]`<br>`[effect: sets P1#InRange?]` |

> **命名防撞補充**：凡屬於「寫入/接收目標名稱」的參數（如 `var=Total`、`-> var=CycleSec`、`pt=P_Out`），標準格式一律寫**裸名稱（不加 `$`）**，避免前置變數過濾器（`FilterParameters`）在該變數已存在時將目標名誤展開為舊值；`$(Name)` 僅用於讀取/插值。

---

## 三、詞法規則、引號轉義與 `.prog` 多行序列化規範

### 3.1 Tokenizer 規則（嚴禁使用 `string.Split(',')`）
1. 解析器必須採用**單次狀態機詞法掃描（Single-Pass Lexer）**，正確辨識：
   - 雙引號保護區間 `"..."`（內部可安全包含 `,`、`=`、`{`、`}`、`->`、空白與 `\n`）
   - 巢狀括號/表達式保護區間：`$(...)`、`(...)`、`[...]`（例如 `expr=$(A) + 1`、`ios=[OUT1;OUT2;OUT3]`、`logic=Start? & !Door?`）
2. **何時必須加雙引號 `"`**：
   - 值包含結構分隔字元 `,`、`{`、`}`、`->`、`[[`、換行 `\n`、前導/尾隨空白，或值本身含 `"` 時必須加雙引號。
   - 一般文字字串參數（如 `msg="Production Started"`、`cmd="PWR ON"`、`code="res = x * 2"`）含空白或 `=` 時保留雙引號以明確界定文字邊界。
   - **免引號規則**：純識別字、數字、點位名、布林值（`OUT1`、`P0.0`、`0.5`、`true`、`Start?`），以及 **`logic` 邏輯運算式與 `expr` 算式**（如 `logic=Start? & !Alarm?`、`logic=$(Total)==40`、`expr=$(A) + $(B)`、`expr=P1 + P2`）只要不含上述結構分隔字元，皆**不強制加雙引號**以維持清爽可讀。

### 3.2 `.prog` INI 檔案序列化修正規範（修復外層引號碰撞）

目前 [ScriptSerializer.cs](file:///d:/AntiGravity/ARCOSharp/src/ARCOS.Core/Serialization/ScriptSerializer.cs#L33-L37) 以 `1="<Command>\t<Param>"` 儲存每行指令，並於讀取時使用 `.Trim('\"')` 拆解。**當參數內部尾端出現雙引號（例如 `msg="Hello"`）時，`.Trim('\"')` 會誤刪參數內部的引號**。

本規格強制修正 `.prog` 單行編解碼規則如下：

1. **寫入 `.prog` 時（Outer INI Escape）**：
   依序將 `Command\tRawParameter` 內部進行標準跳脫：
   - `\` → `\\`
   - `"` → `\"`
   - `\r\n` 與 `\n` → `\n`
   再於最外層包上一對 `"`：`1="System@Log Message\tmsg=\"Line1\nLine2\""`。
2. **讀取 `.prog` 時（Outer INI Unescape）**：
   - 僅精準剝除「**開頭第 1 個 `"` 與結尾最後 1 個 `"`**」（嚴禁使用 `.Trim('\"')`）。
   - 逐字元反轉義 `\\`、`\"`、`\n`（注意向下相容：舊檔未轉義的裸 `\` 非接續 `n`/`r`/`t`/`"`/`\` 時原樣保留）。

### 3.3 「資料多行」與「顯示排版分行」的嚴格分離

| 類型 | 說明 | 存檔方式 | UI 顯示方式 |
| :--- | :--- | :--- | :--- |
| **資料本身多行** | 如 `Python@Script`、`System@Log Message` 內文或批次清單確含換行 | 值以 `""` 包住，換行存為 `\n` | 展開多行；L1 精簡模式可折成首行預覽 `(+N lines)` |
| **排版視覺分行** | 指令有多個具名參數，希望在 Sequencer 表格參數欄垂直對齊易讀 | 存檔仍為單行標準格式，**不靠塞 `\n` 切參數** | 由 UI 顯示投影層依設定切換「單行橫排」或「每段/每參自動換行顯示」 |

---

## 四、寬容解析、自動格式化與向下相容契約

為兼顧「AI 容錯率」、「人類閱讀一致性」與「未來 Schema 演化」，解析管線拆為三層：

### 4.1 層 1：執行期寬容解析器（`CommandParamParser`）
- **區段合併**：執行期無論某個 `key=value` 寫在 `::` 後方或 `{...}` 內部，一律合併進統一字典 `ParsedCommandParams.AllArgs`，確保：
  1. AI 誤將選填參數寫到 `{...}` 外面時不中斷執行。
  2. 未來 CSVI 將參數從「必填」改「選填」（補上預設值）時，所有舊腳本 100% 無縫相容。
- **舊版自動偵測（Legacy Fallback）**：
  若原始參數字串**不含頂層 `=`、不含 `{...}`、亦不含 `->`**（且非單一引號鍵值），自動進入舊版位置參數模式，並依 Schema 中各參數宣告順序（`LegacyIndex`）自動映射為具名鍵值；`[[Label]]Msg` 照常萃取至 `ErrorLabel` / `ErrorMessage`。
- **相容既有的 `KEY=VAL` 混合寫法**：如舊 `IO@Shift`（`OUT1,OUT2,Shift=1`）、`IO@AddIODriver`（`IO1, DEVICE=COM1, ...`）、`System@Pause`（`CONDITION=...`），位置參數與具名參數可同時並存於 `PositionalArgs` 與 `NamedArgs`。

### 4.2 層 2：Schema 驅動自動格式化器（`CommandFormatter`）
於儲存、編輯器套用或 REPL/AI 驗證通過時執行規範化重排：
1. 檢查所有 `Required == true` 參數是否齊全（缺漏回報靜態錯誤）。
2. 將 `Required` 參數依 Schema 順序排入 `::` 後方。
3. 將 `Optional` 參數與 `DefaultValue` 比對：
   - **值不等於預設值**（或標記 `AlwaysExplicit == true`）➔ 自動收進 `{...}`。
   - **值等於預設值**（且非 `AlwaysExplicit`）➔ 自動省略以消除雜訊。
4. 將 `Direction == Output` 參數排入 `->` 後方。
5. 將 `[[Label]]Msg` 固定置於最尾端。

### 4.3 層 3：靜態驗證器（供後續 REPL / AI 閉環使用）
在呈交人類審查前，驗證器對照 Schema 執行確定性檢查：
- `E001` 未知指令（`Module@Action` 不存在）
- `E002` 缺少必填參數
- `E003` 未知具名參數鍵（攔截 AI 捏造參數名）
- `E004` 型別不符（`Number` / `Bool` / `Enum` 範圍錯誤）
- `E005` 引號/大括號未閉合
- `W001` 必填/選填放錯段位（可自動格式化修正）
- `W002` 使用了 AI 審查警戒指令（例如無條件 `Thread@Jump`）

---

## 五、CSVI 參數 Schema 模型（Single Source of Truth）

每個 `.csvi` 透過 `IParameterGuidance.GetSchema()`（並同步寫入 `CSVI.Manifest.json` 的 `Schema` 區段）宣告自身參數結構。**同一份宣告同時驅動：Parser 映射、Formatter 排版、三層顯示投影、MVCON Guide UI 與 AI Schema 輸出**。

### 5.1 資料結構定義（`ARCOS.Core.Guidance` / `ARCOS.Core.Csvi`）

```csharp
public enum ParamDirection
{
    Input = 0,          // :: 或 {...} 區段
    Output = 1,         // -> 區段（使用者指定輸出目標）
    ImplicitEffect = 2  // 不寫入原始碼，僅於審查模式以 Inlay Hint 顯示的系統固定副作用
}

public enum ParamDisplayTier
{
    Primary = 0,   // 核心參數，L1/L2/L3 皆顯示
    Secondary = 1, // 輔助選填，L1 模式若非 Critical 可折疊為 {+N}
    Critical = 2   // 安全/高風險參數，凡非預設值在任何模式皆強制展開並高亮
}

public enum ParamValueType
{
    String, Number, Integer, Boolean, LogicExpr,
    PortName, PortList, DriverName, ThreadTarget, ProgramTarget,
    LabelName, VariableName, FilePath, Enum, MultiLineText
}

public sealed class ParamSpec
{
    public string Name { get; set; } = "";
    public string[] Aliases { get; set; } = Array.Empty<string>(); // 相容舊參數名（如 CONDITION -> logic）
    public int? LegacyIndex { get; set; }                          // 對應舊版逗號位置索引（0-based）
    public ParamDirection Direction { get; set; } = ParamDirection.Input;
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public bool AlwaysExplicit { get; set; }                       // 即使等於預設值也保留寫出
    public ParamValueType ValueType { get; set; } = ParamValueType.String;
    public string[]? AllowedValues { get; set; }                   // Enum 合法值清單
    public ParamDisplayTier DisplayTier { get; set; } = ParamDisplayTier.Primary;
    public string Description { get; set; } = "";
}

public sealed class CommandSchema
{
    public string CommandKey { get; set; } = "";                   // 如 "IO@SetPort"
    public string Summary { get; set; } = "";                      // 一句話人類可讀語義摘要
    public bool SupportsErrorTrap { get; set; }                    // 是否支援 [[Label]]Msg
    public bool AiAuditWarning { get; set; }                       // AI 使用時是否觸發審查提醒（如 Jump）
    public List<ParamSpec> Parameters { get; set; } = new();
    public List<string> ImplicitEffects { get; set; } = new();     // 如 ["sets $(timer)#Timeout?"]
}
```

### 5.2 三層顯示投影規則（View Projection）

| 顯示模式 | 適用場景 | 必填 `::` | 一般選填 `{...}` | `Critical` 非預設選填 | 顯式輸出 `->` | 系統副作用提示 |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: |
| **L1 流程總覽 (`Compact`)** | 快速掃描 50 行主幹 | 顯示（多行折首行） | 折疊為 `{+N}` | **強制展開 + 警示高亮** | 顯示 | 隱藏 |
| **L2 審查模式 (`Audit`)** | 人類複查 AI 程式碼（預設推薦） | 完整顯示 | 完整顯示（與必填區分灰階） | **展開 + 警示高亮** | 完整顯示 | **自動顯示 Inlay Hint** |
| **L3 原始完整 (`Canonical`)** | 編輯/Diff/簽核 | 完整顯示 | 完整顯示（含預設值參考） | 完整顯示 | 完整顯示 | 完整顯示 |

### 5.3 與 MVCON 參數導引視窗（Guide UI）的對應
- **區塊 1（必填核心區）**：對應 `Required == true`，永遠展開，缺填禁用「確定」。
- **區塊 2（進階選項 `Expander`）**：對應 `Required == false && Direction == Input`，預設收合（但若當前指令已有任何非預設覆寫則自動展開並標記 `● 已覆寫` + 提供「還原預設」按鈕）。
- **區塊 3（輸出目標區）**：對應 `Direction == Output`。
- **區塊 4（錯誤攔截區）**：當 `SupportsErrorTrap == true` 時自動呈現 `[[Label]]` 與逾時/異常訊息欄。

---

## 六、全模組指令規格總表（14 模組，共 251 條指令）

> 本章定義全專案 14 個模組（共 **251 條指令**）的標準 v2.0 具名鍵、必填/選填邊界、預設值、風險層級與舊語法映射。  
> 標記規則：`*` 表示 `Critical` 層級；`[err]` 表示支援 `[[Label]]Msg`；`{...}` 內括號標註 `(預設值)`。

---

### 6.1 `IO` 模組（14 條指令）

| 指令 (`IO@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`AddIODriver`** | `card`, `driver` | `port`(""), `outStart`("P0.0"), `outPorts`(8), `inStart`("P1.0"), `inPorts`(8) | `[effect: reg driver $(card)]` | `IO@AddIODriver :: card=IO1, driver=MockIO {port=COM1, outStart=P0.0, outPorts=8, inStart=P1.0, inPorts=8}` | `IO1, MockIO, DEVICE=..., ...` |
| **`InitIOPort`** | `card` | — | `[effect: open $(card)]` | `IO@InitIOPort :: card=IO1` | `$io` |
| **`ForceUpdate`** | `card` | — | `[effect: sync HW $(card)]` | `IO@ForceUpdate :: card=IO1` | `$io` |
| **`SetPortName`** | `io`, `alias` | — | `[effect: alias $(io)<->$(alias)]` | `IO@SetPortName :: io=P0.0, alias=OUT1` | `$physicalPort, $portName` |
| **`AddVirtualIO`** | `io` | `logic`(False) | `[effect: create $(io) = eval($(logic))]` | `IO@AddVirtualIO :: io=CycleOK? {logic=Start? & !Alarm?}` | `$name, $logic` |
| **`SetPort`** | `io` | `holdSec`(0), `forceUpdate`*(false) | `[effect: $(io)=ON]` | `IO@SetPort :: io=OUT1 {holdSec=0.5, forceUpdate=true}` | `$port[, $second[, ForceUpdate]]` |
| **`ClrPort`** | `io` | `holdSec`(0), `forceUpdate`*(false) | `[effect: $(io)=OFF]` | `IO@ClrPort :: io=OUT1 {holdSec=0.2}` | `$port[, $second[, ForceUpdate]]` |
| **`Flash(OnOff)`** | `io`, `onSec`, `offSec` | — | `[effect: pulse $(io) 1->0]` | `IO@Flash(OnOff) :: io=OUT1, onSec=0.2, offSec=0.5` | `$port, $onSec, $offSec` |
| **`Flash(OffOn)`** | `io`, `offSec`, `onSec` | — | `[effect: pulse $(io) 0->1]` | `IO@Flash(OffOn) :: io=OUT1, offSec=0.5, onSec=0.2` | `$port, $offSec, $onSec` |
| **`Shift`** | `ios` | `shift`(1) | `[effect: shift $(ios)]` | `IO@Shift :: ios=[OUT1;OUT2;OUT3] {shift=1}` | `OUT1,OUT2,OUT3,Shift=1` |
| **`WaitPortOn`** `[err]` | `io` | `timeoutSec`*(0=無限) | `[effect: wait $(io)==ON]` | `IO@WaitPortOn :: io=Start? {timeoutSec=5} [[Err1]]啟動逾時` | `$port, $timeout, [[$label]]$msg` |
| **`WaitPortOff`** `[err]` | `io` | `timeoutSec`*(0=無限) | `[effect: wait $(io)==OFF]` | `IO@WaitPortOff :: io=Busy? {timeoutSec=3} [[Err1]]未復位` | `$port, $timeout, [[$label]]$msg` |
| **`CheckLogicOn`** `[err]` | `logic` | `timeoutSec`*(0=無限) | `[effect: wait $(logic)==true]` | `IO@CheckLogicOn :: logic=Start? & !Door? {timeoutSec=5} [[Err]]條件不滿足` | `$logic, $timeout, [[$label]]$msg` |
| **`CheckLogicOff`** `[err]` | `logic` | `timeoutSec`*(0=無限) | `[effect: wait $(logic)==false]` | `IO@CheckLogicOff :: logic=Alarm1 \| Alarm2 {timeoutSec=3}` | `$logic, $timeout, [[$label]]$msg` |

---

### 6.2 `SYSTEM` 模組（21 條指令）

| 指令 (`System@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`Delay`** | `sec` | — | `[effect: wait $(sec)s]` | `System@Delay :: sec=0.5` | `$seconds` |
| **`Log Message`** | `msg` | — | `[effect: append log]` | `System@Log Message :: msg="Production Started"` | `$message` |
| **`Log Error`** | `msg` | — | `[effect: fail thread]` | `System@Log Error :: msg="Vacuum Sensor Fault"` | `$message` |
| **`Clear Message`** | — | — | `[effect: clear UI logs]` | `System@Clear Message` | (無參) |
| **`Clear Error`** | — | — | `[effect: clear EMG & errors]` | `System@Clear Error` | (無參) |
| **`Note`** (`#Comment----->>`) | `text` | — | *(無副作用，純註解)* | `System@Note :: text="=== 進料段初始化 ==="` | `$comment` |
| **`MessageBox`** | `msg` | — | `[effect: block until OK]` | `System@MessageBox :: msg="請確認治具已鎖緊"` | `$message` |
| **`WarningBox`** | `msg` | `logic`("True") | `[effect: prompt Continue/Stop]` | `System@WarningBox :: msg="料盤即將耗盡" {logic=LowTray?}` | `[$logic, ]$message` |
| **`Pause`** | — | `threads`*("ALL"), `logic`("True") | `[effect: pause $(threads)]` | `System@Pause {threads=Thread1, logic=DoorOpen?}` | `[$thread...][, CONDITION=...]` |
| **`Resume`** | — | `threads`*("ALL"), `logic`("True") | `[effect: resume $(threads)]` | `System@Resume {threads=Thread1}` | `[$thread...][, CONDITION=...]` |
| **`TimerReset`** | `timer` | `timeoutSec`(0) | `[effect: reset $(timer)#Timeout?]` | `System@TimerReset :: timer=T_Heat {timeoutSec=30}` | `$timerName[, $timeout]` |
| **`TimerCheck`** | `timer` | — | `[effect: update $(timer)#Timeout?]` | `System@TimerCheck :: timer=T_Heat` | `$timerName` |
| **`CounterReset`** | `counter` | `target`(0) | `[effect: reset $(counter)#Over?]` | `System@CounterReset :: counter=C_Retry {target=3}` | `$cntName[, $targetCount]` |
| **`CounterCheck`** | `counter` | — | `[effect: dec & update $(counter)#Over?]` | `System@CounterCheck :: counter=C_Retry` | `$cntName` |
| **`Record`** | `rec` | `msg`("") | `[effect: timestamp $(rec)]` | `System@Record :: rec=CT_Pick {msg=Start}` | `$recName[, $msg]` |
| **`LogRecordCycleTime`** | `rec` | `prefix`("") | `-> [opt] var` / `[effect: log CT $(rec)]` | `System@LogRecordCycleTime :: rec=CT_Pick {prefix="Pick CT:"} -> var=CycleSec` | `$recName[, $prefix]` |
| **`ProgramLoad`** | `path` | — | `[effect: reload prog slot(s)]` | `System@ProgramLoad :: path=Master.prog` | `$filePath` |
| **`ProgramSection`** | `section` | `title`("") | `[effect: switch ctx.Section=$(section)]` | `System@ProgramSection :: section=StationA {title=Load}` | `$section[, $progName]` |
| **`IncludeModule`** | `module` | — | `[effect: reload module $(module)]` | `System@IncludeModule :: module=IO` | `$moduleName` |
| **`UserPanelOpen`** | `panel` | `alias`(""), `activate`(true) | `[effect: mount UserPanel $(panel)]` | `System@UserPanelOpen :: panel=UserPanel.csvi {alias=StationB, activate=false}` | `$panelSpec` |
| **`UserPanelClose`** | `panel` | — | `[effect: close UserPanel $(panel)]` | `System@UserPanelClose :: panel=StationB` | `$panelName` |

> **註**：舊 `System@UserPanel.csvi` 屬於無原始碼殼層/舊版別名，新腳本統一使用 `UserPanelOpen` 與 `UserPanelClose`。

---

### 6.3 `THREAD` 模組（31 條指令）

#### A. 結構化條件與迴圈（AI 首選控制流）

| 指令 (`Thread@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`If`** | `logic` | — | `[effect: branch block]` | `Thread@If :: logic=Start? & !Alarm?` | `$logicExpr` |
| **`ElseIf`** | `logic` | — | `[effect: branch block]` | `Thread@ElseIf :: logic=Manual?` | `$logicExpr` |
| **`Else`** | — | — | `[effect: branch block]` | `Thread@Else` | (無參) |
| **`EndIf`** | — | — | `[effect: close If block]` | `Thread@EndIf` | (無參) |
| **`ForeachStart`** | `label` | — | `[effect: mark loop head $(label)]` | `Thread@ForeachStart :: label=LoopItems` | `$label` |
| **`ForeachLoop`** | `label`, `vars` | — | `[effect: LMoveNext $(vars) & loop $(label)]` | `Thread@ForeachLoop :: label=LoopItems, vars=[ItemList;PosList]` | `$label, $arr1[, $arr2...]` |
| **`ForeachBreak`** | — | `logic`("True") | `[effect: exit loop]` | `Thread@ForeachBreak {logic=Found?}` | `[$logicExpr]` |
| **`ForeachContinue`** | — | `logic`("True") | `[effect: next iteration]` | `Thread@ForeachContinue {logic=Skip?}` | `[$logicExpr]` |

#### B. 子程序與線程調度（Main / Worker / SubThread）

| 指令 (`Thread@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`Call`** | `prog` | `logic`("True") | `[effect: call $(prog)]` | `Thread@Call :: prog=SubInit {logic=NeedInit?}` | `$prog[, $logicExpr]` |
| **`Return`** | — | — | `[effect: pop call stack]` | `Thread@Return` | (無參) |
| **`Run`** | `prog`, `thread` | `startLine`(1), `logic`("True") | `[effect: start Thread$(thread)#Run?]` | `Thread@Run :: prog=Prog1, thread=1 {startLine=1, logic=Auto?}` | `$prog, $threadNo[, $startLine[, $logic]]` |
| **`Wait`** | `threads` | — | `[effect: wait !$(threads)#Run?]` | `Thread@Wait :: threads=[Thread1;Thread2]` | `$t1[, $t2...]` |
| **`Suspend`** | `thread` | `logic`("True") | `[effect: stop Thread$(thread)]` | `Thread@Suspend :: thread=1 {logic=Err?}` | `$threadNo[, $logicExpr]` |
| **`Resume`** | `thread` | `logic`("True") | `[effect: resume Thread$(thread)]` | `Thread@Resume :: thread=1` | `$threadNo[, $logicExpr]` |
| **`Stop`** | — | `logic`("True"), `msg`("") | `[effect: halt current thread]` | `Thread@Stop {logic=Abort?, msg=使用者中止}` | `[$logicExpr[, $msg]]` |
| **`SubThreadStart`** | `sub` | `logic`("True") | `[effect: spawn $(sub)#Run?]` | `Thread@SubThreadStart :: sub=CamTask {logic=EnCam?}` | `$subName[, $logicExpr]` |
| **`SubThreadEnd`** | `sub` | — | `[effect: end inline SubThread $(sub)]` | `Thread@SubThreadEnd :: sub=CamTask` | `$subName` |
| **`SubThreadScript`** | `sub`, `file` | — | `[effect: spawn $(sub) from $(file)]` | `Thread@SubThreadScript :: sub=LoadTask, file=Load.init` | `$subName, $initFile` |
| **`SubThreadSuspend`** | `sub` | — | `[effect: stop SubThread $(sub)]` | `Thread@SubThreadSuspend :: sub=CamTask` | `$subName` |
| **`SubThreadWait`** | `subs` | `skipNotFound`(false) | `[effect: wait SubThread $(subs)]` | `Thread@SubThreadWait :: subs=[CamTask;IoTask] {skipNotFound=true}` | `$sub1[, $sub2...][, SKIPNOTFIND=1]` |

#### C. 互斥鎖、單步控制與異常處理

| 指令 (`Thread@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 / 系統副作用 | v2.0 標準範例 | 舊格式相容映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`KeySectionLock`** | `lock` | `timeoutSec`*(5.0), `msg`("") | `[effect: acquire mutex $(lock)]` | `Thread@KeySectionLock :: lock=GantryZone {timeoutSec=10, msg=機構區佔用逾時}` | `$lockName[, $timeout[, $msg]]` |
| **`KeySectionUnLock`** | `lock` | — | `[effect: release mutex $(lock)]` | `Thread@KeySectionUnLock :: lock=GantryZone` | `$lockName` |
| **`GoOn`** | — | `logic`("True") | `[effect: SystemPause if !$(logic)]` | `Thread@GoOn {logic=MaterialOK?}` | `[$logicExpr]` |
| **`Repeat`** | — | `logic`("True") | `[effect: jump line 1]` | `Thread@Repeat {logic=AutoRun?}` | `[$logicExpr]` |
| **`MutePopupMessage`** | `enabled` | — | `[effect: ctx.IsMuted=$(enabled)]` | `Thread@MutePopupMessage :: enabled=true` | `1` / `0` |
| **`ErrorHandler`** | `label` | — | `[effect: mark error handler $(label)]` | `Thread@ErrorHandler :: label=ErrRecover` | `$label` |
| **`ErrorRetry`** | — | `logic`("True"), `offset`(0) | `[effect: retry failed cmd (+$(offset))]` | `Thread@ErrorRetry {logic=RetryOK?, offset=0}` | `[$logicExpr[, $lineOffset]]` |
| **`Label`** | `label` | — | *(跳轉錨點)* | `Thread@Label :: label=RetryPoint` | `$labelName` |
| **`Jump`** ⚠️ | `label` | `logic`("True") | `[effect: PC = $(label)]` *(AI 審查警示)* | `Thread@Jump :: label=RetryPoint {logic=NeedRetry?}` | `$label[, $logicExpr]` |
| **`RunNextCMD`** ⚠️ | — | `logic`("True") | `[effect: skip next if !$(logic)]` *(AI 審查警示)* | `Thread@RunNextCMD {logic=DebugMode?}` | `[$logicExpr]` |
| **`SkipNextCMD`** ⚠️ | — | `logic`("True") | `[effect: skip next if $(logic)]` *(AI 審查警示)* | `Thread@SkipNextCMD {logic=DryRun?}` | `[$logicExpr]` |

> ⚠️ **AI 編碼審查政策（Audit Policy）**：`Jump`、`RunNextCMD`、`SkipNextCMD` 在 `CommandSchema` 中標記 `AiAuditWarning = true`。舊腳本完全相容，但 AI 撰寫新流程時應優先使用 `If / ElseIf / Else / EndIf`、`Foreach` 與 `Call`；若 AI 產出包含上述三指令，驗證器將標記 `W002` 提醒審查者人工複查控制流。

---

### 6.4 `VARIABLE` 模組（34 條指令）

| 指令 (`Variable@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`Set`** | `var`, `val` | `section`("") | `[effect: $(var)=$(val)]` | `Variable@Set :: var=Count, val=10 {section=Global}` |
| **`Delete`** | `var` | `section`("") | `[effect: delete $(var)]` | `Variable@Delete :: var=Temp` |
| **`Rename`** | `var`, `to` | `section`("") | `[effect: rename $(var)->$(to)]` | `Variable@Rename :: var=OldKey, to=NewKey` |
| **`Inc`** | `var` | `step`(1), `section`("") | `[effect: $(var)+=$(step)]` | `Variable@Inc :: var=Count {step=2}` |
| **`Dec`** | `var` | `step`(1), `section`("") | `[effect: $(var)-=$(step)]` | `Variable@Dec :: var=Count {step=1}` |
| **`Calculate`** | `var`, `expr` | `section`("") | `[effect: $(var)=eval($(expr))]` | `Variable@Calculate :: var=Total, expr=$(A) + $(B)` |
| **`Select`** | `var`, `logic`, `true`, `false` | `section`("") | `[effect: $(var)=?:]` | `Variable@Select :: var=Result, logic=Pass?, true=OK, false=NG` |
| **`Append`** | `var`, `val` | `section`("") | `[effect: $(var)+=$(val)]` | `Variable@Append :: var=LogStr, val="_Done"` |
| **`Trim`** | `var`, `start`, `end` | `section`("") | `[effect: trim $(var)]` | `Variable@Trim :: var=Barcode, start=0, end=8` |
| **`Slice`** | `var`, `sep` | `section`("") | `[effect: slice $(var)]` | `Variable@Slice :: var=CsvRow, sep=","` |
| **`Joint`** | `var`, `sep` | `section`("") | `[effect: join $(var)]` | `Variable@Joint :: var=Items, sep="-"` |
| **`LPush`** | `var`, `val` | `section`("") | `[effect: lpush $(var)]` | `Variable@LPush :: var=Queue, val=Item1` |
| **`RPush`** | `var`, `val` | `section`("") | `[effect: rpush $(var)]` | `Variable@RPush :: var=Queue, val=Item2` |
| **`LPop`** | `var` | `section`("") | `-> [opt] out` / `[effect: lpop $(var)]` | `Variable@LPop :: var=Queue -> out=HeadVal` |
| **`RPop`** | `var` | `section`("") | `-> [opt] out` / `[effect: rpop $(var)]` | `Variable@RPop :: var=Queue -> out=TailVal` |
| **`LInsert`** | `var`, `val`, `condition` | `section`("") | `[effect: linsert $(var)]` | `Variable@LInsert :: var=Arr, val=5, condition=">0"` |
| **`LRemove`** | `var`, `val` | `section`("") | `[effect: lremove $(val)]` | `Variable@LRemove :: var=Arr, val=BadItem` |
| **`LResetIndex`** | `var` | `index`(0), `section`("") | `[effect: $(var).index=$(index)]` | `Variable@LResetIndex :: var=Arr {index=0}` |
| **`LSetIndexValue`** | `var`, `index`, `val` | `section`("") | `[effect: $(var)[$(index)]=$(val)]` | `Variable@LSetIndexValue :: var=Arr, index=2, val=NewVal` |
| **`LReverse`** | `var` | `section`("") | `[effect: reverse $(var)]` | `Variable@LReverse :: var=Arr` |
| **`LGetIndexSize`** | `var` | `section`("") | `[effect: sets $(var)#Index & #Size]` | `Variable@LGetIndexSize :: var=Arr` |
| **`LFindIndexSet`** | `var`, `val` | `linked`("") | `[effect: sync cursor]` | `Variable@LFindIndexSet :: var=IdArr, val=ID003 {linked=[NameArr;PosArr]}` |
| **`Shift`** | `vars` | `shift`(1) | `[effect: shift $(vars)]` | `Variable@Shift :: vars=[V1;V2;V3] {shift=1}` |
| **`SAdd`** | `var`, `val` | `section`("") | `[effect: sadd $(var)]` | `Variable@SAdd :: var=SetA, val=K1` |
| **`SUnion`** | `var`, `varA`, `varB` | `section`("") | `[effect: A ∪ B]` | `Variable@SUnion :: var=SetOut, varA=SetA, varB=SetB` |
| **`SDiff`** | `var`, `varA`, `varB` | `section`("") | `[effect: A \ B]` | `Variable@SDiff :: var=SetOut, varA=SetA, varB=SetB` |
| **`SIntersection`** | `var`, `varA`, `varB` | `section`("") | `[effect: A ∩ B]` | `Variable@SIntersection :: var=SetOut, varA=SetA, varB=SetB` |
| **`GetPointElement`** | `var`, `pt`, `axis` | `section`("") | `[effect: $(var)=$(axis)($(pt))]` | `Variable@GetPointElement :: var=PickX, pt=PickPos, axis=X` |
| **`GetPointString`** | `var`, `pt` | `section`("") | `[effect: $(var)=str($(pt))]` | `Variable@GetPointString :: var=PickStr, pt=PickPos` |
| **`Publish`** | `vars` | — | `[effect: publish $(vars)]` | `Variable@Publish :: vars=[SyncEvt]` |
| **`Subscribe`** | `var` | — | `[effect: wait publish $(var)]` | `Variable@Subscribe :: var=SyncEvt` |
| **`Flush`** | `var` | — | `[effect: flush $(var)]` | `Variable@Flush :: var=SyncEvt` |
| **`VariableLoad`** | `path` | — | `[effect: load vars]` | `Variable@VariableLoad :: path=RecipeA.var` |
| **`VariableSave`** | `path` | `patterns`("") | `[effect: save vars]` | `Variable@VariableSave :: path=RecipeA.var {patterns=[Cfg_*;Cal_*]}` |

---

### 6.5 `POINT&PATH` 模組（22 條指令）

| 指令 (`Point&Path@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddXYZ`** | `pt`, `delta` | — | `[effect: $(pt).XYZ += $(delta)]` | `Point&Path@AddXYZ :: pt=P1, delta="(10,20,0)"` |
| **`AddUVW`** | `pt`, `delta` | — | `[effect: $(pt).UVW += $(delta)]` | `Point&Path@AddUVW :: pt=P1, delta="(0,0,90)"` |
| **`SubXYZ`** | `pt`, `delta` | — | `[effect: $(pt).XYZ -= $(delta)]` | `Point&Path@SubXYZ :: pt=P1, delta=OffsetPt` |
| **`SubUVW`** | `pt`, `delta` | — | `[effect: $(pt).UVW -= $(delta)]` | `Point&Path@SubUVW :: pt=P1, delta="(0,0,45)"` |
| **`ReplaceXYZ`** | `pt`, `val` | — | `[effect: $(pt).XYZ = $(val)]` | `Point&Path@ReplaceXYZ :: pt=P1, val="(100,50,10)"` |
| **`ReplaceUVW`** | `pt`, `val` | — | `[effect: $(pt).UVW = $(val)]` | `Point&Path@ReplaceUVW :: pt=P1, val="(0,0,180)"` |
| **`ReplaceH`** | `pt`, `h` | — | `[effect: $(pt).H = $(h)]` | `Point&Path@ReplaceH :: pt=P1, h="{\"speed\":80}"` |
| **`Calculate`** | `pt`, `expr` | `append`(false) | `[effect: $(pt)=calc($(expr))]` | `Point&Path@Calculate :: pt=P_Out, expr=P1 + P2 {append=true}` |
| **`InsertOnePoint`** | `pt`, `val` | — | `[effect: $(pt) <= $(val)]` | `Point&Path@InsertOnePoint :: pt=Tray, val="(10,20,30)"` |
| **`ClearPoint`** | `pt` | — | `[effect: clear $(pt)]` | `Point&Path@ClearPoint :: pt=Tray` |
| **`EnableAxis`** | `pt`, `axes` | — | `[effect: mask $(pt) axes=$(axes)]` | `Point&Path@EnableAxis :: pt=P1, axes=XY` |
| **`Select`** | `pt`, `logic`, `true`, `false` | — | `[effect: $(pt)=?:]` | `Point&Path@Select :: pt=P_Target, logic=Left?, true=P_Left, false=P_Right` |
| **`Pallet1D`** | `pt`, `count` | — | `[effect: gen 1D pallet $(pt)]` | `Point&Path@Pallet1D :: pt=Tray1D, count=10` |
| **`Pallet2D`** | `pt`, `countX`, `countY` | — | `[effect: gen 2D pallet $(pt)]` | `Point&Path@Pallet2D :: pt=Tray2D, countX=4, countY=5` |
| **`ResetPalletIndex`** | `pt` | `index`(0) | `[effect: $(pt).index=$(index)]` | `Point&Path@ResetPalletIndex :: pt=Tray2D {index=0}` |
| **`Circle2DPath`** | `pt`, `startDeg`, `endDeg`, `segments` | — | `[effect: gen circle $(pt)]` | `Point&Path@Circle2DPath :: pt=ArcPath, startDeg=0, endDeg=180, segments=12` |
| **`CoordWS(2Edge)`** | `ws`, `xEnd`, `yEnd` | — | `[effect: build WS $(ws)]` | `Point&Path@CoordWS(2Edge) :: ws=WS1, xEnd=PtX, yEnd=PtY` |
| **`Point2WS`** | `pt`, `ws` | — | `[effect: $(pt)=Point2WS($(ws))]` | `Point&Path@Point2WS :: pt=P1, ws=WS1` |
| **`WS2Point`** | `pt`, `ws` | — | `[effect: $(pt)=WS2Point($(ws))]` | `Point&Path@WS2Point :: pt=P1, ws=WS1` |
| **`SelfMotion`** | `pt`, `basePt`, `newPt` | — | `[effect: compensate $(pt)]` | `Point&Path@SelfMotion :: pt=PickPt, basePt=TeachMark, newPt=CamMark` |
| **`ChkInRangeXY`** | `pt`, `rangePt` | — | `-> [opt] var` / `[effect: sets $(pt)#InRange?]` | `Point&Path@ChkInRangeXY :: pt=P1, rangePt=SafeBox -> var=IsSafe` |
| **`TransImaqPoint`** | `imaq`, `pt` | — | `[effect: transform $(pt) via $(imaq)]` | `Point&Path@TransImaqPoint :: imaq=Cam0, pt=P_Vision` |

---

### 6.6 `MOTION` 模組（21 條指令）

| 指令 (`Motion@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddMotion`** | `mot`, `driver` | `axes`(""), `speed`(""), `acc`(""), `dec`(""), `options`("") | `[effect: reg driver $(mot)]` | `Motion@AddMotion :: mot=Motor1, driver=MockMotion {axes=4, speed=100}` |
| **`Home`** | `mot` | `axes`(""), `mode`("") | `[effect: sets $(mot)#Homed?]` | `Motion@Home :: mot=Motor1` |
| **`Stop`** | — | `mot`*("") | `[effect: clears $(mot)#Busy?]` | `Motion@Stop {mot=Motor1}` |
| **`Go`** | `mot`, `axes`, `speed`*, `coords` | — | `[effect: sets $(mot)#Busy?]` | `Motion@Go :: mot=Motor1, axes=XY, speed=100, coords="50,80"` |
| **`Go(SingleAxis)`** | `mot`, `axes`, `speed`*, `coords` | — | `[effect: sets $(mot)#Busy?]` | `Motion@Go(SingleAxis) :: mot=Motor1, axes=Z, speed=50, coords=10` |
| **`GoPoint`** | `mot`, `speed`*, `pt` | — | `[effect: sets $(mot)#Busy?]` | `Motion@GoPoint :: mot=Motor1, speed=100, pt=PickPos` |
| **`GoPoint(SingleAxis)`** | `mot`, `speed`*, `pt` | — | `[effect: sets $(mot)#Busy?]` | `Motion@GoPoint(SingleAxis) :: mot=Motor1, speed=80, pt=PickPos` |
| **`GoPallet`** | `mot`, `speed`*, `pt` | — | `[effect: advances $(pt).index & #Busy?]` | `Motion@GoPallet :: mot=Motor1, speed=100, pt=Tray2D` |
| **`GoSafe`** `[err]` | `mot`, `speed`*, `pt` | `safeAxis`*("Z"), `safePos`*(0) | `[effect: retreat $(safeAxis)=$(safePos)]` | `Motion@GoSafe :: mot=Motor1, speed=100, pt=PickPos {safePos=20}` |
| **`GoSafe(Pallet)`** `[err]` | `mot`, `speed`*, `pt` | `safeAxis`*("Z"), `safePos`*(0) | `[effect: retreat & advance $(pt)]` | `Motion@GoSafe(Pallet) :: mot=Motor1, speed=100, pt=Tray2D {safePos=20}` |
| **`AddPath`** | `mot`, `speed`*, `pt` | — | `[effect: enqueues $(pt)]` | `Motion@AddPath :: mot=Motor1, speed=100, pt=WayPt1` |
| **`ClearPath`** | `mot` | — | `[effect: clears $(mot).Path]` | `Motion@ClearPath :: mot=Motor1` |
| **`RunPath`** | `mot` | `options`("") | `[effect: runs $(mot).Path]` | `Motion@RunPath :: mot=Motor1` |
| **`GetPos`** | `mot` | — | `[effect: updates $(mot) & $(mot)#Joint]` | `Motion@GetPos :: mot=Motor1` |
| **`GetState`** `[err]` | `mot` | — | `[effect: updates #Busy?/#Alarm?/ErrorCode]` | `Motion@GetState :: mot=Motor1 [[ErrM]]馬達異常` |
| **`WaitMotion`** `[err]` | `mot` | `timeoutSec`*(30) | `[effect: waits !$(mot)#Busy?]` | `Motion@WaitMotion :: mot=Motor1 {timeoutSec=10} [[ErrM]]到位逾時` |
| **`WaitMotion(Error)`** `[err]` | `mot` | `timeoutSec`*(30) | `[effect: waits !#Busy? & !#Alarm?]` | `Motion@WaitMotion(Error) :: mot=Motor1 {timeoutSec=10}` |
| **`WaitPosition`** | `mot`, `tol`, `pt` | — | `[effect: waits \|$(mot)-$(pt)\|<=$(tol)]` | `Motion@WaitPosition :: mot=Motor1, tol=0.1, pt=PickPos` |
| **`SetTool`** `[err]` | `mot`, `pt` | — | `[effect: sets $(mot)#Tool]` | `Motion@SetTool :: mot=Motor1, pt=Nozzle1` |
| **`Config`** `[err]` | `mot`, `key`, `val` | — | `[effect: config $(mot)[$(key)]]` | `Motion@Config :: mot=Motor1, key=Accel, val=2000` |
| **`ExtraCMD`** `[err]` | `mot`, `cmd` | — | `[effect: native $(cmd)]` | `Motion@ExtraCMD :: mot=Motor1, cmd="PWR ON"` |

---

### 6.7 `CAPTURE` 模組（28 條指令）

| 指令 (`Capture@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddCam`** | `cam`, `driver` | `width`(""), `height`(""), `exposure`(""), `file`(""), `options`("") | `[effect: reg camera $(cam)]` | `Capture@AddCam :: cam=Cam0, driver=MockCamera {width=640, height=480}` |
| **`Config`** `[err]` | `cam`, `key`, `val` | — | `[effect: config $(cam)[$(key)]]` | `Capture@Config :: cam=Cam0, key=Exposure, val=15` |
| **`Snap`** `[err]` | `cam` | — | `-> [opt] imaqs` / `[effect: sets $(cam)#OK?]` | `Capture@Snap :: cam=Cam0 -> imaqs=FlyBuf` |
| **`Trigger`** `[err]` | `cam` | — | `-> [opt] imaqs` / `[effect: sets $(cam)#OK?]` | `Capture@Trigger :: cam=Cam0 -> imaqs=FlyBuf` |
| **`UpdateImage`** | `cam` | — | `[effect: refresh preview $(cam)]` | `Capture@UpdateImage :: cam=Cam0` |
| **`LoadFile`** | `imaq`, `path` | — | `[effect: loads $(path) into $(imaq)]` | `Capture@LoadFile :: imaq=Cam0, path=sample.png` |
| **`ImageSave`** | `imaq`, `path` | `burnOverlays`(false) | `[effect: saves $(imaq)]` | `Capture@ImageSave :: imaq=Cam0, path=out.png {burnOverlays=true}` |
| **`ClearImaq`** | — | `imaq`("") | `[effect: clears $(imaq)]` | `Capture@ClearImaq {imaq=FlyBuf}` |
| **`LoadFile2Imaq`** | `imaqs`, `path` | — | `[effect: appends to $(imaqs)]` | `Capture@LoadFile2Imaq :: imaqs=BatchImgs, path=img1.png` |
| **`LoadNextImaq`** | `imaqs`, `imaq` | — | `[effect: advances $(imaqs) -> $(imaq)]` | `Capture@LoadNextImaq :: imaqs=BatchImgs, imaq=Cam0` |
| **`ResetImaqIndex`** | `imaqs` | `index`(0) | `[effect: $(imaqs).index=$(index)]` | `Capture@ResetImaqIndex :: imaqs=BatchImgs {index=0}` |
| **`Display2User`** | `imaq`, `ctrl` | — | `[effect: renders $(imaq) -> $(ctrl)]` | `Capture@Display2User :: imaq=Cam0, ctrl=ImgView1` |
| **`ROISet`** | `imaq`, `roi` | — | `[effect: sets ROI on $(imaq)]` | `Capture@ROISet :: imaq=Cam0, roi="10,20,300,200"` |
| **`ROISave`** | `imaq`, `path` | — | `[effect: saves ROI]` | `Capture@ROISave :: imaq=Cam0, path=roi.png` |
| **`ROITranslate`** | `imaq`, `basePt`, `newPt` | — | `[effect: translates ROI]` | `Capture@ROITranslate :: imaq=Cam0, basePt=Mark0, newPt=MarkNow` |
| **`CalibrateLoad`** | `imaq`, `path` | — | `[effect: loads calibration]` | `Capture@CalibrateLoad :: imaq=Cam0, path=cal.png` |
| **`CalibrateManual`** | `imaq` | `scale`(0.05), `originX`(0), `originY`(0), `angle`(0) | `[effect: manual calibration]` | `Capture@CalibrateManual :: imaq=Cam0 {scale=0.02, angle=1.5}` |
| **`CoordSet`** | `imaq`, `pt` | — | `[effect: sets vision coord]` | `Capture@CoordSet :: imaq=Cam0, pt=CalPts` |
| **`CoordSet(2Edge)`** | `imaq`, `xPt`, `yPt` | — | `[effect: sets 2-edge coord]` | `Capture@CoordSet(2Edge) :: imaq=Cam0, xPt=PtX, yPt=PtY` |
| **`CoordSet(Org+2Edge)`** | `imaq`, `orgPt`, `xPt`, `yPt` | — | `[effect: sets org+2-edge coord]` | `Capture@CoordSet(Org+2Edge) :: imaq=Cam0, orgPt=Org, xPt=PtX, yPt=PtY` |
| **`FindCircle(InOut)`** | `imaq` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindCircle(InOut) :: imaq=Cam0 -> pt=CirclePt` |
| **`FindCircle(OutIn)`** | `imaq` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindCircle(OutIn) :: imaq=Cam0 -> pt=CirclePt` |
| **`FindEdge`** | `imaq` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindEdge :: imaq=Cam0 -> pt=EdgePt` |
| **`FindGap`** | `imaq` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindGap :: imaq=Cam0 -> pt=GapPt` |
| **`FindPoint`** | `imaq` | — | `-> pt` / `[effect: sets $(pt)#Find?]` | `Capture@FindPoint :: imaq=Cam0 -> pt=FeatPt` |
| **`GeoMatchOne`** | `imaq`, `template` | `angle`("") | `-> var, pt` / `[effect: sets $(pt)#Find?]` | `Capture@GeoMatchOne :: imaq=Cam0, template=Mark.png {angle=30} -> var=Score, pt=MarkPt` |
| **`PatternMatchOne`** | `imaq`, `template` | `angle`("") | `-> var, pt` / `[effect: sets $(pt)#Find?]` | `Capture@PatternMatchOne :: imaq=Cam0, template=Mark.png -> var=Score, pt=MarkPt` |
| **`PatternMatchOne(Fast)`** | `imaq`, `template` | `angle`("") | `-> var, pt` / `[effect: sets $(pt)#Find?]` | `Capture@PatternMatchOne(Fast) :: imaq=Cam0, template=Mark.png -> var=Score, pt=MarkPt` |

---

### 6.8 `DAQ` 模組（18 條指令）

| 指令 (`DAQ@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddDAQ`** | `daq`, `driver` | `rate`(""), `count`(""), `wave`(""), `options`("") | `[effect: reg DAQ $(daq)]` | `DAQ@AddDAQ :: daq=Daq0, driver=MockDAQ {rate=1000, count=500, wave=Sine}` |
| **`Start`** `[err]` | `daq` | — | `[effect: starts $(daq)]` | `DAQ@Start :: daq=Daq0` |
| **`Stop`** `[err]` | `daq` | — | `[effect: stops $(daq)]` | `DAQ@Stop :: daq=Daq0` |
| **`Wait`** `[err]` | `daq` | `timeoutSec`*(5) | `[effect: waits $(daq)#OK?]` | `DAQ@Wait :: daq=Daq0 {timeoutSec=3} [[ErrD]]採樣逾時` |
| **`AnalogIn`** `[err]` | `daq` | — | `-> [opt] wav` / `[effect: sets $(daq)#OK?]` | `DAQ@AnalogIn :: daq=Daq0 -> wav=ForceSamples` |
| **`AnalogInWaveForm`** `[err]` | `daq` | `sec`("") | `-> [opt] wav` / `[effect: sets $(daq)#OK?]` | `DAQ@AnalogInWaveForm :: daq=Daq0 {sec=0.5} -> wav=PressCurve` |
| **`AnalogOut`** `[err]` | `daq`, `val` | — | `[effect: writes AO on $(daq)]` | `DAQ@AnalogOut :: daq=Daq0, val=3.3` |
| **`AnalogOutWaveForm`** `[err]` | `daq`, `wav` | — | `[effect: streams $(wav) to $(daq)]` | `DAQ@AnalogOutWaveForm :: daq=Daq0, wav=DriveWave` |
| **`WaveFormGenerate`** | `wav` | `type`("Sine"), `rate`(1000), `count`(200), `amp`(5), `offset`(0) | `[effect: generates $(wav)]` | `DAQ@WaveFormGenerate :: wav=RefW {type=Triangle, count=100, amp=10}` |
| **`WaveFormLoad`** | `wav` | `path`("") | `[effect: loads $(wav)]` | `DAQ@WaveFormLoad :: wav=RefW {path=ref.csv}` |
| **`WaveFormSave`** | `wav` | `path`("") | `[effect: saves $(wav)]` | `DAQ@WaveFormSave :: wav=PressCurve {path=log.csv}` |
| **`Calculate`** | `wav`, `expr` | — | `[effect: $(wav)=eval($(expr))]` | `DAQ@Calculate :: wav=DiffW, expr=W1 - W2` |
| **`Filter`** | `wav`, `type` | `window`(3) | `[effect: filters $(wav)]` | `DAQ@Filter :: wav=PressCurve, type=MovingAverage {window=5}` |
| **`MergeWaveForm`** | `wav`, `wavs` | — | `[effect: merges $(wavs) -> $(wav)]` | `DAQ@MergeWaveForm :: wav=MultiW, wavs=[Ch0;Ch1]` |
| **`CompareAvgInRange`** | `wav`, `max`, `min` | `start`(0), `end`(-1) | `[effect: sets $(wav)#OK?]` | `DAQ@CompareAvgInRange :: wav=PressCurve, max=10, min=2` |
| **`CompareMaxMinInRange`** | `wav`, `max`, `min` | `start`(0), `end`(-1) | `[effect: sets $(wav)#OK?]` | `DAQ@CompareMaxMinInRange :: wav=PressCurve, max=12, min=0` |
| **`CompareCoverBy`** | `wav`, `upper` | `lower`("") | `[effect: sets $(wav)#OK?]` | `DAQ@CompareCoverBy :: wav=PressCurve, upper=UpperW {lower=LowerW}` |
| **`Display2User`** | `wav`, `ctrl` | — | `[effect: renders $(wav) -> $(ctrl)]` | `DAQ@Display2User :: wav=PressCurve, ctrl=WaveChart1` |

---

### 6.9 `USER` 模組（21 條指令）

| 指令 (`User@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`SwitchUserPanel`** | `panel` | — | `[effect: ActivePanel=$(panel)]` | `User@SwitchUserPanel :: panel=StationA` |
| **`Param2UserPanel`** | — | — | `[effect: refreshes UserPanel]` | `User@Param2UserPanel` |
| **`ShowControl`** | `ctrls` | — | `[effect: shows $(ctrls)]` | `User@ShowControl :: ctrls=[BtnStart;LblStatus]` |
| **`HideControl`** | `ctrls` | — | `[effect: hides $(ctrls)]` | `User@HideControl :: ctrls=BtnDebug` |
| **`EnableControl`** | `ctrls` | — | `[effect: enables $(ctrls)]` | `User@EnableControl :: ctrls=[BtnRun;BtnStop]` |
| **`DisableControl`** | `ctrls` | — | `[effect: disables $(ctrls)]` | `User@DisableControl :: ctrls=BtnRun` |
| **`GetControlValue`** | `ctrl` | — | `-> [opt] var` / `[effect: default $UserPanel_$(ctrl)]` | `User@GetControlValue :: ctrl=TxtSpeed -> var=CurSpeed` |
| **`SetControlValue`** | `ctrl`, `val` | — | `[effect: sets UI $(ctrl)]` | `User@SetControlValue :: ctrl=TxtSpeed, val=150` |
| **`QueryControls`** | — | `panel`("Active"), `role`("All"), `kind`("All") | `-> [opt] var` / `[effect: default $UserPanelControls]` | `User@QueryControls {kind=Button} -> var=BtnList` |
| **`ButtonValue`** | `ctrl` | `on`(true) | `[effect: sets button $(ctrl)]` | `User@ButtonValue :: ctrl=BtnAuto {on=false}` |
| **`ButtonTrigger`** | `ctrl` | `on`(true) | `[effect: fires button & UserPanel#$(ctrl)?]` | `User@ButtonTrigger :: ctrl=BtnStart` |
| **`ButtonText`** | `ctrl`, `text` | — | `[effect: sets button text]` | `User@ButtonText :: ctrl=BtnStart, text="運行中"` |
| **`ButtonBlinking`** | `ctrl` | `on`(true) | `[effect: sets blinking]` | `User@ButtonBlinking :: ctrl=BtnAlarm {on=true}` |
| **`TextBoxValue`** | `ctrl`, `text` | — | `[effect: sets TextBox]` | `User@TextBoxValue :: ctrl=TxtMsg, text="Ready"` |
| **`TextBoxTrigger`** | `ctrl`, `text` | — | `[effect: fires TextBox & syncs VAR]` | `User@TextBoxTrigger :: ctrl=TxtLot, text="LOT-001"` |
| **`TextBoxBlinking`** | `ctrl` | `on`(true) | `[effect: sets blinking]` | `User@TextBoxBlinking :: ctrl=TxtMsg {on=false}` |
| **`TextRingValue`** | `ctrl`, `val` | — | `[effect: sets TextRing]` | `User@TextRingValue :: ctrl=CmbMode, val=Auto` |
| **`TextRingTrigger`** | `ctrl`, `val` | — | `[effect: fires TextRing & syncs VAR]` | `User@TextRingTrigger :: ctrl=CmbMode, val=Manual` |
| **`TextRingBlinking`** | `ctrl` | `on`(true) | `[effect: sets blinking]` | `User@TextRingBlinking :: ctrl=CmbMode {on=true}` |
| **`TabValue`** | `ctrl`, `page` | — | `[effect: selects Tab]` | `User@TabValue :: ctrl=MainTabs, page=1` |
| **`TabTrigger`** | `ctrl`, `page` | — | `[effect: fires Tab change]` | `User@TabTrigger :: ctrl=MainTabs, page=Settings` |

---

### 6.10 `PYTHON` 模組（12 條指令）

| 指令 (`Python@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`Init`** | — | `session`("") | `[effect: init Python]` | `Python@Init` |
| **`Reset`** | — | — | `[effect: resets all sessions]` | `Python@Reset` |
| **`ResetSession`** | — | `session`("") | `[effect: resets $(session)]` | `Python@ResetSession {session=VisionS1}` |
| **`File`** | `path` | `session`("") | `[effect: runs $(path)]` | `Python@File :: path=calc.py {session=VisionS1}` |
| **`Script`** `[err]` | `code` | `session`("") | `[effect: runs inline code]` | `Python@Script :: code="res = x * 2" {session=S1}` |
| **`SetVariable`** | `var` | `val`(""), `py`(""), `session`("") | `[effect: ARCOS var -> Python]` | `Python@SetVariable :: var=Count {py=py_cnt, session=S1}` |
| **`GetVariable`** | `py` | `session`("") | `-> [opt] var` / `[effect: Python -> ARCOS var]` | `Python@GetVariable :: py=res {session=S1} -> var=Result` |
| **`SetPoint`** | `pt` | `py`(""), `session`("") | `[effect: ARCOS pt -> Python]` | `Python@SetPoint :: pt=PickPos {py=p_pick}` |
| **`GetPoint`** | `py` | `session`("") | `-> [opt] pt` / `[effect: Python -> ARCOS pt]` | `Python@GetPoint :: py=p_out -> pt=PlacePos` |
| **`SetCapture`** | `imaq` | `py`(""), `session`("") | `[effect: ARCOS imaq -> Python]` | `Python@SetCapture :: imaq=Cam0 {py=img0}` |
| **`SetCaptureFile`** | `imaq`, `path`, `py` | `session`("") | `[effect: saves & binds Python]` | `Python@SetCaptureFile :: imaq=Cam0, path=snap.png, py=img0` |
| **`GetCapture`** | `py` | `session`("") | `-> [opt] imaq` / `[effect: Python -> ARCOS imaq]` | `Python@GetCapture :: py=out_img -> imaq=CamOut` |

---

### 6.11 `REDIS` 模組（9 條指令）

> **多 Session 預設規則**：第一個呼叫 `Redis@CreateSession` 建立的 Session 自動成為預設 Session；其餘所有 Redis 指令省略 `{session=...}` 時操作預設 Session，填寫 `{session=MES}` 時則隔離操作指定 Session。

| 指令 (`Redis@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`CreateSession`** | `session` | `endpoint`("127.0.0.1:6379") | `[effect: sets RedisErr#$(session)=OK]` | `Redis@CreateSession :: session=Line1 {endpoint=192.168.1.10:6379}` |
| **`Set`** | `key`, `val` | `session`("") | `[effect: sets RedisKV[$(key)]]` | `Redis@Set :: key=LotId, val=LOT2026 {session=MES}` |
| **`Get`** | `key` | `session`("") | `-> [opt] var` / `[effect: default $(key)]` | `Redis@Get :: key=LotId {session=MES} -> var=CurLot` |
| **`LPush`** | `key`, `val` | `session`("") | `[effect: pushes to RedisList[$(key)]]` | `Redis@LPush :: key=JobQ, val=Part001 {session=Line1}` |
| **`LPop`** | `key` | `session`("") | `-> [opt] var` / `[effect: default $(key)]` | `Redis@LPop :: key=JobQ {session=Line1} -> var=NextJob` |
| **`BLPop`** `[err]` | `key` | `session`(""), `timeoutSec`*(5) | `-> [opt] var` / `[effect: blocks & pops]` | `Redis@BLPop :: key=JobQ {session=Line1, timeoutSec=3} -> var=NextJob [[ErrQ]]佇列逾時` |
| **`Publish`** | `key`, `val` | `session`("") | `[effect: publishes to $(key)]` | `Redis@Publish :: key=LineEvt, val=CycleDone {session=Line1}` |
| **`Subscribe`** | `key` | `session`("") | `[effect: subscribes $(key)]` | `Redis@Subscribe :: key=LineEvt {session=Line1}` |
| **`CMD`** | `cmd` | `session`("") | `[effect: runs raw Redis $(cmd)]` | `Redis@CMD :: cmd=PING {session=Line1}` |

---

### 6.12 `MULTISTATION` 模組（15 條指令）

> **工站.工位雙模定址規則**：透過 `MultiStation@AddStation` 建立工站群組，支援**命名工位清單**（`{items=[入料;加工;檢驗;出料]}`）或 **1-based 料盤工位＋點位綁定**（`{slots=20, pt=Tray2D}`）。定址採 **`工站.工位`**（如 `FeedInStation.入料`、`TrayA.3`）：有料/無料/OK/NG/Busy 自動映射至 **IO 引擎**（`#On?`/`#Run?`/`#OK?`/`#NG?`/`#Busy?`），工位隨行資料自動映射至 **Variable 引擎**（`工站.工位#欄位`）並隨 `StateShift`（環形緩衝區向前 `+shift` / 向後 `-shift`；直線輸送帶僅需對頭/尾呼叫 `SetStationOFF` 清空即可）與 `Transfer` 同步搬移；`SetStationOFF` 預設一併清空 OK/NG 與隨行資料。`FindFirstOK` 與 `FindEmpty` 專用於進/出料 Tray 盤快速定位取放工位與座標並更新 `$(station)#Found?`。

| 指令 (`MultiStation@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddStation`** | `station` | `items`(""), `slots`(0), `pt`("") | `[effect: reg station & IO/Var bindings]` | `MultiStation@AddStation :: station=FeedInStation {items=[入料;加工;檢驗;出料], pt=TurnPts}`<br>`MultiStation@AddStation :: station=TrayA {slots=20, pt=Tray2D}` |
| **`SetStationON`** | `station` | — | `[effect: sets $(station)#On?=true]` | `MultiStation@SetStationON :: station=FeedInStation.入料` |
| **`SetStationOFF`** | `station` | `clearData`(true) | `[effect: #On?=false & clears OK/NG+Data]` | `MultiStation@SetStationOFF :: station=FeedInStation.出料` |
| **`SetStationOK`** | `station` | — | `[effect: sets $(station)#OK?=true]` | `MultiStation@SetStationOK :: station=FeedInStation.檢驗` |
| **`SetStationNG`** | `station` | — | `[effect: sets $(station)#NG?=true]` | `MultiStation@SetStationNG :: station=TrayA.3` |
| **`SetData`** | `station`, `val` | `field`("Data") | `[effect: var $(station)#$(field)=$(val)]` | `MultiStation@SetData :: station=FeedInStation.入料, val=SN001 {field=SN}` |
| **`GetData`** | `station` | `field`("Data") | `-> [opt] var` | `MultiStation@GetData :: station=FeedInStation.加工 {field=SN} -> var=CurSN` |
| **`GetPoint`** | `station` | — | `-> [opt] pt` | `MultiStation@GetPoint :: station=TrayA.3 -> pt=TargetPt` |
| **`ChkStationRunState`** | `station` | — | `[effect: updates $(station)#Busy?]` | `MultiStation@ChkStationRunState :: station=FeedInStation.加工` |
| **`StateShift`** | — | `station`(""), `shift`("1") | `[effect: shifts ring-buffer states & data]` | `MultiStation@StateShift {station=FeedInStation, shift=1}`<br>`MultiStation@StateShift {station=FeedInStation, shift=-1}` |
| **`Transfer`** | `from`, `to` | `clearSource`(true) | `[effect: moves state & data $(from)->$(to)]` | `MultiStation@Transfer :: from=FeedInStation.出料, to=OutTray.3` |
| **`FindFirstOK`** | `station` | `allowUnchecked`(false) | `-> [opt] var, pt` / `[effect: sets $(station)#Found?]` | `MultiStation@FindFirstOK :: station=InTray -> var=SrcSlot, pt=PickPt` |
| **`FindEmpty`** | `station` | — | `-> [opt] var, pt` / `[effect: sets $(station)#Found?]` | `MultiStation@FindEmpty :: station=OutTray -> var=DstSlot, pt=PlacePt` |
| **`ResetDefault`** | — | `station`("") | `[effect: resets station flags & data]` | `MultiStation@ResetDefault {station=FeedInStation}` |
| **`UpdateIndicator`** | — | `station`("") | `[effect: syncs indicator IOs]` | `MultiStation@UpdateIndicator {station=FeedInStation}` |

---

### 6.13 `AIAGENT` 模組（3 條指令）

| 指令 (`AIAgent@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`SuperviseStart`** | `prompt` | `bypass`*(false) | `[effect: marks AIAgent_Supervising=true]` | `AIAgent@SuperviseStart :: prompt=InspectPart {bypass=true}` |
| **`SuperviseEnd`** `[err]` | — | — | `[effect: invokes AI supervise]` | `AIAgent@SuperviseEnd [[ErrAI]]` |
| **`ErrorHandle`** `[err]` | `prompt` | `bypass`*(false) | `[effect: invokes AI error_handle]` | `AIAgent@ErrorHandle :: prompt=CheckJam {bypass=true} [[Recover]]` |

---

### 6.14 `HELLO` 模組（2 條指令）

| 指令 (`Hello@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`Init`** | — | — | `[effect: sets Hello_Status@Hello=INITIALIZED]` | `Hello@Init` |
| **`HelloWorld`** | — | `msg`("hello world") | `[effect: logs $(msg)]` | `Hello@HelloWorld {msg="Hi ARCOSharp"}` |

---

## 七、分階段實作與驗證計畫

1. **Phase 1 — `ARCOS.Core` 基礎設施升級（不破壞任何現有測試）**：
   - 新增 `CommandSchema` / `ParamSpec` 模型（`ARCOS.Core/Guidance/` 與 `CsviManifest.cs`）。
   - 實作 `CommandParamParser`（單次詞法掃描 + 寬容合併 + Legacy 位置參數自動映射）與 `CommandFormatter` + `CommandDisplayProjector`（L1/L2/L3 顯示投影與防隱藏邏輯）。
   - 修正 `ScriptSerializer.cs` 的外層 `""` 讀寫轉義（取代 `.Trim('\"')`），補齊單元測試。
2. **Phase 2 — 先導三模組落地（`IO` → `SYSTEM` → `THREAD`）**：
   - 依第六章契約表，為三模組 66 條 `.csvi` 補上 `GetSchema()` 宣告並切換使用 `CommandParamParser`。
   - 升級 MVCON Guide UI，依據 Schema 區分「必填區 / 折疊選填覆寫區 / 輸出與錯誤攔截區」。
3. **Phase 3 — 顯示整合與後續 `ARCOS.Repl` 接入**：
   - 於 `SequencerEditorControl` 加入 L1 / L2 / L3 檢視切換與參數欄自動換行投影。
   - 後續建置獨立 `ARCOS.Repl` 時，直接複用同一套 `CommandSchema`、`CommandParamParser`、`CommandFormatter` 與靜態驗證器。
