# MVCon Capture 模組參考手冊

這是針對 **MVCon Capture 模組** 專門設計的 Agent 技能指南。身為 MVCon 自動化系統專家，當遇到需要控制攝影機 (Camera)、讀取或儲存影像、進行影像校正或特徵匹配時，請參照以下語法規則與指令。

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到「**攝影機**」、「**拍照/擷取**」、「**影像辨識**」、「**校正片**」或「**ROI**」等影像處理關鍵字時，請自動套用本技能。

## 🛠️ 基本語法與格式規範
1. **指令句型**：所有的 Capture 指令都必須以 `Capture@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。
3. **與 Point&Path 模組連動**：`Capture` 與 `Point&Path` 具備重度依賴關係。`Capture` 經常運用 Point 點位來儲存 2D 視覺或量測的資料點，其中主要會對應到 `X`、`Y`、`U`、`H` 這四個軸向。Point&Path 相關指令說明請參考 [`references/Point&Path.md`](Point&Path.md)。

## 📸 單張影像與多張影像管理機制
在編寫拍攝邏輯前，Agent 需要理解影像在 MVCon 內的儲存機制：
1. **預設 Capture 記憶體**：`Snap` 與 `Trigger` 皆為拍照指令。完成後，影像會預設存在系統提供的 12 個 Capture 記憶體中，可在UI中進行查看。
2. **多張影像陣列 ($imaqs)**：若在拍照指令中加上 `$imaqs` 參數，除了存入 Capture 記憶體外，系統還會將影像附加至 `$imaqs` 陣列中。
   - **檢視**：`$imaqs` 陣列內的影像可在 MVCon 的使用者頁面中直接查看。
   - **時間戳記與飛拍應用**：系統會自動記錄 `$imaqs` 陣列中每張影像的「取得時間」。此特性常用於**飛拍 (Fly-capture)** 流程中，用以精確計算速度與位置。

---

## 📜 常用指令對照表

### 1. 攝影機初始化與組態設定
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **AddCam** | `$cam, $driver, $parameter` | 新增一個 Capture (攝影機) 裝置 |
| **Config** | `$cam, $parameter, $value` | 設定攝影機內部組態 |

### 2. 影像擷取與存取指令 (部分支援 Error Handling)
> **注意**：`Snap` 與 `Trigger` 等取像指令可能因硬體問題遺失畫面，遇到 `[[$label]]` 請比照 Error Handler 處理。使用 `$imaqs` 參數能結合多張影像陣列功能。
> **語法提示**：若此類指令不需要使用 `$imaqs` 陣列，但需要寫入後方的 `$msg` 或 `[[$label]]` 進行錯誤處理時，可以**保留逗號 `,,` 將欄位空下**來略過該參數 (例如：`Capture@Snap::Cam1,,[[Err]]msg`)。

| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **Snap** | `$cam(, $imaqs, [[$label]]$msg)` | 即刻擷取影像。若成功，系統自動將 IO `$(cam)#OK?` 設為 On。可選擇同時存入 `$imaqs` 陣列或設定錯誤跳轉 |
| **Trigger** | `$cam(, $imaqs, [[$label]]$msg)` | 等待硬體觸發取像。若成功，系統自動將 IO `$(cam)#OK?` 設為 On。可選擇同時存入 `$imaqs` 陣列或設定錯誤跳轉 |
| **UpdateImage** | `$cam(, $second)` | 更新軟體預覽影像 |
| **ImageSave** | `$cam, $file.png` | 將當前影像儲存為圖檔 |
| **LoadFile** | `$cam, $file.png` | 讀取硬碟上的圖檔，並放入指定的攝影機記憶體中 |

### 3. 多張影像陣列管理 ($imaqs 專用)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **LoadFile2Imaq** | `$imaqs, $pattern` | 批次將符合 `$pattern` 的影像讀檔到指定的影像陣列 (`$imaqs`) 中 |
| **LoadNextImaq** | `$cam, $imaqs` | 從影像陣列 (`$imaqs`) 中載入下一張影像，並放入指定的攝影機 (`$cam`) 記憶體中 |
| **ClearImaq** | `$imaqs` | 清除指定的影像陣列 (`$imaqs`) |
| **ResetImaqIndex** | `$imaqs(, $index)` | 重設影像陣列 (`$imaqs`) 的讀取索引位置 |

