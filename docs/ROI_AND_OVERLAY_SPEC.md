# ARCOS / MVCON 影像系統：ROI 與 OVERLAY 統一文字表示格式規範
**文件版本**：1.0.0  
**適用模組**：`Capture` 模組（`Capture@...` 指令群）  
**架構相容性**：相容 NI VDM（NI Vision Development Module）幾何描述規範  
**最後更新時間**：2026-10-26  

---

## 1. 核心設計架構與理念

在 ARCOS / MVCON 機器視覺架構中，**ROI（Region of Interest，感興趣區域）** 與 **OVERLAY（非破壞性視覺疊加圖層）** 共享**完全同構的幾何數據結構與序列化文字表示格式（Shape DSL）**：

```
                    ┌──────────────────────────────┐
                    │    統一文字表達式 (DSL)       │
                    │  例: RotatedRect(...) [...]  │
                    └──────────────┬───────────────┘
                                   │
                 ┌─────────────────┴─────────────────┐
                 ▼                                   ▼
        【 作為輸入：ROI 】                 【 作為輸出：OVERLAY 】
  - 傳入視覺檢驗演算法                  - 在相機即時影像上疊加圖層
  - 限制搜尋範圍、找圓、邊緣偵測        - 標記合格/不良區域 (PASS/NG)
  - 提高影像處理速度與抗干擾能力        - 顯示量測尺寸、中心十字與文字
```

### 關鍵優勢：
1. **零轉換損耗**：從前端前面板（User Front Panel）或 Capture 畫面上拉取的 ROI，可直接由 `ROI STRING` 複製貼上至腳本參數欄。
2. **純文字可讀性**：使用者與工程師可在 Sequencer 腳本的 `Parameters` 儲存格直接手工鍵入或修改。
3. **多形狀串接**：支援以分號 `;` 在單一指令行中一次傳遞多個幾何輪廓。

---

## 2. 基礎幾何輪廓語法（ROI & OVERLAY 核心通用）

幾何表示法採用 `形狀名稱(數值參數串列)`，不區分大小寫。

| 幾何形狀標籤 | 語法格式 | 參數說明 | 典型鍵入範例 |
| :--- | :--- | :--- | :--- |
| **`Point`** | `Point(x, y)` | `x, y`: 目標座標 | `Point(320, 240)` |
| **`Line`** | `Line(x1, y1, x2, y2)` | `x1, y1`: 起點座標<br>`x2, y2`: 終點座標 | `Line(100, 150, 500, 150)` |
| **`Rect`** | `Rect(x, y, w, h)` | `x, y`: 左上角座標<br>`w, h`: 寬度與高度 (AABB) | `Rect(120, 80, 400, 300)` |
| **`RotatedRect`** | `RotatedRect(cx, cy, w, h, angle)` | `cx, cy`: 旋轉中心點<br>`w, h`: 矩形寬高<br>`angle`: 傾斜角度 (度，順時針) | `RotatedRect(320, 240, 250, 120, 45.0)` |
| **`Circle`** | `Circle(cx, cy, r)` | `cx, cy`: 圓心座標<br>`r`: 圓半徑 | `Circle(320, 240, 85)` |
| **`Oval`** | `Oval(cx, cy, rx, ry, [angle])` | `cx, cy`: 橢圓中心<br>`rx, ry`: X軸與Y軸半徑<br>`angle`: 主軸旋轉角 (選填，預設0) | `Oval(300, 200, 100, 60, 15.0)` |
| **`Annulus`** | `Annulus(cx, cy, r_in, r_out, [a1], [a2])` | `cx, cy`: 圓環中心<br>`r_in, r_out`: 內徑與外徑<br>`a1, a2`: 起迄扇形角度 (選填，預設 0~360) | `Annulus(320, 240, 60, 100, 0, 360)` |
| **`Polygon`** | `Polygon(x1,y1, x2,y2, x3,y3, ...)` | 封閉多邊形頂點序列 (至少 3 組點) | `Polygon(100,100, 250,80, 300,220, 90,200)` |
| **`Crosshair`** | `Crosshair(cx, cy, [size])` | `cx, cy`: 十字中心點<br>`size`: 十字臂長像素 (選填，預設 20) | `Crosshair(320, 240, 30)` |

---

## 3. OVERLAY 專屬擴充屬性語法：`[屬性鍵值列表]`

當形狀用作檢驗結果呈現、標記或畫布覆蓋層時，可在形狀本體後方緊接方括號 `[...]` 指定樣式。屬性之間以逗號 `,` 分隔。

