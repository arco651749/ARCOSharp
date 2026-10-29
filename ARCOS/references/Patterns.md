# ARCOS 機台標準實戰樣板庫 (Canonical Automation Recipes)

本手冊收錄自動化機台開發中最核心的四大標準架構。
為符合現場工程需求，每個樣板皆區分為**「快速測試版 (Debug / Commissioning)」**與**「產線量產版 (Mass Production Ready)」**，Agent 可依據使用者的任務情境靈活套用。

---

## 🎯 漸進式嚴謹度套用準則 (Progressive Rigor Rule)

| 開發階段 | 使用者語境特徵 | Agent 應採取的生成模式 |
| :--- | :--- | :--- |
| **快速單動/測試** | 「快速測一下」、「動作看看」、「Debug」 | **快速測試版**：指令精簡、無超時攔截、快速看見動作。 |
| **整合試俥** | 「幫我串流程」、「加上超時報警」 | **試俥版**：加入 `TimeOut` 參數，超時直接彈窗提示工程師。 |
| **產線量產** | 「產線用」、「標準站點」、「不可卡機停線」 | **量產健壯版**：加入 `[[$label]]` 攔截、`Thread@ErrorHandler` 自動重試、門型避障與狀態復歸。 |

---

## 樣板 1：雙向氣缸到位檢測與超時重試 (Cylinder Actuation & Retry)

適用場景：夾爪、壓合缸、頂升定位缸、進退料推桿。

### A. 快速測試版 (Quick Test)
```markdown
// 快速切換電磁閥並等待到位 (無超時卡控)
IO@ClrPort::CylRetract;
IO@SetPort::CylPush;
IO@WaitPortOn::CylExtendSensor?
```

### B. 產線量產版 (Production Level: 具備重試與異常攔截)
```markdown
// 1. 初始化重試計數器
Variable@Set::CylRetry,0;

Thread@Label::ActuateLoop;
// 2. 驅動氣缸伸出
IO@ClrPort::CylRetract;
IO@SetPort::CylPush;

// 3. 限時等待 2 秒，超時跳轉錯誤常式
IO@WaitPortOn::CylExtendSensor?,2,[[CylTimeout]]Cylinder Extend Timeout;
System@Log Message::Cylinder Extended Successfully;
Thread@Stop::;

// ======== 錯誤處理與自動重試常式 ========
Thread@ErrorHandler::[[CylTimeout]];
Variable@Inc::CylRetry;
Thread@If::CylRetry <= 3;
   System@Log Message::Cylinder extend timeout, retrying $(CylRetry)/3...;
   // 縮回退讓
   IO@ClrPort::CylPush;
   IO@SetPort::CylRetract;
   IO@WaitPortOn::CylRetractSensor?,2;
   System@Delay::0.2;
   // 跳回重新嘗試
   Thread@Jump::ActuateLoop;
Thread@Else::;
   // 重試 3 次仍失敗，安全復歸並發出致命警報停止
   IO@ClrPort::CylPush;
   IO@SetPort::CylRetract;
   Thread@Stop::True,Cylinder Fatal Error After 3 Retries;
Thread@EndIf::
```

---

## 樣板 2：門型軌跡取放料 (Pick & Place Arch Motion & Vacuum)

適用場景：SCARA 手臂、笛卡爾直角滑台取料、放料、移載機構。

### A. 快速測試版 (Quick Test)
```markdown
// 直接點到點移動 (無安全高度退點)
Motion@GoPoint::Robot,500,PickPos;
Motion@WaitMotor::Robot;
IO@SetPort::VacuumValve;
System@Delay::0.5;
Motion@GoPoint::Robot,500,PlacePos;
Motion@WaitMotor::Robot;
IO@ClrPort::VacuumValve
```

### B. 產線量產版 (Production Level: 門型避障 + 真空檢知 + 異常脫困)
```markdown
// 1. 先抬升至安全退點高度 Z(50)，避免橫移掃斷工件
Motion@GoPoint::Robot,500,PickPos@Z(50);
Motion@WaitMotor::Robot,TimeOut=5,[[MotionErr]]Robot Move SafeZ Fail;

// 2. 在安全高度下，橫移至取料點上方 XY
Motion@GoPoint::Robot,500,PickPos@XY;
Motion@WaitMotor::Robot,TimeOut=5,[[MotionErr]]Robot Move XY Fail;

// 3. 落地：下放 Z 軸至取料深度
Motion@GoPoint::Robot,300,PickPos@Z;
Motion@WaitMotor::Robot,TimeOut=5,[[MotionErr]]Robot Descend Fail;

// 4. 開啟真空電磁閥，並嚴格等待真空負壓建立 (限時 1.5 秒)
IO@SetPort::VacuumValve;
IO@WaitPortOn::VacuumSensor?,1.5,[[VacuumErr]]Vacuum Build Failed;

// 5. 吸附成功，垂直起拔回安全高度
Motion@GoPoint::Robot,300,PickPos@Z(50);
Motion@WaitMotor::Robot,TimeOut=5,[[MotionErr]]Robot Ascend Fail;

// 6. 橫移至放料點上方 XY
Motion@GoPoint::Robot,500,PlacePos@XY;
Motion@WaitMotor::Robot,TimeOut=5,[[MotionErr]]Robot Move Place XY Fail;

// 7. 下降至放料深度
Motion@GoPoint::Robot,300,PlacePos@Z;
Motion@WaitMotor::Robot,TimeOut=5,[[MotionErr]]Robot Descend Place Fail;

// 8. 破真空釋放工件，確認脫料開關
IO@ClrPort::VacuumValve;
IO@SetPort::BlowValve,0.2;
IO@WaitPortOff::VacuumSensor?,1.0;

// 9. 脫料完成，手臂退回安全高度
Motion@GoPoint::Robot,500,PlacePos@Z(50);
Motion@WaitMotor::Robot,TimeOut=5,[[MotionErr]]Robot Exit Fail;
Thread@Stop::;

// ======== 錯誤處理與脫困常式 ========
Thread@ErrorHandler::[[VacuumErr]];
// 吸取失敗：關閉真空，手動退出回安全高度後停機
IO@ClrPort::VacuumValve;
Motion@GoPoint::Robot,200,PickPos@Z(50);
Motion@WaitMotor::Robot;
Thread@Stop::True,Part Pick Failed: Vacuum Not Established;

Thread@ErrorHandler::[[MotionErr]];
// 運動超時異常：緊急煞車並記錄狀態
Motion@Stop::Robot;
Thread@Stop::True,Robot Motion Interrupted By Timeout
```

