# ARCOS DAQ 模組參考手冊

這是針對 **ARCOS DAQ (Data Acquisition) 模組** 專門設計的 Agent 技能指南。身為 ARCOS 自動化系統專家，當遇到需要擴充資料擷取卡、紀錄感測器波形、處理類比訊號輸出入、執行波形濾波比對或產生量測報告時，請參照以下語法規則與指令。

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到「**資料擷取/DAQ**」、「**波形/Waveform**」、「**類比訊號(Analog In/Out)**」、「**濾波/比對波形**」或「**產生量測報告/Report**」等關鍵字時，請自動套用本技能。

## 🛠️ 基本語法與格式規範
1. **指令句型**：所有的 DAQ 指令都必須以 `DAQ@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 分隔。

---

## 📜 常用指令對照表

### 1. DAQ 裝置控制與資料存取 (Device & Data Access)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **AddDAQ** | `$daq, $driver, $parameters` | 新增並初始化一個 DAQ 擷取設備 |
| **Start** / **Stop** | `$daq` | 開始或停止指定設備 `$daq` 的資料連續擷取動作 |
| **Wait** | `$daq(, TimeOut=$second, [[$label]]$message)`| 等待 DAQ 擷取到新一筆合法資料。若超過設定時間，將依據參數決定跳出 `$message` 異常提示或是跳往 `[[$label]]` 處理 |
| **AnalogIn** | `$daq(, $waveform)` | 取得當下最新的一筆類比輸入資料 (可指定存入波形變數 `$waveform`) |
| **AnalogInWaveForm**| `$daq(, $second, $waveform)` | 連續取得 `$second` 秒內的波形資料，並存入 `$waveform` |
| **AnalogOut** | `$daq, $channel0(, $channel1...)`| 輸出指定的單格/多通道類比數值 |
| **AnalogOutWaveForm** | `$daq, $waveform` | 依照變數 `$waveform` 記載的資料，連續輸出類比波形 |

### 2. 波形產生、存取與基礎運算 (Waveform Basics)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **WaveFormGenerate**| *(參見 HELP 手冊)* | 透過內建函數自訂產生模擬波形 |
| **WaveFormSave** | `$waveform, $file` | 將波形陣列資料儲存至實體檔案 `$file` 中 |
| **WaveFormLoad** | `$waveform, $file` | 從檔案讀取歷史波形資料放入變數中 |
| **Calculate** | `$waveform = $waveform1 {+-\*/} $waveform2` | 波形之間的加、減、乘、除或位移等數學運算 |
| **MergeWaveForm** | `$WaveForm, $WaveForm1, $WaveForm2(, ...)` | 將多組散落的波形依照時間或相位合併為單一波形 |

### 3. 訊號過濾與範圍比對 (Filtering & Comparison)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **Filter** | `$waveform, $filter(, $parameter)` | 套用指定的數位濾波器 (如低通、高通或均值濾波) 來平滑或整理波形 |
| **CompareAvgInRange**| `$waveform, $max, $min(, $start, $end)` | 計算指定區間內的波形平均值，並比對是否落在 `$min` 與 `$max` (容許範圍) 中，結果寫回IO |
| **CompareMaxMinInRange**| `$waveform, $max, $min(, $start, $end)` | 檢查指定區間內波形的波峰與波谷極值，是否皆落在合格的上下限內，結果寫回IO |
| **CompareCoverBy** | `$waveform, $coverbywave` | 比對當前波形軌跡是否完全被 `$coverbywave` 這組標準規格波形所覆蓋 (包絡線檢測)，結果寫回IO |

### 4. 報表產生與顯示 (Reporting & Interface)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **Display2User** | `$waveform, $control` | 將擷取到或計算後的波形畫在使用者介面 (User Panel) 的圖表控制項 `$control` 上 |
| **Report(AddWaveForm)**| `$waveform1(, $waveform2...)` | 將指定的波形圖預先儲存到系統待產的量測報告中 |
| **Report(AddRecord)**| `$record1(, $record2...)` | 將文數字型的記錄資料追加至報告內 |
| **Report(Output)** | `$file` | 將先前的波形與紀錄一併整合成正式報告檔案 `$file` 輸出 |
| **ReportViewer** | `$file` | 開啟報表瀏覽器查看生成的結果檔 |

---

## 💡 AI 撰寫範例參考

**範例：感測器類比波形讀取、過濾與數值判定**
當使用者要求擷取 2 秒鐘的 DAQ 訊號，並過濾雜訊後判定其峰值是否在安全範圍 (小於 5.0，大於 1.0) 內，若失敗則停機報錯：

```markdown
// 1. 開始波形擷取，連續收集 2 秒到 SensorWave 變數中
DAQ@Start::MyDAQ;
DAQ@AnalogInWaveForm::MyDAQ,2,SensorWave;

// 2. 擷取完畢後停止
DAQ@Stop::MyDAQ;

// 3. 套用高頻濾波器 (LowPassFilter) 去除訊號毛刺
DAQ@Filter::SensorWave,LowPassFilter;

// 4. 將乾淨的波形畫在 UI 面板的圖表上給使用者觀看
DAQ@Display2User::SensorWave,GraphControl_1;

// 5. 比對過濾後的波形峰谷值，若任一值不在 1.0~5.0 區間內
DAQ@CompareMaxMinInRange::SensorWave,5.0,1.0
```