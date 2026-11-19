# ARCOS MultiStation 模組參考手冊 (v2.0)

本手冊涵蓋 **MultiStation** 模組全 12 條指令的 **ARCOS Command Syntax v2.0** 規範（底層由 `BasicEngines.MultiStation` / `MultiStationEngine` 驅動，並完整向下相容舊版整數工站編號與舊指令名 `SetStationON`/`OFF`/`OK`/`NG`）。

---

## 🏗️ 設計哲學：單一統一的多工位狀態機

不將轉盤（Rotary）、輸送帶（Conveyor）與料盤（Tray）硬拆成三套不同模型；所有多工位載具統一以同一個 **`StationGroup`（有序工位集合）** 表達，並與 `Point&Path` 職責徹底解耦（座標與料盤點位交由 `Point&Path` 管理，透過 `FindFirstOK`/`FindEmpty` 輸出的 0-based `index` 串接 `Point&Path@ResetPalletIndex`）：

1. **IO 引擎（存在與品質互鎖）**：每個工位自動維護虛擬 IO 旗標，直接以 `SetOn` / `SetOK` 搭配 `$logic` 運算式設定：
   - `工站.工位#On?`（與 `#Run?` 同步）：有料 / 無料（`SetOn :: station=..., logic=...`）
   - `工站.工位#OK?` / `工站.工位#NG?`：良品 / 不良品判定（`SetOK :: station=..., logic=...`）
   - `工站.工位#Busy?`：工位忙碌狀態
2. **Variable 引擎（隨行履歷資料）**：每個工位可掛載任意多個隨行欄位（`工站.工位#欄位`，預設欄位名 `Data`），並在 `StateShift` 與 `Transfer` 時整包隨工件移動；`SetOn` 在 `logic=False` 時預設 `{clearData=true}` 一併清空 OK/NG 與全部隨行資料。

### 定址格式：`工站.工位`
- **命名工位**：`MultiStation@AddStation :: station=FeedInStation {items=[入料;加工;檢驗;出料]}` → 定址為 `FeedInStation.入料`、`FeedInStation.加工` 等。
- **1-based 料盤工位**：`MultiStation@AddStation :: station=TrayA {slots=20}` → 定址為 `TrayA.1` ~ `TrayA.20`。
- **舊版相容**：未加點號的純整數（如 `station=1`）仍自動對映至預設群組 `Station1#Run?`。

### 涵蓋所有機構的正交操作
- **轉盤（Index Table）**：使用 `StateShift {station=FeedInStation, shift=1}`（正數向前、負數向後，環形緩衝區）。
- **直線輸送帶（Conveyor）**：同樣使用環形 `StateShift`，再顯式對頭端（正向時）或尾端（負向時）呼叫 `SetOn :: ..., logic=False` 清空即可。
- **進 / 出料 Tray 盤**：搭配 `FindFirstOK`、`FindEmpty` 找出目標工位鍵與 **0-based `index`**，交給 `Point&Path@ResetPalletIndex` 定位料盤點位，再以 `Transfer` 整包交接狀態與隨行資料。

---

## 📜 全 12 條指令對照表

