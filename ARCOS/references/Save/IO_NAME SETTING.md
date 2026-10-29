# ARCOS IO Name Settings

此檔案記錄自定義 IO 名稱映射。格式為 `Port=名稱`，以行或逗號分隔。

> **命名規範與維護原則**：
> - **Input IO**：名稱尾端必須且只能帶有 `?`（如 `StartBtn?`、`DoorSensor?`）。
> - **Output IO**：名稱採用動作動詞/名詞（如 `CylinderPush`、`AlarmBuzzer`），嚴禁帶有 `?`。
> - **鍵值去重**：同一個 Port 若重複定義，以最新設定為準；異動前務必先讀取並保留既有其他 Port 映射。

## Port Mapping

```ini
# 範例參考:
# P0.0=StartBtn?,P0.1=StopBtn?,P0.2=CylinderSensor?,P1.0=CylinderValve,P1.1=AlarmBuzzer
```
