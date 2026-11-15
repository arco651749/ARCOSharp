# ARCOS UserPanel 全控件標準範本與多面板切換指南

本文件整理所有受 ARCOSharp 探索與控制引擎支援的 **10 類標準 Avalonia 控件／指示器寫法**，以及 **多 `UserPanel.csvi` 同時載入與切換（`System@UserPanelOpen` + `User@SwitchUserPanel` + `Panel:Control`）** 的標準語法。

附屬測試面板（已編譯為可動態載入之 CSVI 3.0 套件，位於 `PANELS/`）：
1. [UserPanel_Templates.csvi](file:///d:/AntiGravity/ARCOSharp/PANELS/UserPanel_Templates.csvi)：展示全 10 類標準控件與指示器範本。
2. [UserPanel_StationB.csvi](file:///d:/AntiGravity/ARCOSharp/PANELS/UserPanel_StationB.csvi)：模擬第二工站面板（刻意包含同名控件如 `BtnConfirm`、`TxtHmiMessage`、`ImgCam1`、`PlotDaq1` 與專屬控件 `TogClampB`、`CmbFilterMode`），用來驗證多面板切換與同名隔離。

---

## 1. 控件設計兩條核心規則

1. **必須指定唯一的 `x:Name`**：
   載入任何 `UserPanel*.csvi` 時，`MainWindow` 會自動遞迴掃描邏輯樹，將所有具名控件登記進 `UserPanelEngine`（若未設 `x:Name` 則回退讀取 `AutomationProperties.AutomationId`）。
2. **`Tag` 屬性決定資料／裝置自動連動**：
   * `Tag="IO:<Port>"`：`Ellipse` 與 `ToggleButton` 會在執行 `User@Param2UserPanel` 時自動同步對應 IO 狀態（ON `#10B981` / OFF `#64748B`）。
   * `Tag="VAR:<VarKey>"`：`TextBox` 與 `TextBlock` 會在 `User@Param2UserPanel` 時自動顯示系統變數內容。
   * `Tag="CAPTURE:<CamName>"`：搭配 `Image`，供 `Capture@Display2User` 顯示指定相機影像。
   * `Tag="DAQ:<WaveName>"`：搭配 `AvaPlot` 或佔位 `Border`，供 `DAQ@Display2User` 繪製波形圖表。

---

## 2. 全 10 類標準控件／指示器範本

### (1) `Button`（瞬間動作按鍵｜`Role = Control`）
```xml
<Button x:Name="BtnDemo" Content="Click Demo" Background="#2563EB" Foreground="#FFFFFF" CornerRadius="4" Padding="12,6"/>
```
* 常用指令：
  * `User@ButtonText::BtnDemo,Press Now;`
  * `User@ButtonValue::BtnDemo,ON;` / `User@ButtonTrigger::BtnDemo,ON;`
  * `User@ButtonBlinking::BtnDemo,1;`（`0` 停止閃爍）
  * `User@DisableControl::BtnDemo;` / `User@EnableControl::BtnDemo;`

### (2) `ToggleButton`（保持型開關｜`Role = Control`）
```xml
<ToggleButton x:Name="TogVacuum" Tag="IO:P0.0" Content="Vacuum ON/OFF" Padding="12,6"/>
```
* 常用指令：
  * `User@ButtonValue::TogVacuum,ON;`（接受 `ON` / `OFF` / `1` / `0` / `TRUE` / `FALSE`）
  * `User@Param2UserPanel::;`（從 `IO:P0.0` 同步開關狀態）

### (3) `CheckBox`（勾選框｜`Role = Control`）
```xml
<CheckBox x:Name="ChkBypass" Content="Bypass Sensor Check"/>
```
* 常用指令：
  * `User@ButtonValue::ChkBypass,ON;`
  * `User@GetControlValue::ChkBypass,$BypassState;`

### (4) `TextBox` — 輸入參數欄（`Role = Control`）
```xml
<TextBox x:Name="TxtRecipe" Tag="VAR:RecipeName" Text="DefaultRecipe"/>
```
* 常用指令：
  * `User@TextBoxValue::TxtRecipe,Recipe_A;`
  * `User@TextBoxTrigger::TxtRecipe,Recipe_B;`
  * `User@GetControlValue::TxtRecipe,$CurRecipe;`

### (5) `TextBox` — 唯讀狀態顯示欄（`IsReadOnly="True"`｜`Role = Indicator`）
```xml
<TextBox x:Name="TxtHmiMessage" IsReadOnly="True" Text="Templates Ready"/>
```
* 常用指令：
  * `User@TextBoxValue::TxtHmiMessage,Running Step 1;`
  * `User@TextBoxBlinking::TxtHmiMessage,1;`
  * `User@HideControl::TxtHmiMessage;` / `User@ShowControl::TxtHmiMessage;`

### (6) `ComboBox` / `TextRing`（下拉選單｜`Role = Control`）
```xml
<ComboBox x:Name="CmbRunMode" SelectedIndex="0" HorizontalAlignment="Stretch">
    <ComboBoxItem Content="AUTO"/>
    <ComboBoxItem Content="DRY_RUN"/>
    <ComboBoxItem Content="STEP"/>
</ComboBox>
```
* 常用指令：
  * `User@TextRingValue::CmbRunMode,DRY_RUN;`（可直接指定選項文字或 `0`/`1`/`2` 索引）
  * `User@TextRingTrigger::CmbRunMode,STEP;`
  * `User@TextRingBlinking::CmbRunMode,1;`

### (7) `TabControl`（面板子分頁容器｜`Role = Control`）
```xml
<TabControl x:Name="MainTab" SelectedIndex="0">
    <TabItem x:Name="TabBasic" Header="Basic Controls">...</TabItem>
    <TabItem x:Name="TabMedia" Header="Capture &amp; DAQ Indicators">...</TabItem>
</TabControl>
```
* 常用指令：
  * `User@TabValue::MainTab,1;`（或填分頁名稱 `TabMedia`）
  * `User@TabTrigger::MainTab,TabBasic;`

### (8) `TextBlock`（純文字指標｜`Role = Indicator`）
```xml
<TextBlock x:Name="IndStatus" Tag="VAR:StepStatus" Text="IDLE" FontWeight="Bold"/>
```
* 常用指令：
  * `User@SetControlValue::IndStatus,PASS;`
  * `User@GetControlValue::IndStatus,$OutStatus;`

### (9) `Ellipse`（LED 圓形燈號｜`Role = Indicator`）
```xml
<Ellipse x:Name="LedPort0" Tag="IO:P0.0" Width="12" Height="12" Fill="#64748B"/>
```
* 常用指令：
  * `User@Param2UserPanel::;`（同步 `IO:P0.0`：`#10B981` 綠 / `#64748B` 灰）
  * `User@ButtonValue::LedPort0,ON;` / `User@ButtonBlinking::LedPort0,1;`

### (10) `Image` 與 `AvaPlot` / `Border`（影像與波形顯示器｜`Role = Indicator`）
```xml
<!-- 相機影像顯示器 -->
<Image x:Name="ImgCam1" Tag="CAPTURE:Cam1" Stretch="Uniform"/>

<!-- DAQ 波形顯示器 (執行 DAQ@Display2User 時會自動升級為互動式 ScottPlot AvaPlot) -->
<Border x:Name="PlotDaq1" Tag="DAQ:Wave1" Background="#0F172A" MinHeight="180"/>
```
* 常用指令：
  * `Capture@Display2User::Cam1,ImgCam1;`
  * `DAQ@Display2User::Wave1,PlotDaq1;`

---

## 3. 多面板載入、切換與跨面板隔離測試腳本

可直接在 Sequencer 或 Panel CMD 執行以下腳本，測試同時掛載兩個測試用 `UserPanel*.csvi`：

```text
// 1. 同時載入兩個自訂 UserPanel 並指定別名 (第二參數為別名；可加 Background 保持原焦點)
System@UserPanelOpen::UserPanel_Templates.csvi,Templates;
System@UserPanelOpen::UserPanel_StationB.csvi,StationB;

// 2. 跨面板查詢所有已註冊控件 (加上 Panel=* 會回傳 Panel:Control 格式)
User@QueryControls::$AllCtrls,Panel=*,Role=All;

// 3. 切換焦點回 Templates 面板並寫入標準控件
User@SwitchUserPanel::Templates;
User@ButtonText::BtnDemo,Ready To Go;
User@ButtonValue::TogVacuum,ON;
User@ButtonValue::ChkBypass,ON;
User@TextBoxValue::TxtRecipe,Recipe_Gold_V3;
User@TextRingValue::CmbRunMode,DRY_RUN;
User@TabValue::MainTab,1;
User@SetControlValue::IndStatus,RUNNING;

// 4. 在不切換畫面的情況下，背景直指 StationB 同名元件 (TxtHmiMessage / BtnConfirm) 更新
User@SetControlValue::StationB:TxtHmiMessage,Updated From Background;
User@ButtonText::StationB:BtnConfirm,B-Armed;
User@TextRingValue::StationB:CmbFilterMode,MEDIAN;

// 5. 搭配硬體 / 相機 / DAQ 更新兩張面板的畫布
IO@AddIODriver::DriverName=MockIO,DeviceName=MainIO;
IO@SetPort::P0.0,0.02,ForceUpdate;
User@Param2UserPanel::;

Capture@AddCam::CamTest,MockCamera;
Capture@Snap::CamTest;
Capture@Display2User::CamTest,Templates:ImgCam1;
Capture@Display2User::CamTest,StationB:ImgCam1;

DAQ@AddDAQ::MyDAQ,MockDAQ;
DAQ@WaveFormGenerate::Wave1,Constant,Rate=1000,Count=500,Amp=1050;
DAQ@Display2User::Wave1,Templates:PlotDaq1;
DAQ@Display2User::Wave1,StationB:PlotDaq1;

// 6. 切換到 StationB 驗證剛才背景寫入的結果，最後可選擇關閉 StationB
User@SwitchUserPanel::StationB;
User@GetControlValue::StationB:TxtHmiMessage,$StationBMsg;
User@GetControlValue::Templates:TxtRecipe,$TemplatesRecipe;
// System@UserPanelClose::StationB;
```