| 指令 (`MultiStation@Action`) | 必填輸入 (`::`) | 選填覆寫 (`{...}`) | 輸出 (`->`) / 系統副作用 | v2.0 標準範例 |
| :--- | :--- | :--- | :--- | :--- |
| **`AddStation`** | `station` | `items`(""), `slots`(0) | `[effect: reg station & IO/Var bindings]` | `MultiStation@AddStation :: station=FeedInStation {items=[入料;加工;檢驗;出料]}`<br>`MultiStation@AddStation :: station=TrayA {slots=20}` |
| **`SetOn`** | `station`, `logic` | `clearData`(true) | `[effect: $(station)#On?=eval($(logic)); clears OK/NG+Data on false]` | `MultiStation@SetOn :: station=FeedInStation.入料, logic=InSensor?`<br>`MultiStation@SetOn :: station=FeedInStation.出料, logic=False` |
| **`SetOK`** | `station`, `logic` | — | `[effect: $(station)#OK?=eval($(logic)), #NG?=!eval($(logic))]` | `MultiStation@SetOK :: station=FeedInStation.檢驗, logic=Cam0#OK? & PressCurve#OK?`<br>`MultiStation@SetOK :: station=TrayA.3, logic=False` |
| **`SetData`** | `station`, `val` | `field`("Data") | `[effect: var $(station)#$(field)=$(val)]` | `MultiStation@SetData :: station=FeedInStation.入料, val=SN001 {field=SN}` |
| **`GetData`** | `station` | `field`("Data") | `-> [opt] var` | `MultiStation@GetData :: station=FeedInStation.加工 {field=SN} -> var=CurSN` |
| **`ChkStationRunState`** | `station` | — | `[effect: updates $(station)#Busy?]` | `MultiStation@ChkStationRunState :: station=FeedInStation.加工` |
| **`StateShift`** | — | `station`(""), `shift`("1") | `[effect: shifts ring-buffer states & data]` | `MultiStation@StateShift {station=FeedInStation, shift=1}`<br>`MultiStation@StateShift {station=FeedInStation, shift=-1}` |
| **`Transfer`** | `from`, `to` | `clearSource`(true) | `[effect: moves state & data $(from)->$(to)]` | `MultiStation@Transfer :: from=FeedInStation.出料, to=OutTray.3` |
| **`FindFirstOK`** | `station` | `allowUnchecked`(false) | `-> [opt] var, index` / `[effect: sets $(station)#Found?]` | `MultiStation@FindFirstOK :: station=InTray -> var=SrcSlot, index=SrcIdx` |
| **`FindEmpty`** | `station` | — | `-> [opt] var, index` / `[effect: sets $(station)#Found?]` | `MultiStation@FindEmpty :: station=OutTray -> var=DstSlot, index=DstIdx` |
| **`ResetDefault`** | — | `station`("") | `[effect: resets station flags & data]` | `MultiStation@ResetDefault {station=FeedInStation}` |
| **`UpdateIndicator`** | — | `station`("") | `[effect: syncs indicator IOs]` | `MultiStation@UpdateIndicator {station=FeedInStation}` |

---

## 💡 典型整機流程範例（入料盤 → 轉盤 → 出料盤）

```arcos
// 1. 定義入料盤、轉盤與出料盤（純工位狀態，與 Point&Path 點位陣列解耦）
MultiStation@AddStation :: station=InTray {slots=20}
MultiStation@AddStation :: station=Rotary {items=[入料;加工;檢驗;出料]}
MultiStation@AddStation :: station=OutTray {slots=20}

// 2. 從入料盤找第一個可取料 OK 位 -> 以 ResetPalletIndex 設定料盤點位索引並搬移至轉盤入料口
MultiStation@FindFirstOK :: station=InTray -> var=PickSlot, index=PickIdx
Thread@If :: logic=InTray#Found?
Point&Path@ResetPalletIndex :: pt=InTrayPts {index=$(PickIdx)}
Motion@GoSafe(Pallet) :: mot=Robot1, speed=100, pt=InTrayPts {safePos=30}
MultiStation@Transfer :: from=$(PickSlot), to=Rotary.入料
Thread@EndIf

// 3. 轉盤向前轉動一格（有料/OK/NG 與全部隨行資料同步轉移）
MultiStation@StateShift {station=Rotary, shift=1}

// 4. 出料口尋找空位 -> 以 ResetPalletIndex 設定出料料盤點位索引並下料
MultiStation@FindEmpty :: station=OutTray -> var=DropSlot, index=DropIdx
Thread@If :: logic=OutTray#Found?
Point&Path@ResetPalletIndex :: pt=OutTrayPts {index=$(DropIdx)}
Motion@GoSafe(Pallet) :: mot=Robot1, speed=100, pt=OutTrayPts {safePos=30}
MultiStation@Transfer :: from=Rotary.出料, to=$(DropSlot)
Thread@EndIf
```