| 屬性名稱 | 支援型別 / 取值範圍 | 預設值 | 說明 |
| :--- | :--- | :--- | :--- |
| **`color`** | 色名 (`Green`, `Red`, `Blue`, `Yellow`, `Cyan`, `Magenta`, `White`, `Orange`) 或十六進位色票 (`#10B981`) | `Green` | 外框線條或填充顏色 |
| **`width`** | 正整數 (如 `1`, `2`, `3`, `5`) | `1.5` | 外框筆刷線條粗細 (px) |
| **`fill`** | 布林值 (`true` / `false`) | `false` | 是否開啟 20% 透明度幾何內部填充 |
| **`text`** | 字串 (如 `"PASS"`, `"R=12.5mm"`) | 空 | 在該形狀上方或中心點繪製的即時量測標籤 |
| **`dashed`**| 布林值 (`true` / `false`) | `false` | 是否採用虛線樣式 |

### OVERLAY 組合範例：
1. **工件合格標記框**：
   ```text
   Rect(120, 80, 400, 300)[color=Green, width=2, text="PART 01: PASS"]
   ```
2. **傾斜瑕疵檢測警示框**：
   ```text
   RotatedRect(320, 240, 250, 120, -12.5)[color=Red, width=3, text="NG: Offset -12.5 deg"]
   ```
3. **同心圓量測環與中心靶心**：
   ```text
   Annulus(320, 240, 50, 90)[color=Yellow]; Crosshair(320, 240, 25)[color=Cyan, text="Center (320, 240)"]
   ```

---

## 4. 坐標系與校正單位機制（像素 Pixel vs 物理世界 World）

遵循使用者操作原則：「**有相機校正時顯示實際世界座標，無校正時才顯示像素座標**」。

1. **預設自動模式（推薦）**：
   - 數值直接填入純數字（如 `Rect(12.5, 30.0, 40.0, 20.0)`）。
   - 若當前相機（如 CAM0）已載入標定矩陣（Calibration Matrix），系統自動將數值解析為**實際世界物理座標（單位：mm）**。
   - 若相機尚未標定，系統直接按**影像像素座標（Pixel）** 解析。
2. **強制指定單位模式（顯式後綴）**：
   - 帶 `mm` 後綴：`Rect(12.5mm, 30.0mm, 40.0mm, 20.0mm)` $\rightarrow$ 強制物理座標換算。
   - 帶 `px` 後綴：`Rect(125px, 300px, 400px, 200px)` $\rightarrow$ 強制像素絕對座標。

---

## 5. Sequencer 腳本指令應用範例（嚴格使用 `Capture@...` 模組）

在 ARCOS / MVCON 執行序腳本中，所有影像擷取、檢驗視野、動態圖層指令均歸屬於 **`Capture`** 模組：

### 範例 1：設定檢驗工位搜尋視野（`Capture@SetROI`）
```text
Command:    Capture@SetROI
Parameters: Rect(100, 100, 450, 350)
```
*說明：將當前相機工位的分析視野限制在 (100, 100) 寬 450 高 350 之區域。*

### 範例 2：多孔位並行檢驗視野（`Capture@SetROI` 多幾何串接）
```text
Command:    Capture@SetROI
Parameters: Circle(150, 200, 40); Circle(450, 200, 40); Circle(300, 350, 40)
```
*說明：一次指定 3 個圓形檢測視野進行並行特徵比對。*

### 範例 3：影像畫面手動疊加對位基準標記（`Capture@DrawOverlay`）
```text
Command:    Capture@DrawOverlay
Parameters: Crosshair(320, 240, 30)[color=Green, text="Teach Datum Point"]; Line(0, 240, 640, 240)[color=Yellow, dashed=true]
```
*說明：在相機畫面上疊加綠色對位十字與黃色水平基準參考虛線。*

### 範例 4：清除畫面上所有殘留 Overlay（`Capture@ClearOverlay`）
```text
Command:    Capture@ClearOverlay
Parameters: -1
```
*說明：`-1` 代表清除所有圖層，亦可傳入特定相機編號（如 `CAM0`）。*

### 範例 5：讀取當前畫面手動選定之座標（`Capture@GetSelectPoint`）
```text
Command:    Capture@GetSelectPoint
Parameters: TargetVar=$PickPoint
```
*說明：將使用者在畫面上點擊 `IMAQSelectPoint` 鎖定的校正後物理座標讀入腳本變數 `$PickPoint`，供後續 `Motion@AbsMove` 取放運動使用。*

---

## 6. 前端 UI 介面雙向綁定規格

1. **影像工具列聯動**：
   - 使用者點擊畫面左側懸浮工具列（🔍 ✋ 📏 ⬜ ⬚ ⭕ ✏ 🎯）在影像上拖曳繪製時，系統底層即時生成上述標準 DSL 字串，並自動寫入 `ROI STRING` 文字方塊。
2. **文字反向渲染**：
   - 使用者直接在 `ROI STRING` 貼上或鍵入語法後按下 Enter，影像畫布即時重新解析並高亮呈現對應形狀。
