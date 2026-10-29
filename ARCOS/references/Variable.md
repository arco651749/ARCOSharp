# ARCOS Variable 模組參考手冊

這是針對 **ARCOS Variable 模組** 專門設計的 Agent 技能指南。身為 ARCOS 自動化系統專家，當遇到需要暫存資料、執行邏輯計算、處理字串切割、或者進行類似資料庫的陣列主鍵檢索 (Search/Index) 時，請參照以下語法規則與指令。

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到「**變數宣告**」、「**參數加減**」、「**字串處理**」、「**陣列/清單(Array/List)**」、「**集合**」或「**資料檢索(LFindIndexSet)**」等關鍵字時，請自動套用本技能。

## 🛠️ 基本語法與格式規範
1. **指令句型**：所有的 Variable 指令都必須以 `Variable@` 開頭，後接指令名稱，再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。
3. **變數引用與內插**：參數中使用 `$變數名稱` 可直接代入其當前值。若需在字串中內插 (部份代入) 變數，請使用 `$(變數名稱)`。
4. **區段變數 (Section) 與多工實例化**：部分指令結尾可選填 `$section`（指定對應 Thread 號碼或分區編號，如 `1`、`2`、`Global`），用於隔離不同執行緒的獨立變數空間；若不填則預設為當前 Thread 的變數。
   - **多穴位並行測試核心應用**：當多個 Thread 同時執行同一個 Program（如 4 穴位測試機跑同一份 Program 1）時，MainThread 可預先透過 `Variable@Set::IP,192.168.0.1,1` 與 `Variable@Set::IP,192.168.0.2,2` 注入不同硬體通道。在 Program 1 內部直接取用 `$IP`，Thread 1 與 Thread 2 即可自動吃到各自所屬 Section 的值，達成「單一程式樣板、多執行緒實例化並行」之零重複代碼（DRY）架構。
5. **系統預設全域變數**：在 Global (全域) 空間中已預設定義了系統時間變數，包含 `$DATE` (日期，格式：`yyyymmdd`) 及 `$TIME` (時間，格式：`hhmmss`)。
6. **事件觸發機制 (Pub/Sub)**：Global (全域) 中的變數可以搭配 `Publish` 與 `Subscribe` 指令，來進行跨執行緒的事件發送與接收，並可使用 `Flush` 清除。

---

## 📜 常用指令對照表

### 1. 基本變數與數值運算
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **Set** | `$variable, $value(, $section)` | 宣告並設定變數的數值或字串 |
| **Inc** / **Dec**| `$variable(, $value, $section)` | 將變數值加 1 / 減 1，或加減指定的 `$value` |
| **Calculate**| `$var=formula(, $section)` | 將變數當作算式進行計算並賦值 |
| **Delete** | `$variable(, $section)` | 刪除指定的變數記憶 |
| **Rename** | `$oldname, $newname(, $section)`| 將變數重新命名 |
| **VariableSave** | `$file(, $var1, $var2...)` | 將指定變數存檔。預設為儲存所有變數；若有接續帶入變數參數，則僅儲存該特定變數 |
| **VariableLoad** | `$file` | 從檔案讀取變數 |

### 2. 字串處理 (String Actions)
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **Append** | `$variable, $value(, $section)` | 將 `$value` 串接附加到該變數的字串後方 |
| **Trim** | `$variable, $start, $end(, $section)`| 依照起迄位置擷取變數的字串內容 |
| **Slice** | `$variable, $char(, $section)` | 用指定字元 `$char` 將字串切分成陣列 |
| **Joint** | `$variable, $char(, $section)` | 將陣列用 `$char` 字符合併回單一字串 |

### 3. 陣列與清單操作 (Array/List Actions)
> **定義格式**：陣列定義使用中括號與分號，例如 `Variable@Set::Var,[A;B;C;D];`

| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **LResetIndex** | `$variable, $value(, $section)` | 強制將讀取 `$variable` 的索引游標跳至第 `$value` 個 |
| **LSetIndexValue**| `$variable, $index, $value` | 直接修改 `$variable` 陣列中索引為 `$index` 的值 |
| **LInsert** | `$var, $val, Before\|After[$v1...]` | 在陣列指定值的前/後插入新值 |
| **LPush** / **RPush**| `$variable, $value(, $section)`| 從前端 (L) 或尾端 (R) 塞入新值到陣列中 |
| **LPop** / **RPop** | `$variable(, $section)` | 從前端 (L) 或尾端 (R) 取出並移除陣列值 |
| **LRemove** | `$variable, $value(, $section)` | 清除陣列中所有等於 `$value` 的項目 |
| **LReverse** | `$variable(, $section)` | 將陣列內的元素順序完全反轉 |
| **LGetIndexSize**| `$variable(, $section)` | 取得該陣列目前的索引游標位置以及陣列總大小 |
| **LFindIndexSet** | `$variable, $value, $var1(, $var2...)` | 在 `$variable` 陣列中搜尋 `$value` 的目標值；若找到，則將 `$var1, $var2...` 等關聯陣列的內部索引同步切換至相同位置 (常做為主鍵檢索使用) |

### 4. 集合與點資料輔助
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **SAdd** | `$variable, $value(, $section)` | 將數值加入不重複的集合內 |
| **SUnion** / **SDiff** / **SIntersection** | `$var, $var1, $var2` | 執行集合的 聯集 / 差集 / 交集 運算 |
| **GetPointString**| `$var, $point(, $section)` | 將點資料轉換為字串存入變數 |
| **GetPointElement**| `$var=$axis($point)(, $section)` | 取得點位中指定的座標軸 $axis (如 X, Y, Z, U, V, W) 數值並存入變數 |


### 5.其他
| 指令 | 參數範例 | 說明 |
| :--- | :--- | :--- |
| **Select** | `$var=$logic ? $var1 : $var2` | 條件式變數選擇 (三元運算子) |
| **Shift** | `$var1, $var2(, $var3...), Shift=$num` | 變數資料位移 `$num` 位 (`$num` 可以為負值，代表反向位移) |
| **Publish** | `$var1(, $var2...)` | 發送 (觸發) 指定的全域變數事件 |
| **Subscribe** | `$var` | 阻擋等待並接收指定的變數事件 |
| **Flush** | `$var` | 清除變數發送的事件 |

---

## 💡 AI 撰寫進階語法與範例

### 📌 陣列的宣告與遍歷 (Foreach)
陣列變數宣告時**必須使用中括號 `[]` 並以分號 `;` 區隔**。當呼叫 `$Var` 時，系統會直接輸出目前內部索引所指向的值。
配合 `Thread@ForeachStart` 可以輕鬆走訪整個陣列：

```markdown
// 1. 宣告陣列變數
Variable@Set::Var,[0;1;2;3;4;5;6;7;8;9];

// 2. 啟動 Foreach 迴圈
Thread@ForeachStart::Loop;
// --- 迴圈區段開始 ---
System@Log Message::Current value is $Var;
// --- 迴圈區段結束，條件設為 Var 陣列 ---
Thread@ForeachLoop::Loop,Var
```

### 📌 資料庫級別的主鍵檢索 (LFindIndexSet)
當擁有多個平行對應的陣列 (類似資料表的 Column) 時，`LFindIndexSet` 可以把某個陣列當成 Key，搜尋命中後，**同步將其他變數的索引也移到相同的列 (Row)**，並將結果倒出來供後續程式使用。

**範例情境：當系統知道零件名稱為 `PartB`，要自動拉出對應的 `V`, `I`, `Range` 參數**

```markdown
// 1. 定義資料表的所有欄位陣列 (長度皆為 5)
Variable@Set::Item,[PartA;PartB;PartC;PartD;PartE];
Variable@Set::V,[1.0;2.0;3.0;2.0;2.0];
Variable@Set::I,[1.0;2.0;3.0;1.0;3.0];
Variable@Set::Range,[0.95~1.05;1.95~2.05;2.95~3.05;0.95~1.05;2.95~3.05];

// 2. 使用 LFindIndexSet 執行檢索
// 說明：在 Item 陣列找 "PartB"，若找到，將 V, I, Range 變數的索引也連動帶出
Variable@LFindIndexSet::Item,PartB,V,I,Range;

// 3. 呼叫結果
// 此時 $Item 為 PartB、$V 為 2.0、$I 為 2.0、$Range 為 1.95~2.05
System@Log Message::Selected Part: $Item, Voltage: $V, Current: $I, Spec: $Range
```