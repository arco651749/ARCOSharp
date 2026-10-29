# ARCOS Point&Path 模組參考手冊

這是針對 **ARCOS Point&Path 模組** 專門設計的 Agent 技能指南。身為 ARCOS 自動化系統專家，當遇到需要計算座標、設定機構安全點、或者做工作區矩陣轉換時，請參照以下語法規則與指令。

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到「**點位計算**」、「**陣列設定(Pallet)**」、「**座標轉換**」或使用了 `$X,$Y,$Z` 等關鍵字時，請自動套用本技能。

## 🛠️ 基本語法與格式規範
1. **指令句型**：所有的指令都必須以 `Point&Path@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。
3. **與 Motion 模組連動**：`Point&Path` 與 `Motion` 具備重度依賴關係。前者負責「**產生與計算點位**」，後者負責根據點位「**執行運動**」。Motion 相關指令說明請參考 [`references/Motion.md`](Motion.md)。
4. **陣列與索引 (Index) 存取**：Point 點位支援多點陣列，並內建「內部索引值」。Motion 會依據被呼叫的指令名稱，自動決定點位的讀取行為：
   - 包含 **Point**：視為「單點」操作。
   - 包含 **Path**：視為「多點 (陣列)」操作。
   - 包含 **Pallet**：視為「單點」操作，且在執行完成後，點位的內部索引值會**自動加 1**。
   - **索引設定**：可透過 `ResetPalletIndex` 手動重設內部索引值。
   - **直接索引**：若需強制存取陣列中特定位置的點位，可直接使用中括號 `[]` （例如 `$point[0]`）。
5. **與 Capture 模組連動**：`Point&Path` 與 `Capture` 亦具備重度依賴關係。`Capture` 使用 Point 點位來儲存 2D 視覺或量測的資料點，一般會運用到 `X`、`Y`、`U`、`H` 這四個軸向。Capture 相關指令說明請參考 [`references/Capture.md`](Capture.md)。

### 📌 點位的宣告與運算規則
- **標準格式**：`($X, $Y, $Z, $U, $V, $W)` 或結尾帶操作標記 `($X, $Y, $Z, $U, $V, $W, $H)`。
   - `$H` 用在圓上代表半徑；用在點位的設定上代表要開啟或關閉 MOTION 上的某個 IO。
- **缺省與保持 (inf)**：
   - 只用到 X/Y/Z 時，可簡寫為 `($X, $Y, $Z)`，未填部分預設為 0。
   - 若要保持當前機台座標，請寫 `inf`。例如：`(100,inf,200,inf,inf,inf)` 即保持原本的 Y, U, V, W 軸不變。
- **點位軸向修飾符 (`<點位>@<軸向>`)**：
   - **基本概念**：以自訂點位名稱搭配 `@` 修飾特定軸向（作用類似使用 `inf` 遮罩過濾不需要動作的軸向），且**不會修改**點位變數的原始空間資料。未被 `@` 指定的軸向，在指令執行時會自動以 `inf` 替代。
   - **用法一：過濾並保留指定軸向**（格式：`<點位>@指定軸向`，如 `P1@XY`、`P1@Z`）
     - **範例**：若 `P1=(10,20,30,90,180,90)`，在帶入 `P1@XYZ` 時，系統實際傳遞的座標為 `(10,20,30,inf,inf,inf)`，即僅保留 X、Y、Z 數值，其餘軸不動，且 `P1` 原始值不變。
   - **用法二：覆寫特定軸向數值**（格式：`<點位>@指定軸向(新數值)`，如 `P1@Z(100)`）
     - **範例**：在帶入 `P1@Z(100)` 時，除了未被指定的軸向會自動轉成 `inf` 之外，指定的 Z 軸數值將會被臨時替換為 `100`。
   - **實戰應用**：利用 `@` 修飾符實現 `GoSafe` 三段式移動 (安全高度退/進點)
     ```markdown
     // 1. 宣告目標點位 PT
     Point&Path@Calculate::PT=(10,20,30,90,180,90)
     
     // 2. 起手式：將 Z 軸率先拉升至安全高度 100 (其餘軸保持 inf 不動作)
     Motion@GoPoint::Robot,500,PT@Z(100)
     Motion@WaitMotor::Robot
     
     // 3. 途中橫移：在安全高度下，將 XY 面移動至目標位置上方 (其餘軸不動)
     Motion@GoPoint::Robot,500,PT@XY
     Motion@WaitMotor::Robot
     
     // 4. 落地：最後將 Z 軸下放至 PT 的原始深度
     Motion@GoPoint::Robot,500,PT@Z
     Motion@WaitMotor::Robot
     ```

- **單點定義 (使用 `=`)**：
  `Point&Path@Calculate::P1=(100,200,300)`
- **多點陣列定義 (使用 `<=`)**：(陣列建立)
  ```markdown
  Point&Path@Calculate::PT=(1,2,3);
  Point&Path@Calculate::PT<=(2,4,6);
  Point&Path@Calculate::PT<=(3,6,9)
  ```
- **四則運算**：支援點位變數之間的加減乘除，也支援點位與常數的計算。
  - **點對點運算**（對應軸向各自計算）：
    - `Point&Path@Calculate::P3=P1+P2` （P1 與 P2 對應軸相加）
    - `Point&Path@Calculate::P3=P1-P2` （P1 與 P2 對應軸相減）
    - `Point&Path@Calculate::P3=P1*P2` （P1 與 P2 對應軸相乘）
    - `Point&Path@Calculate::P3=P1/P2` （P1 與 P2 對應軸相除）
  - **點對常數運算**（所有軸向同步計算）：
    - `Point&Path@Calculate::P3=P1+1.1` （P1 的 XYZUVW 皆加上 1.1）
    - `Point&Path@Calculate::P3=P1-1.1` （P1 的 XYZUVW 皆減去 1.1）
    - `Point&Path@Calculate::P3=P1*1.1` （P1 的 XYZUVW 皆乘上 1.1）
    - `Point&Path@Calculate::P3=P1/1.1` （P1 的 XYZUVW 皆除以 1.1）

---

## 📜 常用指令對照表

### 1. 點位宣告與數學計算
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **Calculate** | `$point=$point1`<br>`$point=$point1+$point2`<br>`$point<=($X,$Y...)` | 處理所有點位宣告、算術運算與陣列添加 |
| **Select(X=A?B:C)**| `$point=$logic ? $point1 : $point2` | 三元運算。若邏輯為真選 P1，否則選 P2 |
| **EnableAxis** | `$point, $axis` | 選擇該點位要作動的軸向 |
| **InsertOnePoint** | `$point=$point1` 或 `(X,Y...)` | 插入單一點資料到陣列中 |
| **ClearPoint** | `$point` | 清除該點資料記憶 |

### 2. 點陣列 (Pallet) 管理與路徑規劃
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **Pallet1D** | `$point, $num` | 產生一維等距陣列 (`$point[0]`為起點, `$point[1]`為終點) |
| **Pallet2D** | `$point, $numx, $numy` | 產生二維陣列 (`$point[0]`=原點, `point[1]`=X終點, `point[2]`=Y終點) |
| **Circle2DPath** | `$point, $startangle, $endangle, $section` | 依據圓的點資料計算出弧線段路徑點陣列 |
| **ResetPalletIndex**| `$point(, $index)` | 強制重設點陣列的內部執行索引 |
| **CheckPalletLast?**| `$point` | 檢查該點資料現在是否走到最後一點，結果輸出到`$point`#Last? |

### 3. 座標系轉換與工作區 (Workspace)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **SelfMotion** | `$point, $base, $new` | 執行剛體旋轉，進行機構座標轉換 |
| **TransImaqPoint** | `$point, $base` | 影像點對應機構點。<br>`$base[0]`=原點, `$base[1]`=X軸, `$base[2]`=Y軸 |
| **Point2WS** | `$point, $workspace` | 將當前的 **Motion 機構座標** 轉換為 **Workspace 虛擬工作區座標** |
| **WS2Point** | `$point, $workspace` | 將目標的 **Workspace 虛擬工作區座標** 轉換回 **Motion 機構座標** 供手臂執行 |
| **CoordWS(2Edge)** | `$workspace, $x_end, $y_end` | 以現有原點、指定新 X, Y 方向建立工作區 |

### 4. 局部維度運算與替換
如果只需要對某幾個軸向進行取代或運算，請使用這些精確指令：
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **AddXYZ** / **SubXYZ** | `$point, $point1` 或 `x,y,z` | 僅對 `$X,$Y,$Z` 進行加/減法操作 |
| **AddUVW** / **SubUVW** | `$point, $point1` 或 `u,v,w` | 僅對 `$U,$V,$W` 進行加/減法操作 |
| **ReplaceXYZ** / **ReplaceUVW**| `$point, $point1` 或 `x,y,z` | 直接替換掉 `$X,$Y,$Z` 或 `$U,$V,$W` 的數值 |
| **ReplaceH** | `$point, $value` | 直接替換掉特定用處的 `$H` 數值 |
| **ChkInRangeXY**| `$point, $range` | 檢查 `$point` 是否落在 `$range[0]` (左上) 與 `$range[1]` (右下) 之間，結果輸出到IO`$point`#InRange? |

---

## 💡 AI 撰寫範例參考

**範例：設定並轉換工作區座標系 (Workspace)**
> **設定概念**：工作區座標系用於多個 Motion 及 Vision 之間的座標轉換，使用前需要參照各個 Motion 及 Vision 對應的 (原點、X軸、Y軸) 座標建立參考基準。

當使用者要求將 XY 軸翻轉時，你可以使用以下結構撰寫程式：

```markdown
// 1. 定義工作區 WS：依序將原點、X軸終點、Y軸終點存入陣列中
Point&Path@Calculate::WS=(0,0,50);
Point&Path@Calculate::WS<=(0,100,50);
Point&Path@Calculate::WS<=(100,0,50);

// 2. 宣告虛擬工作區上的目標點
Point&Path@Calculate::PT=(20,40);

// 3. 執行轉換：將工作區座標算回機械臂實際要走的 Motion 座標
// 執行後 PT 的值因為 XY 翻轉且 Z 保留，會變成 (40,20,50)
Point&Path@WS2Point::PT,WS;

// 4. (選用) 呼叫機構移動
Motion@GoPoint::Robot,500,PT
```
