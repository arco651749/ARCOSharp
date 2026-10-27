# ARCOS / MVCON 原版 UI 與系統截圖永久索引庫

本目錄收錄由原版 LabVIEW ARCOS-2022 提取之全套人機介面 (HMI)、分頁配置與區塊圖截圖，作為 C# Avalonia 跨平台版本 1:1 像素級對齊之權威設計依據。

---

## 📑 截圖清單與功能說明

| 編號 | 檔案名稱 | 對應分頁 / 模組 | 核心畫面內容與對齊重點 |
| :---: | :--- | :--- | :--- |
| **01** | [01_UserTab_MainSequencer.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/01_UserTab_MainSequencer.png) | **User Tab (主畫面)** | • 左側大畫布預留 User Front Panel SubPanel<br>• 左下角 `PAUSE`、亮黃 `!!STOP!!`、綠色 `OK` 燈號與即時日誌流<br>• 右側常駐 Sequencer 藍頭表格、紅指針 PC 條、`OPERATOR`/`SUPERVISOR` 切換與 `▶ Run` 控制列 |
| **02** | [02_Capture_Vision.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/02_Capture_Vision.png) | **Capture (視覺相機)** | • 頂部 `CAM0` ~ `CAM11` 12 隻相機切換分頁<br>• 左側 24 顆實體/虛擬 IO 狀態指示燈 (P0.0~P4.4)<br>• 中央 IMAQ 影像檢視區與左側繪圖量測工具列<br>• 底部手動寸動 Jog、7 軸坐標即時回饋、`Go Point` / `Go Pallet` 按鈕 |
| **03** | [03_IMAQ_Array.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/03_IMAQ_Array.png) | **IMAQ Array (多幀飛拍庫)** | • 左側連續取像清單與微秒級時間戳表格 (`00:00:00.000`)<br>• 頂部 `GetList`、`IMAQLast?` 綠色旗標與 `IMAQIndex`<br>• 右側大尺寸飛拍影像回放與邊界檢視視窗 |
| **04** | [04_Point_And_Path.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/04_Point_And_Path.png) | **Point & Path (空間點位)** | • 三欄式佈局：點位清單 ➔ 7 軸數值 (X, Y, Z, U, V, W, H) ➔ 連續 Path 軌跡點陣列<br>• 滿盤旗標 `LastOne?` 燈號與目前游標索引 |
| **05** | [05_ModulesInfo_System.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/05_ModulesInfo_System.png) | **Modules Info (System)** | • 子分頁：System, IO, Motion, DAQ, MultiStation, ExternalModule<br>• 左側 SystemModuleCMD、工作目錄、紅字 Errors、Logs 全域即時串流<br>• 右側生產週期時間記錄表 (RecordS) |
| **06** | [06_ModulesInfo_IO.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/06_ModulesInfo_IO.png) | **Modules Info (IO)** | • 左側 `IODeviceList` 硬體列表與 `IOModuleCMD`<br>• 右側大型動態 SubPanel 容器 (嵌入選中硬體的獨立前面板) |
| **07** | [07_Threads_Matrix.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/07_Threads_Matrix.png) | **Threads (執行緒矩陣)** | • 子分頁：`Thread 1~6`, `Thread 7~12`, `SubThread`<br>• 左欄：`Thread Main` (Thread 0) 獨立指令表與 PC 紅色指針<br>• 右側 2x3 矩陣：6 條執行緒並行跳動的獨立指令 DataGrid 與 PC 指針 |
| **08** | [08_Parameter_And_Variable.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/08_Parameter_And_Variable.png) | **Parameter & Variable (變數)** | • 左側 `Section` 分區篩選下拉、手動寫入鍵值與磁碟存檔<br>• 中央大型 2 欄式 Key / Value 變數表格<br>• 右側全域系統參數與帶索引陣列值表格 |
| **09** | [09_WaveForm_DAQ.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/09_WaveForm_DAQ.png) | **WaveForm (示波器/DAQ)** | • 左側通道清單與 5 筆波形數據塊 (`DataIn`, `t0`, `dt`, `Y`)<br>• 右側高速數位示波器圖表 (支援 X 時間軸與 Y 振幅軸，下附圖例與游標切換) |
| **10** | [10_Remote_Telnet.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/10_Remote_Telnet.png) | **Remote (MC Protocol)** | • 左側 `PLC(MC Protocol) Port: 2800 ~ 2812` 13 組埠號清單<br>• 中央遠端指令鏡像與執行狀態<br>• 右側遠端指令快速測試範本按鈕 (`$SetIO`, `$GetIO`, `$Execute` 等) 與即時 Socket 連線測試器 |
| **11** | [11_ARCOS_Remoter_Standalone.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/11_ARCOS_Remoter_Standalone.png) | **ARCOS-Remoter.vi** | • 獨立伴隨守護進程 (Sidecar) 介面截圖 |
| **12** | [12_Motor_Command_Cases.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/12_Motor_Command_Cases.png) | **Motor Command Cases** | • `Motor_Command Execute.vi` 16 個 Case 運動狀態機區塊圖 |
| **13** | [13_ARCOS_2022_BlockDiagram.png](file:///d:/AntiGravity/MVCON2CSharp/docs/ui_references/13_ARCOS_2022_BlockDiagram.png) | **ARCOS-2022.vi 區塊圖** | • 透過 G-AI MCP 提取之原版主程式完整底層架構區塊圖 (包含 13 個 While Loop 與 Event Structure) |