---

## 樣板 3：Tray 盤陣列走訪與滿料換盤 (Pallet Scanning & Index Management)

適用場景：IC 托盤、螺絲盤、料匣陣列取放。

### A. 快速測試版 (Quick Test)
```markdown
// 每次呼叫自動走下一個陣列點
Motion@GoPallet::Robot,500,TrayPallet;
Motion@WaitMotor::Robot
```

### B. 產線量產版 (Production Level: 走訪、滿盤判定、換盤交握與索引歸零)
```markdown
// 1. 執行 Pallet 步進移動 (內部索引自動 +1)
Motion@GoPallet::Robot,500,TrayPallet;
Motion@WaitMotor::Robot,TimeOut=10,[[PalletErr]]Pallet Motion Timeout;

// 2. 判斷是否已走到最後一顆點位 (系統自動舉牌 $(point)#Last?)
Thread@If::TrayPallet#Last?;
   System@Log Message::Tray is exhausted, requesting auto tray exchange;
   // 抬起至安全高度
   Motion@GoPoint::Robot,500,TrayPallet@Z(50);
   Motion@WaitMotor::Robot;
   
   // 通知送料機構換盤，等待新盤定位到位 (限時 30 秒)
   IO@SetPort::TrayChangeRequest;
   IO@WaitPortOn::TrayReadySensor?,30,[[TrayTimeout]]Tray Exchange Timeout;
   IO@ClrPort::TrayChangeRequest;
   
   // 換盤完成，重設料盤索引為 0，重新開始新循環
   Point&Path@ResetPalletIndex::TrayPallet,0;
   System@Log Message::New Tray Loaded and Index Reset to 0;
Thread@EndIf::;
Thread@Stop::;

// ======== 錯誤處理常式 ========
Thread@ErrorHandler::[[PalletErr]];
Motion@Stop::Robot;
Thread@Stop::True,Pallet Motor Move Error;

Thread@ErrorHandler::[[TrayTimeout]];
IO@ClrPort::TrayChangeRequest;
Thread@Stop::True,Tray Exchange Timeout Waiting For Feeder
```

---

## 樣板 4：SubThread 背景守護與即時安全監控 (Safety & Alarm Daemon)

適用場景：光柵遮斷即刻暫停、氣壓不足警報、三色燈閃爍、心跳監控。

### A. 快速測試版 (Quick Test)
```markdown
// 一般並行監控 (隨主執行緒暫停而暫停)
Thread@SubThreadStart::Blinker;
   IO@Flash(OnOff)::YellowLight,0.5,0.5;
Thread@SubThreadEnd::Blinker
```

### B. 產線量產版 (Production Level: 使用 `&` 後綴，不受 SystemPause 影響之長效守護行程)
```markdown
// 1. 宣告常態運作旗標
Variable@Set::GuardEnable,1;

// 2. 啟動守護行程 (名稱帶 & 符號，代表不受 SystemPause 影響，全時監控)
Thread@SubThreadStart::SafetyGuard&;
   Thread@Label::GuardLoop;
   System@Delay::0.05;

   // 檢查氣壓感應器：若氣壓不足 (OFF) 立即全域暫停主流程並鳴笛
   Thread@If::~AirPressureSensor?;
      System@Log Error::Main Air Pressure Dropped! Triggering SystemPause;
      System@SystemPause::;
      IO@SetPort::AlarmBuzzer;
      // 等待氣壓恢復正常
      IO@WaitPortOn::AirPressureSensor?,TimeOut=120;
      IO@ClrPort::AlarmBuzzer;
      System@SystemResume::;
      System@Log Message::Air Pressure Restored. System Resumed;
   Thread@EndIf::;

   // 檢查安全光柵：若遮斷 (OFF) 立即暫停
   Thread@If::~LightCurtainSensor?;
      System@SystemPause::;
      IO@SetPort::YellowBlink;
      // 等待操作人員按下復歸按鈕
      IO@WaitPortOn::SafetyResetBtn?,TimeOut=60;
      IO@ClrPort::YellowBlink;
      System@SystemResume::;
   Thread@EndIf::;

   // 依據控制旗標決定是否持續背景循環
   Thread@If::GuardEnable == 1;
      Thread@Jump::GuardLoop;
   Thread@EndIf::;
Thread@SubThreadEnd::SafetyGuard&;
Thread@Stop::
```
