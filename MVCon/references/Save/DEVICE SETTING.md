# MVCon Device Settings

此檔案記錄機台初始化與新增之硬體設備配置指令。系統重啟或需重新載入硬體時，Agent 依此手冊還原配置。

> **Agent 維護原則**：
> 1. **動態驅動探索與參數核對（核心規則）**：MVCon 的硬體驅動可動態增減（只要在 `CoreModules/` 對應模組目錄下新增或刪除 Driver 資料夾）。Agent **嚴禁憑空猜測 Driver 名稱與參數格式**，在新增設備或協助除錯前，**必須先檢視對應模組目錄**，並使用 `view_file` 查閱該驅動資料夾內的 `Parameter.txt`，依其格式協助使用者驗證並組裝參數。
> 2. **探索目錄基準**：
>    - IO 驅動：`CoreModules/IO Module/<Driver>/Parameter.txt`
>    - 馬達驅動：`CoreModules/Motor Module/<Driver>/Parameter.txt`
>    - 相機驅動：`CoreModules/Capture Module/<Driver>/Parameter.txt`
>    - DAQ 驅動：`CoreModules/DAQ Module/<Driver>/Parameter.txt`
> 3. **記錄規則**：新增或更新設備時，在對應小節記錄完整指令。同名裝置以新設定覆蓋舊設定；新裝置則追加於後。
> 4. **讀取優先**：修改前必須先讀取現有配置，避免誤刪既有裝置。

## 1. IO Drivers
```markdown
// 語法：IO@AddIODriver::$io,$driver,$param...; IO@InitIOPort::$io
// 驅動來源：查閱 CoreModules/IO Module/ 下之目錄與各 Driver 的 Parameter.txt
```

## 2. Motors (Motion)
```markdown
// 語法：Motion@AddMotor::$motor,$driver,$param...
// 驅動來源：查閱 CoreModules/Motor Module/ 下之目錄與各 Driver 的 Parameter.txt
```

## 3. Cameras (Capture)
```markdown
// 語法：Capture@AddCam::$cam,$driver,$param...
// 驅動來源：查閱 CoreModules/Capture Module/ 下之目錄與各 Driver 的 Parameter.txt
```

## 4. DAQ Devices
```markdown
// 語法：DAQ@AddDAQ::$daq,$driver,$param...
// 驅動來源：查閱 CoreModules/DAQ Module/ 下之目錄與各 Driver 的 Parameter.txt
```