### 4. 影像校正與座標設定
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **CalibrateManual** | `$cam, $file.png` | 手動進行校正片校正，並將結果存檔 |
| **CalibrateLoad** | `$cam, $file.png` | 載入先前的校正設定檔 |
| **CoordSet** | `$cam, $points` | 設定影像座標系 (原點, X軸, Y軸)。`$points` 為包含 3 個點的陣列 |
| **CoordSet(2Edge)** | `$cam, $x-axis, $y-axis` | 使用 X、Y 軸建立座標系，原點為交點。`$x-axis` 與 `$y-axis` 為兩點構成的向量 |
| **CoordSet(Org+2Edge)** | `$cam, $org, $x-axis, $y-axis` | 指定 `$org` 為原點，並以 `$x-axis`、`$y-axis` 兩點向量決定軸向 |

### 5. ROI (感興趣區域) 與影像辨識
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **ROISet** | `$cam, $ROI_string` | 設定影像辨識範圍，`$ROI_string` 可從 MVCon UI 介面取得 |
| **ROISave** | `$cam, $file.png` | 將 ROI 內的影像存檔並進行特徵訓練，供後續指令 (如 `PatternMatchOne`) 使用 |
| **ROITranslate** | `$cam, $base, $new` | 以 `$base` 與 `$new` 的座標差異，平移 ROI 辨識框 |
| **PatternMatchOne** | `$cam, $file.png, $score, $point(, ANGLE=XX~YY)` | 執行特徵匹配。`$score` 為閥值，`$point` 為找到的座標。若找到，系統自動將 IO `$(point)#Find?` 設為 On |
| **PatternMatchOne(Fast)** | `$cam, $file.png, $score, $point(, ANGLE=XX~YY)` | 快速模式特徵匹配。`$score` 為閥值，`$point` 為找到的座標。若找到，系統自動將 IO `$(point)#Find?` 設為 On |
| **GeoMatchOne** | `$cam, $file.png, $score, $point(, ANGLE=XX~YY)` | 幾何輪廓匹配。`$score` 為閥值，`$point` 為找到的座標。若找到，系統自動將 IO `$(point)#Find?` 設為 On |
| **FindCircle(OutIn)** | `$cam, $point` | 由外向內找圓 (需先設定 ROI)。`$point` 為找到的座標。若找到，系統自動將 IO `$(point)#Find?` 設為 On |
| **FindCircle(InOut)** | `$cam, $point` | 由內向外找圓 (需先設定 ROI)。`$point` 為找到的座標。若找到，系統自動將 IO `$(point)#Find?` 設為 On |
| **FindEdge** | `$cam, $point` | 尋找邊界 (需先設定 ROI)。`$point` 為找到的座標。若找到，系統自動將 IO `$(point)#Find?` 設為 On |
| **FindGap** | `$cam, $point` | 尋找間隙 (需先設定 ROI)。`$point` 為找到的座標。若找到，系統自動將 IO `$(point)#Find?` 設為 On |
| **FindPoint** | `$cam, $point` | 尋找點位 (需先設定 ROI)。`$point` 為找到的座標。若找到，系統自動將 IO `$(point)#Find?` 設為 On |
| **Display2User** | `$cam, $control` | 將影像顯示到指定的 UI 面板控制項 (`$control`) 上 |

---

## 💡 AI 撰寫範例參考

**範例：拍攝影像、失敗處理並進行樣板辨識**
若使用者要求拍攝一張對位照片，並對照片進行模板配對（Pattern Match），如果拍不到就報錯：

```markdown
// 1. 觸發相機拍照，若出錯跳到 ErrCapture
Capture@Snap::Cam1,,[[ErrCapture]]Camera 1 Disconnected;

// 2. 進行特徵對位
// (分數存在 Score 變數中，結果座標點存在 ResultPt)
Capture@PatternMatchOne::Cam1,target.png,Score,ResultPt,ANGLE=-5~5;

// === 結束當前流程 ===
Thread@Stop::;

// ======== 錯誤處理區段 ========
Thread@ErrorHandler::[[ErrCapture]];
// 設定狀態與彈出訊息
Variable@Set::ErrorState,True;
Thread@Stop::True,Camera Snap Failed
```