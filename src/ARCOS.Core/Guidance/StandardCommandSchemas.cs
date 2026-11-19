using System;
using System.Collections.Generic;
using System.Linq;

namespace ARCOS.Core.Guidance;

/// <summary>
/// ARCOSharp 標準指令參數 Schema 註冊表（先導三模組：IO 14 條 / SYSTEM 21 條 / THREAD 31 條，共 66 條）
/// 全面採用「資料型別即鍵名」規約：
///   - IoEngine: io / ios          - TimerEngine: timer          - CounterEngine: counter
///   - RecordEngine: rec           - VariableEngine: var / vars  - PointEngine: pt / pts
///   - ImageEngine: imaq / imaqs   - DaqEngine: wav / wavs       - Driver: dev / driver
///   - UserPanel: panel            - Thread/Sub/Lock/Label: thread / threads / sub / subs / lock / label
/// 對應 docs/COMMAND_SYNTAX_AND_SCHEMA_SPEC.md。
/// </summary>
public static class StandardCommandSchemas
{
    private static readonly Dictionary<string, CommandSchema> _schemas = BuildAll();

    public static IReadOnlyDictionary<string, CommandSchema> All => _schemas;

    public static CommandSchema? Get(string commandKey)
    {
        if (string.IsNullOrWhiteSpace(commandKey)) return null;
        return _schemas.TryGetValue(commandKey.Trim(), out var schema) ? schema : null;
    }

    public static IReadOnlyList<CommandSchema> GetByModule(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName)) return Array.Empty<CommandSchema>();
        string prefix = moduleName.Trim() + "@";
        return _schemas.Values
            .Where(s => s.CommandKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
    }

    private static Dictionary<string, CommandSchema> BuildAll()
    {
        var dict = new Dictionary<string, CommandSchema>(StringComparer.OrdinalIgnoreCase);

        void Reg(CommandSchema schema, params string[] keyAliases)
        {
            dict[schema.CommandKey] = schema;
            foreach (var alias in keyAliases)
            {
                dict[alias] = schema;
            }
        }

        ParamSpec Req(string name, ParamValueType type, int? legacyIdx, string desc, params string[] aliases) => new()
        {
            Name = name,
            Aliases = aliases,
            LegacyIndex = legacyIdx,
            Direction = ParamDirection.Input,
            Required = true,
            ValueType = type,
            DisplayTier = ParamDisplayTier.Primary,
            Description = desc
        };

        ParamSpec Opt(string name, ParamValueType type, string defaultVal, int? legacyIdx, string desc,
            ParamDisplayTier tier = ParamDisplayTier.Secondary, bool alwaysExplicit = false, params string[] aliases) => new()
        {
            Name = name,
            Aliases = aliases,
            LegacyIndex = legacyIdx,
            Direction = ParamDirection.Input,
            Required = false,
            DefaultValue = defaultVal,
            AlwaysExplicit = alwaysExplicit,
            ValueType = type,
            DisplayTier = tier,
            Description = desc
        };

        ParamSpec Out(string name, ParamValueType type, string desc, params string[] aliases) => new()
        {
            Name = name,
            Aliases = aliases,
            Direction = ParamDirection.Output,
            Required = false,
            ValueType = type,
            DisplayTier = ParamDisplayTier.Primary,
            Description = desc
        };

        ParamSpec OutPos(string name, ParamValueType type, int? legacyIdx, string desc, bool required = false, params string[] aliases) => new()
        {
            Name = name,
            Aliases = aliases,
            LegacyIndex = legacyIdx,
            Direction = ParamDirection.Output,
            Required = required,
            ValueType = type,
            DisplayTier = ParamDisplayTier.Primary,
            Description = desc
        };

        ParamSpec ReqCrit(string name, ParamValueType type, int? legacyIdx, string desc, params string[] aliases) => new()
        {
            Name = name,
            Aliases = aliases,
            LegacyIndex = legacyIdx,
            Direction = ParamDirection.Input,
            Required = true,
            ValueType = type,
            DisplayTier = ParamDisplayTier.Critical,
            Description = desc
        };

        // =========================================================================
        // 1. IO 模組（14 條指令）— 點位一律採 io / ios，控制卡採 card
        // =========================================================================

        Reg(new CommandSchema
        {
            CommandKey = "IO@AddIODriver",
            Summary = "宣告並註冊硬體 IO 卡驅動實例與實體埠映射",
            Parameters =
            {
                Req("card", ParamValueType.DriverName, 0, "IO 卡裝置代號 (如 IO1)", "name", "deviceName"),
                Req("driver", ParamValueType.String, 1, "驅動型號 (如 MockIO)", "driverType"),
                Opt("device", ParamValueType.String, "", null, "實體裝置/連線位址 (COM 埠、IP、OPC-UA、卡號等)", ParamDisplayTier.Secondary, false, "DEVICE"),
                Opt("outStart", ParamValueType.PortName, "P0.0", null, "實體輸出起始點位", ParamDisplayTier.Secondary, false, "OUTSTART"),
                Opt("outPorts", ParamValueType.Integer, "8", null, "實體輸出點數", ParamDisplayTier.Secondary, false, "OUTPORTS"),
                Opt("inStart", ParamValueType.PortName, "P1.0", null, "實體輸入起始點位", ParamDisplayTier.Secondary, false, "INSTART"),
                Opt("inPorts", ParamValueType.Integer, "8", null, "實體輸入點數", ParamDisplayTier.Secondary, false, "INPORTS")
            },
            ImplicitEffects = { "reg driver $(card)" }
        }, "IO@AddDriver", "IO@AddDrvier");

        Reg(new CommandSchema
        {
            CommandKey = "IO@InitIOPort",
            Summary = "初始化並啟動已註冊之 IO 硬體驅動",
            Parameters = { Req("card", ParamValueType.DriverName, 0, "已註冊之 IO 卡代號", "device", "io") },
            ImplicitEffects = { "open $(card)" }
        }, "IO@Initio", "IO@InitIO");

        Reg(new CommandSchema
        {
            CommandKey = "IO@ForceUpdate",
            Summary = "強制立即向指定 IO 硬體驅動雙向刷新電位",
            Parameters = { Req("card", ParamValueType.DriverName, 0, "已註冊之 IO 卡代號", "device", "io") },
            ImplicitEffects = { "sync HW $(card)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "IO@SetPortName",
            Summary = "將實體 IO 點位綁定至語意別名 (PORTNAME)",
            Parameters =
            {
                Req("io", ParamValueType.PortName, 0, "實體點位位址 (如 P0.0)", "port", "physicalPort"),
                Req("alias", ParamValueType.PortName, 1, "語意別名 (如 OUT1, Start?)", "portName")
            },
            ImplicitEffects = { "alias $(io)<->$(alias)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "IO@AddVirtualIO",
            Summary = "動態建立內部邏輯虛擬點位 (Virtual IO)，支援以 $logic 運算式決定狀態",
            Parameters =
            {
                Req("io", ParamValueType.PortName, 0, "虛擬 IO 點位名稱", "name", "port"),
                Opt("logic", ParamValueType.LogicExpr, "False", 1, "初始狀態或 $logic 邏輯運算式 (如 Start? & !Alarm?)", ParamDisplayTier.Secondary, false, "init", "initialValue", "value")
            },
            ImplicitEffects = { "create $(io) = eval($(logic))" }
        }, "IO@VirtualIO");

        Reg(new CommandSchema
        {
            CommandKey = "IO@SetPort",
            Summary = "設定指定 IO 點位為 ON (1)，可選保持秒數與強制刷新",
            Parameters =
            {
                Req("io", ParamValueType.PortName, 0, "目標 IO 點位名稱或別名", "port"),
                Opt("holdSec", ParamValueType.Number, "0", 1, "置位後保持等待秒數", ParamDisplayTier.Secondary, false, "second", "sec"),
                Opt("forceUpdate", ParamValueType.Boolean, "false", 2, "強制立即插隊刷新至硬體", ParamDisplayTier.Critical, false, "ForceUpdate")
            },
            ImplicitEffects = { "$(io)=ON" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "IO@ClrPort",
            Summary = "設定指定 IO 點位為 OFF (0)，可選保持秒數與強制刷新",
            Parameters =
            {
                Req("io", ParamValueType.PortName, 0, "目標 IO 點位名稱或別名", "port"),
                Opt("holdSec", ParamValueType.Number, "0", 1, "復位後保持等待秒數", ParamDisplayTier.Secondary, false, "second", "sec"),
                Opt("forceUpdate", ParamValueType.Boolean, "false", 2, "強制立即插隊刷新至硬體", ParamDisplayTier.Critical, false, "ForceUpdate")
            },
            ImplicitEffects = { "$(io)=OFF" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "IO@Flash(OnOff)",
            Summary = "輸出 ON→OFF 單次脈衝閃爍",
            Parameters =
            {
                Req("io", ParamValueType.PortName, 0, "目標 IO 點位名稱", "port"),
                Req("onSec", ParamValueType.Number, 1, "ON 持續秒數"),
                Req("offSec", ParamValueType.Number, 2, "OFF 持續秒數")
            },
            ImplicitEffects = { "pulse $(io) 1->0" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "IO@Flash(OffOn)",
            Summary = "輸出 OFF→ON 單次脈衝閃爍",
            Parameters =
            {
                Req("io", ParamValueType.PortName, 0, "目標 IO 點位名稱", "port"),
                Req("offSec", ParamValueType.Number, 1, "OFF 持續秒數"),
                Req("onSec", ParamValueType.Number, 2, "ON 持續秒數")
            },
            ImplicitEffects = { "pulse $(io) 0->1" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "IO@Shift",
            Summary = "將一組 IO 點位狀態整批移位（正負方向，邊界補 0）",
            Parameters =
            {
                Req("ios", ParamValueType.PortList, 0, "待移位之 IO 點位陣列 [P1;P2;P3]", "ports"),
                Opt("shift", ParamValueType.Integer, "1", null, "移位格數（正右負左）", ParamDisplayTier.Secondary, false, "Shift")
            },
            ImplicitEffects = { "shift $(ios)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "IO@WaitPortOn",
            Summary = "等待指定 IO 點位變為 ON (1)，支援逾時報警或跳轉",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("io", ParamValueType.PortName, 0, "監控 IO 點位名稱", "port"),
                Opt("timeout", ParamValueType.Number, "0", 1, "逾時秒數（0 表示無限等待）", ParamDisplayTier.Critical, false, "TimeOut")
            },
            ImplicitEffects = { "wait $(io)==ON" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "IO@WaitPortOff",
            Summary = "等待指定 IO 點位變為 OFF (0)，支援逾時報警或跳轉",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("io", ParamValueType.PortName, 0, "監控 IO 點位名稱", "port"),
                Opt("timeout", ParamValueType.Number, "0", 1, "逾時秒數（0 表示無限等待）", ParamDisplayTier.Critical, false, "TimeOut")
            },
            ImplicitEffects = { "wait $(io)==OFF" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "IO@CheckLogicOn",
            Summary = "等待 $logic 布林邏輯運算式成立，支援逾時報警或跳轉",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("logic", ParamValueType.LogicExpr, 0, "$logic 布林邏輯運算式 (支援 & | !)", "expr", "condition"),
                Opt("timeout", ParamValueType.Number, "0", 1, "逾時秒數（0 表示無限等待）", ParamDisplayTier.Critical, false, "TimeOut")
            },
            ImplicitEffects = { "wait $(logic)==true" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "IO@CheckLogicOff",
            Summary = "等待 $logic 布林邏輯運算式不成立，支援逾時報警或跳轉",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("logic", ParamValueType.LogicExpr, 0, "$logic 布林邏輯運算式 (支援 & | !)", "expr", "condition"),
                Opt("timeout", ParamValueType.Number, "0", 1, "逾時秒數（0 表示無限等待）", ParamDisplayTier.Critical, false, "TimeOut")
            },
            ImplicitEffects = { "wait $(logic)==false" }
        });

        // =========================================================================
        // 2. SYSTEM 模組（21 條指令）— timer / counter / rec / var / panel
        // =========================================================================

        Reg(new CommandSchema
        {
            CommandKey = "System@Delay",
            Summary = "非阻塞等待指定秒數（受系統暫停時鐘保護）",
            Parameters = { Req("sec", ParamValueType.Number, 0, "延遲秒數", "seconds", "time") },
            ImplicitEffects = { "wait $(sec)s" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@Log Message",
            Summary = "輸出一般資訊日誌至系統訊息欄",
            Parameters = { Req("msg", ParamValueType.MultiLineText, 0, "日誌訊息文字", "message") },
            ImplicitEffects = { "append log" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@Log Error",
            Summary = "輸出錯誤日誌並觸發當前執行緒異常失敗狀態",
            Parameters = { Req("msg", ParamValueType.MultiLineText, 0, "錯誤訊息文字", "message") },
            ImplicitEffects = { "fail thread" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@Clear Message",
            Summary = "清空系統訊息日誌顯示區",
            ImplicitEffects = { "clear UI logs" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@Clear Error",
            Summary = "清除全機 EMG Stop 鎖定與執行緒錯誤旗標",
            ImplicitEffects = { "clear EMG & errors" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@Note",
            Summary = "純文字註解分段標記（執行期無動作）",
            Parameters = { Req("text", ParamValueType.MultiLineText, 0, "註解說明文字", "comment") }
        }, "System@#Comment----->>");

        Reg(new CommandSchema
        {
            CommandKey = "System@MessageBox",
            Summary = "彈出操作員訊息視窗並等待按下確定",
            Parameters = { Req("msg", ParamValueType.MultiLineText, 0, "提示視窗內容", "message") },
            ImplicitEffects = { "block until OK" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@WarningBox",
            Summary = "條件成立時彈出警告確認框（選擇繼續或停止執行緒）",
            Parameters =
            {
                Req("msg", ParamValueType.MultiLineText, 1, "警告視窗訊息", "message"),
                Opt("logic", ParamValueType.LogicExpr, "True", 0, "觸發條件 $logic 邏輯運算式", ParamDisplayTier.Secondary, false, "condition")
            },
            ImplicitEffects = { "prompt Continue/Stop" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@Pause",
            Summary = "暫停全機 (Global) 或指定執行緒 (Local)",
            Parameters =
            {
                Opt("threads", ParamValueType.ThreadTarget, "ALL", 0, "目標執行緒清單或 ALL", ParamDisplayTier.Critical, false, "THREAD", "thread"),
                Opt("logic", ParamValueType.LogicExpr, "True", null, "觸發暫停之 $logic 條件", ParamDisplayTier.Secondary, false, "CONDITION", "condition")
            },
            ImplicitEffects = { "pause $(threads)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@Resume",
            Summary = "恢復全機 (Global) 或指定執行緒 (Local) 執行",
            Parameters =
            {
                Opt("threads", ParamValueType.ThreadTarget, "ALL", 0, "目標執行緒清單或 ALL", ParamDisplayTier.Critical, false, "THREAD", "thread"),
                Opt("logic", ParamValueType.LogicExpr, "True", null, "觸發恢復之 $logic 條件", ParamDisplayTier.Secondary, false, "CONDITION", "condition")
            },
            ImplicitEffects = { "resume $(threads)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@TimerReset",
            Summary = "重設計時器並設定逾時門檻秒數",
            Parameters =
            {
                Req("timer", ParamValueType.String, 0, "計時器名稱（尾綴 & 可豁免系統暫停）", "name", "timerName"),
                Opt("target", ParamValueType.Number, "0", 1, "逾時門檻秒數", ParamDisplayTier.Secondary, false, "timeout", "timeoutSec")
            },
            ImplicitEffects = { "reset $(timer)#Timeout?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@TimerCheck",
            Summary = "檢查計時器是否逾時並同步更新 $(timer)#Timeout?",
            Parameters = { Req("timer", ParamValueType.String, 0, "計時器名稱", "name", "timerName") },
            ImplicitEffects = { "update $(timer)#Timeout?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@CounterReset",
            Summary = "重設計數器並設定目標次數上限",
            Parameters =
            {
                Req("counter", ParamValueType.String, 0, "計數器名稱", "name", "cntName"),
                Opt("target", ParamValueType.Integer, "0", 1, "目標次數上限", ParamDisplayTier.Secondary, false, "count")
            },
            ImplicitEffects = { "reset $(counter)#Over?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@CounterCheck",
            Summary = "計數器遞減 1 並同步更新 $(counter)#Over?",
            Parameters = { Req("counter", ParamValueType.String, 0, "計數器名稱", "name", "cntName") },
            ImplicitEffects = { "dec & update $(counter)#Over?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@Record",
            Summary = "寫入命名時間戳紀錄點（供 CycleTime 計算）",
            Parameters =
            {
                Req("rec", ParamValueType.String, 0, "紀錄器名稱", "name", "recName"),
                Opt("msg", ParamValueType.String, "", 1, "附加紀錄標籤", ParamDisplayTier.Secondary, false, "message")
            },
            ImplicitEffects = { "timestamp $(rec)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@LogRecordCycleTime",
            Summary = "計算並輸出指定紀錄點之經過週期時間",
            Parameters =
            {
                Req("rec", ParamValueType.String, 0, "紀錄器名稱", "name", "recName"),
                Opt("prefix", ParamValueType.String, "", 1, "日誌前綴文字"),
                Out("var", ParamValueType.VariableName, "可選：將計算秒數存入指定變數", "sec")
            },
            ImplicitEffects = { "log CT $(rec)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@ProgramLoad",
            Summary = "動態載入 .prog/.pro 專案檔或 .init 單段腳本",
            Parameters = { Req("file", ParamValueType.FilePath, 0, "程式檔案路徑或檔名", "file") },
            ImplicitEffects = { "reload prog slot(s)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@ProgramSection",
            Summary = "切換當前執行緒變數分區 (Section) 並可選命名程式段",
            Parameters =
            {
                Req("section", ParamValueType.String, 0, "變數分區或程式槽編號"),
                Opt("title", ParamValueType.String, "", 1, "程式段顯示名稱", ParamDisplayTier.Secondary, false, "name")
            },
            ImplicitEffects = { "switch ctx.Section=$(section)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@IncludeModule",
            Summary = "動態載入或重新熱重載指定名稱之 CSVI 模組",
            Parameters = { Req("module", ParamValueType.String, 0, "模組名稱 (如 IO, MOTION)", "moduleName") },
            ImplicitEffects = { "reload module $(module)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@UserPanelOpen",
            Summary = "開啟並掛載指定 .csvi 人機子面板 (UserPanel)",
            Parameters =
            {
                Req("panel", ParamValueType.FilePath, 0, "UserPanel .csvi 檔案路徑", "path"),
                Opt("alias", ParamValueType.String, "", null, "面板分頁自訂名稱", ParamDisplayTier.Secondary, false, "name"),
                Opt("activate", ParamValueType.Boolean, "true", null, "開啟後立即切換至該分頁")
            },
            ImplicitEffects = { "mount UserPanel $(panel)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "System@UserPanelClose",
            Summary = "關閉並卸載指定名稱之 UserPanel 分頁",
            Parameters = { Req("panel", ParamValueType.String, 0, "欲關閉之面板名稱", "name", "panelName") },
            ImplicitEffects = { "close UserPanel $(panel)" }
        });

        // =========================================================================
        // 3. THREAD 模組（31 條指令）— label / vars / prog / thread / sub / lock
        // =========================================================================

        // --- A. 結構化條件與迴圈 ---
        Reg(new CommandSchema
        {
            CommandKey = "Thread@If",
            Summary = "結構化條件分支起點（邏輯成立執行內部區塊）",
            Parameters = { Req("logic", ParamValueType.LogicExpr, 0, "$logic 布林條件運算式", "condition") },
            ImplicitEffects = { "branch block" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@ElseIf",
            Summary = "結構化條件替代分支",
            Parameters = { Req("logic", ParamValueType.LogicExpr, 0, "$logic 布林條件運算式", "condition") },
            ImplicitEffects = { "branch block" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@Else",
            Summary = "結構化條件預設分支",
            ImplicitEffects = { "branch block" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@EndIf",
            Summary = "結束對應之 If/ElseIf/Else 結構化區塊",
            ImplicitEffects = { "close If block" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@ForeachStart",
            Summary = "標記陣列迴圈起點標籤",
            Parameters = { Req("label", ParamValueType.LabelName, 0, "迴圈配對標籤名稱") },
            ImplicitEffects = { "mark loop head $(label)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@ForeachLoop",
            Summary = "推進陣列變數游標，未到底時跳回 ForeachStart 繼續疊代",
            Parameters =
            {
                Req("label", ParamValueType.LabelName, 0, "對應 ForeachStart 標籤名稱"),
                Req("vars", ParamValueType.String, 1, "連動推進之陣列變數清單 [Arr1;Arr2]", "arrays")
            },
            ImplicitEffects = { "LMoveNext $(vars) & loop $(label)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@ForeachBreak",
            Summary = "條件成立時立即跳出目前 Foreach 迴圈",
            Parameters = { Opt("logic", ParamValueType.LogicExpr, "True", 0, "跳出條件", ParamDisplayTier.Secondary, false, "condition") },
            ImplicitEffects = { "exit loop" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@ForeachContinue",
            Summary = "條件成立時略過剩餘步驟直接進入下一次疊代",
            Parameters = { Opt("logic", ParamValueType.LogicExpr, "True", 0, "略過條件", ParamDisplayTier.Secondary, false, "condition") },
            ImplicitEffects = { "next iteration" }
        });

        // --- B. 子程序與多執行緒調度 ---
        Reg(new CommandSchema
        {
            CommandKey = "Thread@Call",
            Summary = "壓入呼叫堆疊並執行指定子程式區段，結束後由 Return 返回",
            Parameters =
            {
                Req("prog", ParamValueType.ProgramTarget, 0, "目標程式槽編號或名稱", "program"),
                Opt("logic", ParamValueType.LogicExpr, "True", 1, "條件", ParamDisplayTier.Secondary, false, "condition")
            },
            ImplicitEffects = { "call $(prog)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@Return",
            Summary = "條件成立時從 Call 子程序返回原呼叫點下一行（省略 logic 表示無條件返回）",
            Parameters =
            {
                Opt("logic", ParamValueType.LogicExpr, "True", 0, "返回條件（留空無條件返回）", ParamDisplayTier.Secondary, false, "condition")
            },
            ImplicitEffects = { "pop call stack" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@Run",
            Summary = "在指定背景 Worker Thread (0~12) 上啟動執行目標程式段",
            Parameters =
            {
                Req("prog", ParamValueType.ProgramTarget, 0, "目標程式槽編號或名稱", "program"),
                Req("thread", ParamValueType.Integer, 1, "目標執行緒編號 (0~12)", "threadNo"),
                Opt("startLine", ParamValueType.String, "1", 2, "起始執行行號或 Label 名稱", ParamDisplayTier.Secondary, false, "start", "label"),
                Opt("logic", ParamValueType.LogicExpr, "True", 3, "啟動條件", ParamDisplayTier.Secondary, false, "condition")
            },
            ImplicitEffects = { "start Thread$(thread)#Run?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@Wait",
            Summary = "等待一個或多個指定執行緒結束運行",
            Parameters = { Req("threads", ParamValueType.ThreadTarget, 0, "待等待執行緒清單 [Thread1;Thread2]") },
            ImplicitEffects = { "wait !$(threads)#Run?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@Suspend",
            Summary = "停止指定編號之主/工作執行緒",
            Parameters =
            {
                Req("thread", ParamValueType.Integer, 0, "目標執行緒編號 (0~12)", "threadNo"),
                Opt("logic", ParamValueType.LogicExpr, "True", 1, "停止條件", ParamDisplayTier.Secondary, false, "condition")
            },
            ImplicitEffects = { "stop Thread$(thread)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@Resume",
            Summary = "恢復指定編號之主/工作執行緒",
            Parameters =
            {
                Req("thread", ParamValueType.Integer, 0, "目標執行緒編號 (0~12)", "threadNo"),
                Opt("logic", ParamValueType.LogicExpr, "True", 1, "恢復條件", ParamDisplayTier.Secondary, false, "condition")
            },
            ImplicitEffects = { "resume Thread$(thread)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@Stop",
            Summary = "條件成立時停止當前執行緒（可選記錄終止原因）",
            Parameters =
            {
                Opt("logic", ParamValueType.LogicExpr, "True", 0, "停止條件", ParamDisplayTier.Secondary, false, "condition"),
                Opt("msg", ParamValueType.String, "", 1, "終止說明訊息", ParamDisplayTier.Secondary, false, "message")
            },
            ImplicitEffects = { "halt current thread" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@SubThreadStart",
            Summary = "將至 SubThreadEnd 之間的區塊派生為獨立平行子執行緒執行",
            Parameters =
            {
                Req("sub", ParamValueType.String, 0, "子執行緒名稱（尾綴 & 可豁免暫停）", "name", "subName"),
                Opt("logic", ParamValueType.LogicExpr, "True", 1, "啟動條件", ParamDisplayTier.Secondary, false, "condition")
            },
            ImplicitEffects = { "spawn $(sub)#Run?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@SubThreadEnd",
            Summary = "標記對應 SubThreadStart 內嵌區塊的結尾",
            Parameters = { Req("sub", ParamValueType.String, 0, "對應 SubThread 名稱", "name", "subName") },
            ImplicitEffects = { "end inline SubThread $(sub)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@SubThreadScript",
            Summary = "載入外部 .init 腳本並以獨立 SubThread 平行執行",
            Parameters =
            {
                Req("sub", ParamValueType.String, 0, "子執行緒名稱", "name", "subName"),
                Req("file", ParamValueType.FilePath, 1, "外部 .init 腳本檔案", "scriptFile")
            },
            ImplicitEffects = { "spawn $(sub) from $(file)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@SubThreadSuspend",
            Summary = "強制停止指定名稱之執行中 SubThread",
            Parameters = { Req("sub", ParamValueType.String, 0, "目標 SubThread 名稱", "name", "subName") },
            ImplicitEffects = { "stop SubThread $(sub)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@SubThreadWait",
            Summary = "等待一個或多個指定 SubThread 執行完成",
            Parameters =
            {
                Req("subs", ParamValueType.ThreadTarget, 0, "目標 SubThread 名稱清單 [Sub1;Sub2]", "names"),
                Opt("skipNotFound", ParamValueType.Boolean, "false", null, "找不到目標時直接略過不報錯", ParamDisplayTier.Secondary, false, "SKIPNOTFIND")
            },
            ImplicitEffects = { "wait SubThread $(subs)" }
        });

        // --- C. 互斥鎖、單步控制與異常處理 ---
        Reg(new CommandSchema
        {
            CommandKey = "Thread@KeySectionLock",
            Summary = "取得跨執行緒共用互斥鎖（防止多機構干涉撞機）",
            Parameters =
            {
                Req("lock", ParamValueType.String, 0, "互斥鎖名稱", "name", "lockName"),
                Opt("timeout", ParamValueType.Number, "5.0", 1, "取得鎖逾時秒數", ParamDisplayTier.Critical, false, "timeout"),
                Opt("msg", ParamValueType.String, "", 2, "逾時錯誤訊息", ParamDisplayTier.Secondary, false, "message")
            },
            ImplicitEffects = { "acquire mutex $(lock)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@KeySectionUnLock",
            Summary = "釋放當前執行緒持有之跨執行緒互斥鎖",
            Parameters = { Req("lock", ParamValueType.String, 0, "互斥鎖名稱", "name", "lockName") },
            ImplicitEffects = { "release mutex $(lock)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@GoOn",
            Summary = "Thread@Stop 的反相指令：條件成立時繼續執行，條件不成立時立即停止當前執行緒（可選記錄終止原因）",
            Parameters =
            {
                Opt("logic", ParamValueType.LogicExpr, "True", 0, "允許繼續條件（False 時停止當前執行緒）", ParamDisplayTier.Secondary, false, "condition"),
                Opt("msg", ParamValueType.String, "", 1, "條件不成立時之終止說明訊息", ParamDisplayTier.Secondary, false, "message")
            },
            ImplicitEffects = { "halt current thread if !$(logic)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@Repeat",
            Summary = "條件成立時跳回本程式段第 1 行重頭循環執行",
            Parameters = { Opt("logic", ParamValueType.LogicExpr, "True", 0, "循環條件", ParamDisplayTier.Secondary, false, "condition") },
            ImplicitEffects = { "jump line 1" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@MutePopupMessage",
            Summary = "開啟或關閉當前執行緒之彈窗靜音模式",
            Parameters = { Req("enabled", ParamValueType.Boolean, 0, "是否靜音 (true/false)", "mute") },
            ImplicitEffects = { "ctx.IsMuted=$(enabled)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@ErrorHandler",
            Summary = "宣告異常處理區段標籤（搭配 [[Label]] 錯誤跳轉）",
            Parameters = { Req("label", ParamValueType.LabelName, 0, "異常處理標籤名稱") },
            ImplicitEffects = { "mark error handler $(label)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@ErrorRetry",
            Summary = "於異常處理完畢後條件跳回原出錯指令重試",
            Parameters =
            {
                Opt("logic", ParamValueType.LogicExpr, "True", 0, "允許重試條件", ParamDisplayTier.Secondary, false, "condition"),
                Opt("offset", ParamValueType.Integer, "0", 1, "相對於原出錯行之行號偏移量", ParamDisplayTier.Secondary, false, "lineOffset")
            },
            ImplicitEffects = { "retry failed cmd (+$(offset))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@Label",
            Summary = "宣告跳轉目標錨點標籤",
            Parameters = { Req("label", ParamValueType.LabelName, 0, "標籤名稱", "name") }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@Jump",
            Summary = "跳轉至指定 Label（AI 審查警戒指令，優先使用 If/Foreach/Call）",
            AiAuditWarning = true,
            Parameters =
            {
                Req("label", ParamValueType.LabelName, 0, "目標標籤名稱"),
                Opt("logic", ParamValueType.LogicExpr, "True", 1, "跳轉條件", ParamDisplayTier.Secondary, false, "condition")
            },
            ImplicitEffects = { "PC = $(label)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@RunNextCMD",
            Summary = "僅在條件成立時執行下一行指令（AI 審查警戒指令）",
            AiAuditWarning = true,
            Parameters = { Opt("logic", ParamValueType.LogicExpr, "True", 0, "執行下一行之條件", ParamDisplayTier.Secondary, false, "condition") },
            ImplicitEffects = { "skip next if !$(logic)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Thread@SkipNextCMD",
            Summary = "條件成立時略過下一行指令（AI 審查警戒指令）",
            AiAuditWarning = true,
            Parameters = { Opt("logic", ParamValueType.LogicExpr, "True", 0, "略過下一行之條件", ParamDisplayTier.Secondary, false, "condition") },
            ImplicitEffects = { "skip next if $(logic)" }
        });

        // =========================================================================
        // 4. VARIABLE 模組（34 條指令）— var / vars / pt + 顯式輸出 -> var / -> out
        // =========================================================================

        // --- A. 基本讀寫、數值與表達式 ---
        Reg(new CommandSchema
        {
            CommandKey = "Variable@Set",
            Summary = "設定變數值並將陣列游標歸 0",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標變數名稱", "key", "name"),
                Req("val", ParamValueType.String, 1, "設定值（陣列為 [A;B;C]）", "value"),
                Opt("section", ParamValueType.String, "", 2, "目標分區（留空為當前分區）")
            },
            ImplicitEffects = { "$(var)=$(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Delete",
            Summary = "刪除指定變數",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "待刪除變數名稱", "key", "name"),
                Opt("section", ParamValueType.String, "", 1, "目標分區")
            },
            ImplicitEffects = { "delete $(var)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Rename",
            Summary = "重新命名指定變數",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "原變數名稱", "oldKey", "from"),
                Req("to", ParamValueType.VariableName, 1, "新變數名稱", "newKey"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "rename $(var)->$(to)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Inc",
            Summary = "變數數值遞增（預設 +1）",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標變數名稱", "key"),
                Opt("step", ParamValueType.Number, "1", 1, "遞增步長"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "$(var)+=$(step)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Dec",
            Summary = "變數數值遞減（預設 -1）",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標變數名稱", "key"),
                Opt("step", ParamValueType.Number, "1", 1, "遞減步長"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "$(var)-=$(step)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Calculate",
            Summary = "計算數學或字串運算式並寫入目標變數",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, null, "儲存計算結果之目標變數", "dest"),
                Req("expr", ParamValueType.String, null, "數學或表達式公式", "formula"),
                Opt("section", ParamValueType.String, "", null, "目標分區")
            },
            ImplicitEffects = { "$(var)=eval($(expr))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Select",
            Summary = "依 $logic 條件二選一並寫入目標變數",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, null, "儲存選擇結果之目標變數", "dest"),
                Req("logic", ParamValueType.LogicExpr, null, "$logic 判斷邏輯運算式", "condition"),
                Req("true", ParamValueType.String, null, "條件成立時採用之值", "onTrue", "then", "v1"),
                Req("false", ParamValueType.String, null, "條件不成立時採用之值", "onFalse", "else", "v2"),
                Opt("section", ParamValueType.String, "", null, "目標分區")
            },
            ImplicitEffects = { "$(var)=$(logic)?$(true):$(false)" }
        });

        // --- B. 字串處理 ---
        Reg(new CommandSchema
        {
            CommandKey = "Variable@Append",
            Summary = "將文字拼接至目標變數尾端",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標變數名稱", "key"),
                Req("val", ParamValueType.String, 1, "附加尾端字串", "suffix"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "$(var)+=$(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Trim",
            Summary = "依起始與結束索引擷取子字串",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標變數名稱", "key"),
                Req("start", ParamValueType.Integer, 1, "起始索引"),
                Req("end", ParamValueType.Integer, 2, "結束索引或長度", "length"),
                Opt("section", ParamValueType.String, "", 3, "目標分區")
            },
            ImplicitEffects = { "trim $(var)[$(start)..$(end)]" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Slice",
            Summary = "依分隔字元將字串切割為 [A;B;C] 陣列",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標變數名稱", "key"),
                Req("sep", ParamValueType.String, 1, "切割分隔符", "separator"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "slice $(var) by $(sep)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Joint",
            Summary = "依分隔字元將陣列合併為單一字串",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數名稱", "key"),
                Req("sep", ParamValueType.String, 1, "合併連接符", "separator"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "join $(var) with $(sep)" }
        });

        // --- C. 陣列 / 清單操作 ---
        Reg(new CommandSchema
        {
            CommandKey = "Variable@LPush",
            Summary = "從陣列左端（頭部）推入元素",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數", "key"),
                Req("val", ParamValueType.String, 1, "推入值", "value"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "lpush $(var) <= $(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@RPush",
            Summary = "從陣列右端（尾部）推入元素",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數", "key"),
                Req("val", ParamValueType.String, 1, "推入值", "value"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "rpush $(var) <= $(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@LPop",
            Summary = "從陣列左端彈出並移除元素（可選存入輸出變數）",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數", "key"),
                Opt("section", ParamValueType.String, "", 1, "目標分區"),
                Out("out", ParamValueType.VariableName, "可選：接收彈出值之目標變數", "dest")
            },
            ImplicitEffects = { "lpop $(var)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@RPop",
            Summary = "從陣列右端彈出並移除元素（可選存入輸出變數）",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數", "key"),
                Opt("section", ParamValueType.String, "", 1, "目標分區"),
                Out("out", ParamValueType.VariableName, "可選：接收彈出值之目標變數", "dest")
            },
            ImplicitEffects = { "rpop $(var)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@LInsert",
            Summary = "依條件將元素插入陣列指定位置",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數", "key"),
                Req("val", ParamValueType.String, 1, "待插入元素值", "value"),
                Req("logic", ParamValueType.LogicExpr, 2, "插入條件/排序基準（如 >0、Before[B]、After[B]）", "condition"),
                Opt("section", ParamValueType.String, "", 3, "目標分區")
            },
            ImplicitEffects = { "linsert $(var)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@LRemove",
            Summary = "從陣列移除指定目標值",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數", "key"),
                Req("val", ParamValueType.String, 1, "欲移除之目標值", "target"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "lremove $(val) from $(var)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@LResetIndex",
            Summary = "重設陣列讀取游標位置（預設 0）",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數", "key"),
                Opt("index", ParamValueType.Integer, "0", 1, "目標游標索引"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "$(var).index=$(index)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@LSetIndexValue",
            Summary = "覆寫陣列指定索引位置之元素值",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數", "key"),
                Req("index", ParamValueType.Integer, 1, "目標索引位置"),
                Req("val", ParamValueType.String, 2, "新元素值", "value"),
                Opt("section", ParamValueType.String, "", 3, "目標分區")
            },
            ImplicitEffects = { "$(var)[$(index)]=$(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@LReverse",
            Summary = "反轉陣列元素順序",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數", "key"),
                Opt("section", ParamValueType.String, "", 1, "目標分區")
            },
            ImplicitEffects = { "reverse $(var)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@LGetIndexSize",
            Summary = "查詢陣列當前游標與總長度並寫入 #Index / #Size",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標陣列變數", "key"),
                Opt("section", ParamValueType.String, "", 1, "目標分區")
            },
            ImplicitEffects = { "sets $(var)#Index & $(var)#Size" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@LFindIndexSet",
            Summary = "以主鍵陣列搜尋目標值命中索引，並同步連動切換其他陣列游標",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "主鍵陣列變數", "key"),
                Req("val", ParamValueType.String, 1, "搜尋目標值", "target"),
                Opt("linked", ParamValueType.String, "", 2, "連動切換游標之陣列清單 [Arr1;Arr2]", ParamDisplayTier.Secondary, false, "vars")
            },
            ImplicitEffects = { "sync cursor of $(var) & $(linked)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Shift",
            Summary = "將一組變數的值依指定格數整批移位",
            Parameters =
            {
                Req("vars", ParamValueType.String, 0, "待移位之變數清單 [V1;V2;V3]"),
                Opt("shift", ParamValueType.Integer, "1", null, "移位格數（正右負左）", ParamDisplayTier.Secondary, false, "Shift")
            },
            ImplicitEffects = { "shift $(vars)" }
        });

        // --- D. 集合運算 ---
        Reg(new CommandSchema
        {
            CommandKey = "Variable@SAdd",
            Summary = "將不重複元素加入集合陣列",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "目標集合變數", "key"),
                Req("val", ParamValueType.String, 1, "加入元素值", "value"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "sadd $(var) += $(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@SUnion",
            Summary = "計算兩集合陣列之聯集並存入目標變數",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "輸出聯集目標變數", "dest"),
                Req("varA", ParamValueType.VariableName, 1, "集合變數 A", "v1"),
                Req("varB", ParamValueType.VariableName, 2, "集合變數 B", "v2"),
                Opt("section", ParamValueType.String, "", 3, "目標分區")
            },
            ImplicitEffects = { "$(var)=$(varA) ∪ $(varB)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@SDiff",
            Summary = "計算兩集合陣列之差集 (A - B) 並存入目標變數",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "輸出差集目標變數", "dest"),
                Req("varA", ParamValueType.VariableName, 1, "集合變數 A", "v1"),
                Req("varB", ParamValueType.VariableName, 2, "集合變數 B", "v2"),
                Opt("section", ParamValueType.String, "", 3, "目標分區")
            },
            ImplicitEffects = { "$(var)=$(varA) \\ $(varB)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@SIntersection",
            Summary = "計算兩集合陣列之交集並存入目標變數",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "輸出交集目標變數", "dest"),
                Req("varA", ParamValueType.VariableName, 1, "集合變數 A", "v1"),
                Req("varB", ParamValueType.VariableName, 2, "集合變數 B", "v2"),
                Opt("section", ParamValueType.String, "", 3, "目標分區")
            },
            ImplicitEffects = { "$(var)=$(varA) ∩ $(varB)" }
        });

        // --- E. 點位資料擷取 ---
        Reg(new CommandSchema
        {
            CommandKey = "Variable@GetPointElement",
            Summary = "從指定點位擷取單一軸向 (X/Y/Z/U/V/W) 或 H 欄位值寫入變數",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, null, "儲存擷取值之目標變數", "dest"),
                Req("pt", ParamValueType.String, null, "來源點位名稱或索引 (如 P1, Tray[0])", "point"),
                Req("axis", ParamValueType.String, null, "軸向或 H 鍵名 (如 X, Z, H.speed)", "element"),
                Opt("section", ParamValueType.String, "", null, "目標分區")
            },
            ImplicitEffects = { "$(var)=$(axis)($(pt))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@GetPointString",
            Summary = "將指定點位格式化為 (X,Y,Z,U,V,W) 字串並寫入變數",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "儲存字串之目標變數", "dest"),
                Req("pt", ParamValueType.String, 1, "來源點位名稱", "point"),
                Opt("section", ParamValueType.String, "", 2, "目標分區")
            },
            ImplicitEffects = { "$(var)=str($(pt))" }
        });

        // --- F. 同步事件與檔案持久化 ---
        Reg(new CommandSchema
        {
            CommandKey = "Variable@Publish",
            Summary = "發佈變數事件訊號通知等待中之 Subscribe 執行緒",
            Parameters = { Req("vars", ParamValueType.String, 0, "發佈變數清單 [V1;V2]", "var") },
            ImplicitEffects = { "publish $(vars)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Subscribe",
            Summary = "非阻塞等待指定變數之 Publish 事件觸發",
            Parameters = { Req("var", ParamValueType.VariableName, 0, "訂閱變數名稱", "key") },
            ImplicitEffects = { "wait publish $(var)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@Flush",
            Summary = "清除指定變數之已觸發事件訊號",
            Parameters = { Req("var", ParamValueType.VariableName, 0, "變數名稱", "key") },
            ImplicitEffects = { "flush $(var)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@VariableLoad",
            Summary = "從檔案載入變數資料至 VariableEngine",
            Parameters = { Req("file", ParamValueType.FilePath, 0, "變數檔案路徑", "file") },
            ImplicitEffects = { "load vars from $(file)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Variable@VariableSave",
            Summary = "將符合篩選樣式之變數持久化儲存至檔案",
            Parameters =
            {
                Req("file", ParamValueType.FilePath, 0, "目標儲存路徑", "file"),
                Opt("patterns", ParamValueType.String, "", 1, "可選變數名稱/正則篩選清單 [P1;P2]")
            },
            ImplicitEffects = { "save vars to $(file)" }
        });

        // =========================================================================
        // 5. POINT&PATH 模組（22 條指令）— pt / ws / imaq + 顯式輸出 -> pt / -> var
        // =========================================================================

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@AddXYZ",
            Summary = "對點位 XYZ 座標進行增量平移累加",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "目標點位名稱", "point"),
                Req("delta", ParamValueType.String, 1, "平移量 (dx,dy,dz) 或來源點位", "offset")
            },
            ImplicitEffects = { "$(pt).XYZ += $(delta)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@AddUVW",
            Summary = "對點位 UVW 姿態軸進行增量累加",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "目標點位名稱", "point"),
                Req("delta", ParamValueType.String, 1, "增量 (du,dv,dw) 或來源點位", "offset")
            },
            ImplicitEffects = { "$(pt).UVW += $(delta)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@SubXYZ",
            Summary = "對點位 XYZ 座標進行減量平移",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "目標點位名稱", "point"),
                Req("delta", ParamValueType.String, 1, "減量 (dx,dy,dz) 或來源點位", "offset")
            },
            ImplicitEffects = { "$(pt).XYZ -= $(delta)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@SubUVW",
            Summary = "對點位 UVW 姿態軸進行減量運算",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "目標點位名稱", "point"),
                Req("delta", ParamValueType.String, 1, "減量 (du,dv,dw) 或來源點位", "offset")
            },
            ImplicitEffects = { "$(pt).UVW -= $(delta)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@ReplaceXYZ",
            Summary = "覆寫點位之 XYZ 座標分量（保留 UVW 與 H）",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "目標點位名稱", "point"),
                Req("val", ParamValueType.String, 1, "新座標 (x,y,z) 或來源點位", "value")
            },
            ImplicitEffects = { "$(pt).XYZ = $(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@ReplaceUVW",
            Summary = "覆寫點位之 UVW 姿態分量（保留 XYZ 與 H）",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "目標點位名稱", "point"),
                Req("val", ParamValueType.String, 1, "新姿態 (u,v,w) 或來源點位", "value")
            },
            ImplicitEffects = { "$(pt).UVW = $(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@ReplaceH",
            Summary = "覆寫點位之 JSON 附加參數字典 (H)",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "目標點位名稱", "point"),
                Req("h", ParamValueType.String, 1, "JSON 物件或 H 參數值", "val")
            },
            ImplicitEffects = { "$(pt).H = $(h)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@Calculate",
            Summary = "計算點位座標/向量運算式並覆寫或壓入目標點位陣列",
            Parameters =
            {
                Req("pt", ParamValueType.String, null, "目標點位名稱", "dest", "point"),
                Req("expr", ParamValueType.String, null, "點位座標 (x,y,z,...) 或運算式 (如 P1+P2)"),
                Opt("append", ParamValueType.Boolean, "false", null, "true 時壓入陣列尾端 (<=)，false 為覆寫 (=)")
            },
            ImplicitEffects = { "$(pt) = calc($(expr))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@InsertOnePoint",
            Summary = "將單一點位壓入指定點位陣列尾端",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "目標點位陣列名稱", "point"),
                Req("val", ParamValueType.String, 1, "新點位座標 (x,y,z) 或來源點位", "expr")
            },
            ImplicitEffects = { "$(pt) <= $(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@ClearPoint",
            Summary = "清空指定點位陣列並重設游標",
            Parameters = { Req("pt", ParamValueType.String, 0, "目標點位名稱", "point") },
            ImplicitEffects = { "clear $(pt)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@EnableAxis",
            Summary = "保留點位指定有效軸向（其餘軸屏蔽設為 inf）",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "目標點位名稱", "point"),
                Req("axis", ParamValueType.String, 1, "啟用軸向字元組合 (如 XYZ, XY, Z)", "axes")
            },
            ImplicitEffects = { "mask $(pt) axis=$(axis)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@Select",
            Summary = "依 $logic 條件於兩點位間二選一寫入目標點位",
            Parameters =
            {
                Req("pt", ParamValueType.String, null, "目標點位名稱", "dest", "point"),
                Req("logic", ParamValueType.LogicExpr, null, "$logic 判斷邏輯運算式", "condition"),
                Req("true", ParamValueType.String, null, "成立時採用之點位", "onTrue", "then", "p1"),
                Req("false", ParamValueType.String, null, "不成立時採用之點位", "onFalse", "else", "p2")
            },
            ImplicitEffects = { "$(pt)=$(logic)?$(true):$(false)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@Pallet1D",
            Summary = "依 [0]起點、[1]終點等距生成 1D 料盤點位陣列",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "料盤點位陣列名稱", "point"),
                Req("count", ParamValueType.Integer, 1, "總點數")
            },
            ImplicitEffects = { "gen 1D pallet $(pt) x$(count)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@Pallet2D",
            Summary = "依 [0]原點、[1]X終點、[2]Y終點生成 2D 矩陣料盤",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "料盤點位陣列名稱", "point"),
                Req("countX", ParamValueType.Integer, 1, "X 方向點數", "nx"),
                Req("countY", ParamValueType.Integer, 2, "Y 方向點數", "ny")
            },
            ImplicitEffects = { "gen 2D pallet $(pt) $(countX)x$(countY)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@ResetPalletIndex",
            Summary = "重設料盤陣列游標索引並更新 $(pt)#Last?",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "料盤點位陣列名稱", "point"),
                Opt("index", ParamValueType.Integer, "0", 1, "重設目標索引")
            },
            ImplicitEffects = { "$(pt).index=$(index)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@Circle2DPath",
            Summary = "依圓心、起終點與角度生成 2D 圓弧分割路徑點陣列",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "圓弧路徑點位名稱", "point"),
                Req("startDeg", ParamValueType.Number, 1, "起始角度 (deg)"),
                Req("endDeg", ParamValueType.Number, 2, "結束角度 (deg)"),
                Req("segments", ParamValueType.Integer, 3, "分割段數")
            },
            ImplicitEffects = { "gen circle $(pt) [$(startDeg)°..$(endDeg)°]" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@CoordWS(2Edge)",
            Summary = "以雙邊端點建立工作區座標系 (WorkSpace)",
            Parameters =
            {
                Req("ws", ParamValueType.String, 0, "工作區點位名稱", "pt"),
                Req("xEnd", ParamValueType.String, 1, "X 軸終點點位或座標"),
                Req("yEnd", ParamValueType.String, 2, "Y 軸終點點位或座標")
            },
            ImplicitEffects = { "build WS $(ws)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@Point2WS",
            Summary = "將機構座標轉換為工作區 (WS) 相對座標",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "欲轉換之點位名稱", "point"),
                Req("ws", ParamValueType.String, 1, "目標工作區座標系名稱")
            },
            ImplicitEffects = { "$(pt) = Point2WS($(ws))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@WS2Point",
            Summary = "將工作區 (WS) 座標逆轉換回機構絕對座標",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "欲轉換之點位名稱", "point"),
                Req("ws", ParamValueType.String, 1, "來源工作區座標系名稱")
            },
            ImplicitEffects = { "$(pt) = WS2Point($(ws))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@SelfMotion",
            Summary = "依基準點與新實測點進行剛體平移與旋轉偏差補償",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "待補償之目標點位", "point"),
                Req("basePt", ParamValueType.String, 1, "原始基準點位"),
                Req("newPt", ParamValueType.String, 2, "視覺/量測新點位")
            },
            ImplicitEffects = { "compensate $(pt) by $(basePt)->$(newPt)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@ChkInRangeXY",
            Summary = "檢查點位是否位於安全範圍內（支援 axis 軸向組合：1點圓/球、2點長方形/長方體、3點異Z長方體/共面三角形、多點平面多邊形/稜柱體），更新 $(pt)#InRange? 與可選輸出 IO",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "受測點位名稱", "point"),
                Req("rangePt", ParamValueType.String, 1, "定義範圍之點位陣列（1點圓/2點矩形/3點立體或三角/多點多邊形）"),
                Opt("axis", ParamValueType.String, "XY", null, "檢查軸向組合（預設 XY；如 XYZ、XZ、YZ、Z、XYZU）", ParamDisplayTier.Secondary, false, "axes"),
                Out("io", ParamValueType.PortName, "可選：將判定結果 (ON/OFF) 寫入指定 IO", "dest")
            },
            ImplicitEffects = { "sets $(pt)#InRange?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Point&Path@TransImaqPoint",
            Summary = "將影像像素座標透過校正矩陣轉換為機構點位",
            Parameters =
            {
                Req("imaqs", ParamValueType.String, 0, "來源影像校正名稱", "imaq"),
                Req("pt", ParamValueType.String, 1, "待轉換之點位名稱", "point")
            },
            ImplicitEffects = { "transform $(pt) via $(imaqs)" }
        });

        // =====================================================================
        // 6. MOTION 模組 (21 條指令) — mot / pt / speed* / timeout*
        // =====================================================================

        Reg(new CommandSchema
        {
            CommandKey = "Motion@AddMotion",
            Summary = "註冊並初始化運動/機械手臂控制器驅動實例",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱（如 Motor1）", "motor", "motion", "name"),
                Req("driver", ParamValueType.DriverName, 1, "驅動程式名稱（如 MockMotion）", "driverType"),
                Opt("device", ParamValueType.String, "", null, "實體裝置/連線位址 (COM 埠、IP、OPC-UA、卡號等)", ParamDisplayTier.Secondary, false, "DEVICE"),
                Opt("axis", ParamValueType.Integer, "", null, "控制軸數（選填）", ParamDisplayTier.Secondary, false, "Axes", "axes"),
                Opt("speed", ParamValueType.Number, "", null, "預設速度 mm/s（選填）", ParamDisplayTier.Secondary, false, "Speed"),
                Opt("acc", ParamValueType.Number, "", null, "預設加速度 mm/s^2（選填）", ParamDisplayTier.Secondary, false, "Acc"),
                Opt("dec", ParamValueType.Number, "", null, "預設減速度 mm/s^2（選填）", ParamDisplayTier.Secondary, false, "Dec"),
                Opt("options", ParamValueType.String, "", 2, "其他驅動專屬擴充鍵值參數", ParamDisplayTier.Secondary, false, "param", "extra")
            },
            ImplicitEffects = { "reg motion driver $(mot)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@Home",
            Summary = "重置錯誤、啟用 Servo ON 並執行回原點（不於 Home 當下跳錯，異常由 Motion@GetState / WaitMotion 檢出）",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                Opt("axis", ParamValueType.String, "", 1, "指定回原點軸向（如 Z、XY；Robot 或全軸可省略）", ParamDisplayTier.Secondary, false, "axes"),
                new ParamSpec
                {
                    Name = "mode",
                    Aliases = new[] { "homeMode" },
                    LegacyIndex = 2,
                    Direction = ParamDirection.Input,
                    Required = false,
                    DefaultValue = "",
                    ValueType = ParamValueType.String,
                    AllowedValues = new[] { "ResetCounter", "Positive", "Negative" },
                    DisplayTier = ParamDisplayTier.Secondary,
                    Description = "原點復歸模式（標準值：ResetCounter 歸零計數、Positive 正向復歸、Negative 反向復歸，亦可填驅動自訂模式）"
                }
            },
            ImplicitEffects = { "reset error & Servo ON, home $(mot) -> sets #Homed?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@Stop",
            Summary = "停止指定軸控或全機所有運動控制器（dev 留空代表全停）",
            Parameters =
            {
                Opt("mot", ParamValueType.DriverName, "", 0, "運動控制器名稱（留空代表停止全部軸控）", ParamDisplayTier.Critical, false, "motor", "motion")
            },
            ImplicitEffects = { "clears $(mot)#Busy? & sets $(mot)#Done?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@Go",
            Summary = "依指定軸向與座標清單進行多軸插補移動（非阻塞，更新 #Busy?）",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                Req("axis", ParamValueType.String, 1, "目標軸向組合（如 XY、XYZ）", "axes"),
                ReqCrit("speed", ParamValueType.Number, 2, "移動速度（mm/s 或 %）"),
                Req("coords", ParamValueType.String, 3, "依序對應軸向之座標值清單", "pos", "val")
            },
            ImplicitEffects = { "sets $(mot)#Busy?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@Go(SingleAxis)",
            Summary = "單軸點對點座標移動（非阻塞，更新 #Busy?）",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                Req("axis", ParamValueType.String, 1, "單軸軸向（如 X、Y、Z）", "axes"),
                ReqCrit("speed", ParamValueType.Number, 2, "移動速度"),
                Req("coords", ParamValueType.String, 3, "目標座標值", "pos", "val")
            },
            ImplicitEffects = { "sets $(mot)#Busy?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@GoPoint",
            Summary = "移動至指定點位（支援笛卡爾與 #Joint 關節座標，非阻塞）",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                ReqCrit("speed", ParamValueType.Number, 1, "移動速度"),
                Req("pt", ParamValueType.String, 2, "目標點位名稱或 (X,Y,Z,...) 座標", "point", "target")
            },
            ImplicitEffects = { "sets $(mot)#Busy?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@GoPoint(SingleAxis)",
            Summary = "以單軸模式移動至指定點位（非阻塞）",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                ReqCrit("speed", ParamValueType.Number, 1, "移動速度"),
                Req("pt", ParamValueType.String, 2, "目標點位名稱或座標", "point", "target")
            },
            ImplicitEffects = { "sets $(mot)#Busy?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@GoPallet",
            Summary = "取出料盤下一點位並移動（自動遞增料盤游標，非阻塞）",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                ReqCrit("speed", ParamValueType.Number, 1, "移動速度"),
                Req("pt", ParamValueType.String, 2, "料盤點位名稱", "pallet", "point")
            },
            ImplicitEffects = { "advances $(pt).index & sets $(mot)#Busy?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@GoSafe",
            Summary = "先將安全軸退回安全高度，到位後再移動至目標點位",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                ReqCrit("speed", ParamValueType.Number, 1, "移動速度"),
                Req("pt", ParamValueType.String, 2, "目標點位名稱或座標", "point", "target"),
                new ParamSpec { Name = "axis", Aliases = new[] { "safeAxis" }, Direction = ParamDirection.Input, Required = false, DefaultValue = "Z", ValueType = ParamValueType.Enum, AllowedValues = new[] { "Z", "X", "Y" }, DisplayTier = ParamDisplayTier.Critical, Description = "先退避之安全軸（Z / X / Y）" },
                Opt("safePos", ParamValueType.Number, "0", null, "安全軸退避位置", ParamDisplayTier.Critical, false, "Z", "X", "Y")
            },
            ImplicitEffects = { "retreat $(axis)=$(safePos) & sets $(mot)#Busy?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@GoSafe(Pallet)",
            Summary = "先退回安全軸，再取出料盤下一點位移動",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                ReqCrit("speed", ParamValueType.Number, 1, "移動速度"),
                Req("pt", ParamValueType.String, 2, "料盤點位名稱", "pallet", "point"),
                new ParamSpec { Name = "axis", Aliases = new[] { "safeAxis" }, Direction = ParamDirection.Input, Required = false, DefaultValue = "Z", ValueType = ParamValueType.Enum, AllowedValues = new[] { "Z", "X", "Y" }, DisplayTier = ParamDisplayTier.Critical, Description = "先退避之安全軸（Z / X / Y）" },
                Opt("safePos", ParamValueType.Number, "0", null, "安全軸退避位置", ParamDisplayTier.Critical, false, "Z", "X", "Y")
            },
            ImplicitEffects = { "retreat $(axis)=$(safePos), advances $(pt).index & sets $(mot)#Busy?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@AddPath",
            Summary = "將一個路徑點加入軸控連續路徑佇列",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                ReqCrit("speed", ParamValueType.Number, 1, "段移動速度"),
                Req("pt", ParamValueType.String, 2, "路徑點位名稱或座標", "point")
            },
            ImplicitEffects = { "enqueues $(pt) to $(mot).Path" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@ClearPath",
            Summary = "清空軸控連續路徑佇列",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion")
            },
            ImplicitEffects = { "clears $(mot).Path" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@RunPath",
            Summary = "啟動執行軸控已加入之連續路徑佇列（非阻塞）",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                Opt("options", ParamValueType.String, "", 1, "路徑執行附加選項（選填）", ParamDisplayTier.Secondary, false, "extra")
            },
            ImplicitEffects = { "sets $(mot)#Busy? & runs $(mot).Path" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@GetPos",
            Summary = "讀取軸控目前座標，同步更新點位 $(mot) 與 $(mot)#Joint",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion")
            },
            ImplicitEffects = { "updates pt $(mot) & $(mot)#Joint" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@GetState",
            Summary = "讀取軸控運作與異警狀態，更新 #Busy?/#Alarm? 與 Motion#ErrorCode",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion")
            },
            ImplicitEffects = { "updates $(mot)#Busy?, #Alarm? & Motion#ErrorCode" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@WaitMotion",
            Summary = "等待軸控移動完成（$(mot)#Busy? 轉為 OFF），逾時觸發錯誤",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                Opt("timeout", ParamValueType.Number, "30", null, "等待逾時秒數（預設 30s）", ParamDisplayTier.Critical, false, "TimeOut")
            },
            ImplicitEffects = { "waits !$(mot)#Busy? & updates Motion#ErrorCode" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@WaitMotion(Error)",
            Summary = "等待軸控移動完成並即時監測 $(mot)#Alarm? 異警",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                Opt("timeout", ParamValueType.Number, "30", null, "等待逾時秒數（預設 30s）", ParamDisplayTier.Critical, false, "TimeOut")
            },
            ImplicitEffects = { "waits !$(mot)#Busy? & !$(mot)#Alarm? & updates Motion#ErrorCode" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@WaitPosition",
            Summary = "等待軸控即時位置進入目標點位容許誤差範圍內",
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                Req("tol", ParamValueType.Number, 1, "到位容許誤差（mm 或 deg）", "tolerance"),
                Req("pt", ParamValueType.String, 2, "目標點位名稱或座標", "point", "target")
            },
            ImplicitEffects = { "waits |$(mot) - $(pt)| <= $(tol)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@SetTool",
            Summary = "切換機械手臂作用中之工具座標系 (Tool Frame)",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                Req("pt", ParamValueType.String, 1, "工具偏置點位名稱或座標", "tool", "toolPt")
            },
            ImplicitEffects = { "sets $(mot)#Tool" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@Config",
            Summary = "設定軸控驅動內部組態參數鍵值",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                Req("key", ParamValueType.String, 1, "組態參數名稱", "param"),
                Req("val", ParamValueType.String, 2, "組態參數數值", "value")
            },
            ImplicitEffects = { "sets $(mot) config $(key)=$(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Motion@ExtraCMD",
            Summary = "下達各品牌機械手臂控制器原生擴充指令字串",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("mot", ParamValueType.DriverName, 0, "運動控制器名稱", "motor", "motion"),
                Req("cmd", ParamValueType.MultiLineText, 1, "原生控制器指令字串（支援多行腳本）", "command")
            },
            ImplicitEffects = { "runs native $(cmd) on $(mot)" }
        });

        // =====================================================================
        // 7. CAPTURE 模組 (28 條指令) — cam / imaqs / pt / var
        // =====================================================================

        Reg(new CommandSchema
        {
            CommandKey = "Capture@AddCam",
            Summary = "註冊並初始化視覺相機驅動實例",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "相機名稱（如 Cam0）", "imaq", "name"),
                Req("driver", ParamValueType.DriverName, 1, "相機驅動名稱（如 MockCamera）", "driverType"),
                Opt("device", ParamValueType.String, "", null, "實體相機索引/連線位址 (索引號、IP、序號等)", ParamDisplayTier.Secondary, false, "DEVICE"),
                Opt("width", ParamValueType.Integer, "", null, "影像寬度 px（選填）", ParamDisplayTier.Secondary, false, "Width"),
                Opt("height", ParamValueType.Integer, "", null, "影像高度 px（選填）", ParamDisplayTier.Secondary, false, "Height"),
                Opt("exposure", ParamValueType.Number, "", null, "曝光時間 ms（選填）", ParamDisplayTier.Secondary, false, "Exposure"),
                Opt("file", ParamValueType.FilePath, "", null, "模擬預載圖檔路徑（選填）", ParamDisplayTier.Secondary, false, "File", "Path"),
                Opt("options", ParamValueType.String, "", 2, "其他相機驅動擴充鍵值參數", ParamDisplayTier.Secondary, false, "extra")
            },
            ImplicitEffects = { "reg camera driver $(cam)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@Config",
            Summary = "設定相機驅動內部參數（如 Exposure、Gain、File）",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "相機名稱", "imaq"),
                Req("key", ParamValueType.String, 1, "相機組態參數名稱", "param"),
                Req("val", ParamValueType.String, 2, "相機組態參數值", "value")
            },
            ImplicitEffects = { "sets $(cam) config $(key)=$(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@Snap",
            Summary = "單次觸發相機取像並覆寫同名影像槽 $(cam)，可選同步 Append 至指定 IMAQ 陣列",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "相機名稱（預設取像覆寫同名影像槽 $(cam)）", "imaq"),
                Opt("imaqs", ParamValueType.String, "", 1, "可選：覆寫 $(cam) 後同步 Append 至指定 IMAQ 影像陣列", ParamDisplayTier.Secondary, false, "array", "imaq")
            },
            ImplicitEffects = { "overwrites imaq $(cam) & sets $(cam)#OK?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@Trigger",
            Summary = "執行飛拍觸發取像並覆寫同名影像槽 $(cam)，可選連同 Metadata 同步 Append 至指定 IMAQ 陣列",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "相機名稱（預設取像覆寫同名影像槽 $(cam)）", "imaq"),
                Opt("imaqs", ParamValueType.String, "", 1, "可選：覆寫 $(cam) 後同步 Append 至指定 IMAQ 影像陣列", ParamDisplayTier.Secondary, false, "array", "imaq")
            },
            ImplicitEffects = { "overwrites imaq $(cam) & sets $(cam)#OK?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@UpdateImage",
            Summary = "即時更新指定相機之預覽影像緩衝區",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "相機名稱", "imaq")
            },
            ImplicitEffects = { "refresh preview $(cam)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@LoadFile",
            Summary = "自檔案載入單張影像至指定 ImageEngine 槽位",
            Parameters =
            {
                Req("imaqs", ParamValueType.String, 0, "目標影像槽位名稱", "cam", "imaq"),
                Req("file", ParamValueType.FilePath, 1, "來源圖檔路徑", "file")
            },
            ImplicitEffects = { "loads $(file) into imaq $(imaqs)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@ImageSave",
            Summary = "將影像連同校正與中繼資料儲存為圖檔",
            Parameters =
            {
                Req("imaqs", ParamValueType.String, 0, "來源影像槽位名稱", "cam", "imaq"),
                Req("file", ParamValueType.FilePath, 1, "輸出圖檔路徑", "file"),
                Opt("burnOverlays", ParamValueType.Boolean, "false", null, "是否將視覺疊加層燒錄進圖檔", ParamDisplayTier.Secondary, false, "BurnOverlays", "IncludeOverlays")
            },
            ImplicitEffects = { "saves imaq $(imaqs) to $(file)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@ClearImaq",
            Summary = "清除指定 IMAQ 影像槽位或影像陣列",
            Parameters =
            {
                Req("imaqs", ParamValueType.String, 0, "目標影像陣列名稱", "imaq", "cam")
            },
            ImplicitEffects = { "clears imaq $(imaqs)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@LoadFile2Imaq",
            Summary = "將圖檔或資料夾內影像批次載入至 IMAQ 影像陣列",
            Parameters =
            {
                Req("imaqs", ParamValueType.String, 0, "目標 IMAQ 影像陣列名稱", "imaq", "array"),
                Req("file", ParamValueType.FilePath, 1, "來源圖檔路徑或正規表示式（如 PICTURE/CAM0/img_\\d+\\.png）", "pattern", "path")
            },
            ImplicitEffects = { "appends $(file) into imaqs $(imaqs)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@LoadNextImaq",
            Summary = "從 IMAQ 影像陣列取出下一張影像載入至指定 Capture UI 影像槽位",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "目標 Capture UI 影像槽位/相機名稱", "to", "dst", "imaq"),
                Req("imaqs", ParamValueType.String, 1, "來源 IMAQ 影像陣列名稱", "src", "array")
            },
            ImplicitEffects = { "advances $(imaqs).index -> $(cam)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@ResetImaqIndex",
            Summary = "重設 IMAQ 影像陣列之目前讀取索引游標",
            Parameters =
            {
                Req("imaqs", ParamValueType.String, 0, "IMAQ 影像陣列名稱", "imaq", "array"),
                Opt("index", ParamValueType.Integer, "0", 1, "目標起始索引（預設 0）", ParamDisplayTier.Secondary, false, "idx")
            },
            ImplicitEffects = { "sets $(imaqs).index = $(index)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@Display2User",
            Summary = "將指定影像槽位畫面顯示至人機面板控制項",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                Req("ui", ParamValueType.String, 1, "目標人機影像控制項名稱", "ctrl", "control", "target")
            },
            ImplicitEffects = { "renders imaq $(cam) -> UI $(ui)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@ROISet",
            Summary = "設定指定影像之感興趣檢測區域 (ROI: X,Y,W,H)",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                Req("roi", ParamValueType.String, 1, "ROI 區域定義（如 \"10,20,300,200\"）")
            },
            ImplicitEffects = { "sets ROI on $(cam)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@ROISave",
            Summary = "將目前影像 ROI 區域存檔",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                Req("file", ParamValueType.FilePath, 1, "儲存路徑", "file")
            },
            ImplicitEffects = { "saves ROI of $(cam) to $(file)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@ROITranslate",
            Summary = "依基準點位與新點位平移補償影像 ROI 位置",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                Req("basePt", ParamValueType.String, 1, "原始基準點位"),
                Req("newPt", ParamValueType.String, 2, "新量測基準點位")
            },
            ImplicitEffects = { "translates ROI on $(cam)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@CalibrateLoad",
            Summary = "自校正圖檔載入像素至實體座標轉換矩陣",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                Req("file", ParamValueType.FilePath, 1, "校正圖檔路徑", "file")
            },
            ImplicitEffects = { "loads calibration onto $(cam)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@CalibrateManual",
            Summary = "手動設定影像之比例尺 (mm/px)、原點與旋轉角校正",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                Opt("scale", ParamValueType.Number, "0.05", 1, "像素比例尺 (mm/px)", ParamDisplayTier.Primary, false, "Scale"),
                Opt("originX", ParamValueType.Number, "0", null, "原點 X 座標", ParamDisplayTier.Secondary, false, "OriginX"),
                Opt("originY", ParamValueType.Number, "0", null, "原點 Y 座標", ParamDisplayTier.Secondary, false, "OriginY"),
                Opt("angle", ParamValueType.Number, "0", null, "旋轉角度 (deg)", ParamDisplayTier.Secondary, false, "Angle")
            },
            ImplicitEffects = { "applies manual calibration to $(cam)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@CoordSet",
            Summary = "依點位陣列建立視覺影像座標系",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                Req("pt", ParamValueType.String, 1, "定義座標系之點位陣列名稱", "point")
            },
            ImplicitEffects = { "sets vision coord on $(cam)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@CoordSet(2Edge)",
            Summary = "以 X/Y 雙邊端點建立視覺影像座標系",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                Req("xPt", ParamValueType.String, 1, "X 軸點位名稱", "xEnd"),
                Req("yPt", ParamValueType.String, 2, "Y 軸點位名稱", "yEnd")
            },
            ImplicitEffects = { "sets 2-edge coord on $(cam)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Capture@CoordSet(Org+2Edge)",
            Summary = "以原點與 X/Y 雙邊建立視覺影像座標系",
            Parameters =
            {
                Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                Req("orgPt", ParamValueType.String, 1, "原點點位名稱", "org"),
                Req("xPt", ParamValueType.String, 2, "X 軸點位名稱", "xEnd"),
                Req("yPt", ParamValueType.String, 3, "Y 軸點位名稱", "yEnd")
            },
            ImplicitEffects = { "sets org+2-edge coord on $(cam)" }
        });

        foreach (var (findKey, findDesc) in new[]
        {
            ("Capture@FindCircle(InOut)", "由內向外搜尋圓形特徵，結果點位輸出至 pt 並更新 $(pt)#Find?"),
            ("Capture@FindCircle(OutIn)", "由外向內搜尋圓形特徵，結果點位輸出至 pt 並更新 $(pt)#Find?"),
            ("Capture@FindEdge", "搜尋影像直線邊緣特徵，結果點位輸出至 pt 並更新 $(pt)#Find?"),
            ("Capture@FindGap", "搜尋兩邊緣間隙寬度與中心，結果點位輸出至 pt 並更新 $(pt)#Find?"),
            ("Capture@FindPoint", "搜尋目標特徵點座標，結果點位輸出至 pt 並更新 $(pt)#Find?")
        })
        {
            Reg(new CommandSchema
            {
                CommandKey = findKey,
                Summary = findDesc,
                Parameters =
                {
                    Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                    OutPos("pt", ParamValueType.String, 1, "量測結果輸出點位名稱", false, "point", "dest")
                },
                ImplicitEffects = { "sets $(pt)#Find?" }
            });
        }

        foreach (var (matchKey, matchDesc) in new[]
        {
            ("Capture@GeoMatchOne", "執行幾何形狀特徵單一比對，輸出分數至 var、位置至 pt"),
            ("Capture@PatternMatchOne", "執行灰階樣板單一比對，輸出分數至 var、位置至 pt"),
            ("Capture@PatternMatchOne(Fast)", "執行快速灰階樣板單一比對，輸出分數至 var、位置至 pt")
        })
        {
            Reg(new CommandSchema
            {
                CommandKey = matchKey,
                Summary = matchDesc,
                Parameters =
                {
                    Req("cam", ParamValueType.DriverName, 0, "Capture UI 影像槽位/相機名稱", "imaqs", "imaq"),
                    Req("file", ParamValueType.FilePath, 1, "樣板圖檔或模型檔路徑", "template", "path"),
                    Opt("angle", ParamValueType.Number, "", 4, "允許旋轉角度容差範圍 (deg)"),
                    OutPos("var", ParamValueType.VariableName, 2, "輸出比對分數之變數名稱", false, "score", "scoreVar"),
                    OutPos("pt", ParamValueType.String, 3, "輸出比對結果座標之點位名稱", false, "point", "dest")
                },
                ImplicitEffects = { "sets $(pt)#Find?" }
            });
        }

        // =====================================================================
        // 8. DAQ 模組 (18 條指令) — daq / wav / wavs / timeout*
        // =====================================================================

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@AddDAQ",
            Summary = "註冊並初始化資料擷取 (DAQ) 驅動實例",
            Parameters =
            {
                Req("daq", ParamValueType.DriverName, 0, "DAQ 裝置名稱（如 Daq0）", "name"),
                Req("driver", ParamValueType.DriverName, 1, "DAQ 驅動名稱（如 MockDAQ）", "driverType"),
                Opt("device", ParamValueType.String, "", null, "實體裝置/連線位址 (如 Dev1、COM 埠、IP、OPC-UA 等)", ParamDisplayTier.Secondary, false, "DEVICE"),
                Opt("rate", ParamValueType.Number, "", null, "取樣頻率 Hz（與 sec 決定環形緩衝容量 = rate * sec）", ParamDisplayTier.Secondary, false, "Rate"),
                Opt("sec", ParamValueType.Number, "", null, "驅動環形緩衝區時間長度（秒，容量 = rate * sec）", ParamDisplayTier.Secondary, false, "Sec", "Duration", "BufferSec", "count", "Count"),
                Opt("wave", ParamValueType.String, "", null, "模擬波形類型（選填）", ParamDisplayTier.Secondary, false, "Wave"),
                Opt("options", ParamValueType.String, "", 2, "其他驅動擴充鍵值參數", ParamDisplayTier.Secondary, false, "extra")
            },
            ImplicitEffects = { "reg DAQ driver $(daq) with ring buffer (rate*sec)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@Start",
            Summary = "啟動 DAQ 驅動連續採樣並以環形緩衝區持續寫入資料",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("daq", ParamValueType.DriverName, 0, "DAQ 裝置名稱")
            },
            ImplicitEffects = { "starts ring-buffer acquisition on $(daq)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@Stop",
            Summary = "停止 DAQ 驅動連續採樣（保留環形緩衝區內既有資料）",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("daq", ParamValueType.DriverName, 0, "DAQ 裝置名稱")
            },
            ImplicitEffects = { "stops ring-buffer acquisition on $(daq)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@Wait",
            Summary = "等待 DAQ 採樣完成旗標 $(daq)#OK? 轉為 ON",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("daq", ParamValueType.DriverName, 0, "DAQ 裝置名稱"),
                Opt("timeout", ParamValueType.Number, "5", null, "等待逾時秒數（預設 5s）", ParamDisplayTier.Critical, false, "TimeOut")
            },
            ImplicitEffects = { "waits $(daq)#OK?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@AnalogIn",
            Summary = "自 DAQ 驅動環形緩衝區取出最新一筆單點數值並寫入指定變數（供 Variable 條件比對）",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("daq", ParamValueType.DriverName, 0, "DAQ 裝置名稱"),
                OutPos("var", ParamValueType.VariableName, 1, "寫入最新單點類比數值之目標變數名稱", true, "target", "val", "wav")
            },
            ImplicitEffects = { "reads latest sample -> $(var) & sets $(daq)#OK?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@AnalogInWaveForm",
            Summary = "自 DAQ 驅動環形緩衝區擷取最近指定時間長度 (sec) 之波形並輸出至目標波形",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("daq", ParamValueType.DriverName, 0, "DAQ 裝置名稱"),
                Opt("sec", ParamValueType.Number, "", 1, "擷取最近波形之時間長度（秒；留空取整段環形緩衝）", ParamDisplayTier.Secondary, false, "duration"),
                OutPos("wav", ParamValueType.String, 2, "可選：將擷取之最近波形寫入指定波形名稱", false, "waveform", "wf")
            },
            ImplicitEffects = { "slices recent $(sec)s waveform from $(daq) ring buffer & sets $(daq)#OK?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@AnalogOut",
            Summary = "輸出單點類比電壓/數值至 DAQ 類比輸出通道",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("daq", ParamValueType.DriverName, 0, "DAQ 裝置名稱"),
                Req("val", ParamValueType.String, 1, "輸出數值或通道=數值規格", "value", "ch", "channel")
            },
            ImplicitEffects = { "writes AO on $(daq)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@AnalogOutWaveForm",
            Summary = "將 DaqEngine 中之指定波形串流輸出至 DAQ 裝置",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("daq", ParamValueType.DriverName, 0, "DAQ 裝置名稱"),
                Req("wav", ParamValueType.String, 1, "來源波形名稱", "waveform", "wf")
            },
            ImplicitEffects = { "streams wav $(wav) to $(daq)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@WaveFormGenerate",
            Summary = "在 DaqEngine 生成標準測試波形（Sine/Square/Triangle/Press）",
            Parameters =
            {
                Req("wav", ParamValueType.String, 0, "目標波形名稱", "waveform", "wf"),
                Opt("type", ParamValueType.Enum, "Sine", 1, "波形類型（Sine/Square/Triangle/Press）", ParamDisplayTier.Primary, false, "Type"),
                Opt("rate", ParamValueType.Number, "1000", null, "取樣率 Hz", ParamDisplayTier.Secondary, false, "Rate"),
                Opt("sec", ParamValueType.Number, "1", null, "波形時間長度（秒；樣本點數 = rate * sec）", ParamDisplayTier.Secondary, false, "Sec", "duration"),
                Opt("amp", ParamValueType.Number, "5", null, "振幅大小", ParamDisplayTier.Secondary, false, "Amp"),
                Opt("offset", ParamValueType.Number, "0", null, "直流偏置", ParamDisplayTier.Secondary, false, "Offset")
            },
            ImplicitEffects = { "generates wav $(wav) (rate*sec)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@WaveFormLoad",
            Summary = "自指定檔案載入波形資料至 DaqEngine",
            Parameters =
            {
                Req("wav", ParamValueType.String, 0, "目標波形名稱", "waveform", "wf"),
                Req("file", ParamValueType.FilePath, 1, "來源波形檔案路徑", "path")
            },
            ImplicitEffects = { "loads $(file) into wav $(wav)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@WaveFormSave",
            Summary = "將指定波形資料儲存至目標檔案",
            Parameters =
            {
                Req("wav", ParamValueType.String, 0, "來源波形名稱", "waveform", "wf"),
                Req("file", ParamValueType.FilePath, 1, "輸出波形檔案路徑", "path")
            },
            ImplicitEffects = { "saves wav $(wav) to $(file)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@Calculate",
            Summary = "執行波形逐點四則運算並寫入目標波形（顯式目標置於首欄）",
            Parameters =
            {
                Req("wav", ParamValueType.String, 0, "結果波形名稱", "target", "wf"),
                Req("expr", ParamValueType.String, 1, "波形算式（如 \"W1 + W2\" 或 \"W1 * 0.5\"）")
            },
            ImplicitEffects = { "$(wav) = eval($(expr))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@Filter",
            Summary = "對指定波形套用數位濾波器（MovingAverage / Median 等）",
            Parameters =
            {
                Req("wav", ParamValueType.String, 0, "目標波形名稱", "waveform", "wf"),
                Req("type", ParamValueType.Enum, 1, "濾波器類型（MovingAverage / Median）", "filter"),
                Opt("window", ParamValueType.Number, "3", 2, "濾波視窗/參數大小", ParamDisplayTier.Secondary, false, "param", "size")
            },
            ImplicitEffects = { "filters wav $(wav)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@MergeWaveForm",
            Summary = "將多個來源波形依序串接合併至目標波形",
            Parameters =
            {
                Req("wav", ParamValueType.String, 0, "合併後目標波形名稱", "target", "wf"),
                Req("wavs", ParamValueType.PortList, 1, "來源波形清單（如 [W1;W2]）", "sources")
            },
            ImplicitEffects = { "merges $(wavs) -> $(wav)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@CompareAvgInRange",
            Summary = "檢查波形指定時間區間（秒）平均值是否落於 [min, max] 範圍並更新 $(wav)#OK?",
            Parameters =
            {
                Req("wav", ParamValueType.String, 0, "受測波形名稱", "waveform", "wf"),
                Req("max", ParamValueType.Number, 1, "上限門檻值"),
                Req("min", ParamValueType.Number, 2, "下限門檻值"),
                Opt("start", ParamValueType.Number, "0", 3, "起始時間（秒）", ParamDisplayTier.Secondary, false, "startSec"),
                Opt("end", ParamValueType.Number, "-1", 4, "結束時間（秒；-1 代表結尾）", ParamDisplayTier.Secondary, false, "endSec")
            },
            ImplicitEffects = { "sets $(wav)#OK?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@CompareMaxMinInRange",
            Summary = "檢查波形指定時間區間（秒）最大與最小值是否皆落於 [min, max] 並更新 $(wav)#OK?",
            Parameters =
            {
                Req("wav", ParamValueType.String, 0, "受測波形名稱", "waveform", "wf"),
                Req("max", ParamValueType.Number, 1, "上限門檻值"),
                Req("min", ParamValueType.Number, 2, "下限門檻值"),
                Opt("start", ParamValueType.Number, "0", 3, "起始時間（秒）", ParamDisplayTier.Secondary, false, "startSec"),
                Opt("end", ParamValueType.Number, "-1", 4, "結束時間（秒；-1 代表結尾）", ParamDisplayTier.Secondary, false, "endSec")
            },
            ImplicitEffects = { "sets $(wav)#OK?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@CompareCoverBy",
            Summary = "檢查波形是否完整包絡於上下參考波形之間並更新 $(wav)#OK?",
            Parameters =
            {
                Req("wav", ParamValueType.String, 0, "受測波形名稱", "waveform", "wf"),
                Req("upper", ParamValueType.String, 1, "上包絡線波形名稱"),
                Opt("lower", ParamValueType.String, "", 2, "下包絡線波形名稱（選填）")
            },
            ImplicitEffects = { "sets $(wav)#OK?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "DAQ@Display2User",
            Summary = "將波形渲染至人機面板控制項（或匯出 .svg / .html）",
            Parameters =
            {
                Req("wav", ParamValueType.String, 0, "來源波形名稱", "waveform", "wf"),
                Req("ui", ParamValueType.String, 1, "人機波形控制項名稱或 .svg/.html 檔名", "ctrl", "control", "target")
            },
            ImplicitEffects = { "renders wav $(wav) -> $(ui)" }
        });

        // =====================================================================
        // 9. USER 模組 (21 條指令) — panel / ui / var
        // =====================================================================

        Reg(new CommandSchema
        {
            CommandKey = "User@SwitchUserPanel",
            Summary = "切換目前作用中之客製人機操作面板 (UserPanel)",
            Parameters =
            {
                Req("panel", ParamValueType.String, 0, "已載入之面板名稱", "name")
            },
            ImplicitEffects = { "switches ActivePanel -> $(panel)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@Param2UserPanel",
            Summary = "觸發 UserPanel 同步重新整理所有綁定參數",
            Parameters = { },
            ImplicitEffects = { "refreshes UserPanel bindings" }
        });

        foreach (var (visKey, visDesc, visEff) in new[]
        {
            ("User@ShowControl", "顯示一個或多個人機控制項", "shows $(ui)"),
            ("User@HideControl", "隱藏一個或多個人機控制項", "hides $(ui)"),
            ("User@EnableControl", "啟用一個或多個可互動人機控制項", "enables $(ui)"),
            ("User@DisableControl", "停用（灰化鎖定）一個或多個控制項", "disables $(ui)")
        })
        {
            Reg(new CommandSchema
            {
                CommandKey = visKey,
                Summary = visDesc,
                Parameters =
                {
                    Req("ui", ParamValueType.PortList, 0, "控制項名稱或清單（如 Btn1 或 [Btn1;Btn2]）", "ctrls", "ctrl", "control")
                },
                ImplicitEffects = { visEff }
            });
        }

        Reg(new CommandSchema
        {
            CommandKey = "User@GetControlValue",
            Summary = "讀取人機控制項目前數值並輸出至指定變數（未指定預設存至 $UserPanel_$(ui)）",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "來源控制項名稱", "ctrl", "control"),
                OutPos("var", ParamValueType.VariableName, 1, "可選：接收數值之目標變數名稱（裸名稱）", false, "dest")
            },
            ImplicitEffects = { "reads UI $(ui) -> var (default $UserPanel_$(ui))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@SetControlValue",
            Summary = "通用寫入人機控制項數值（不觸發使用者事件）",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "目標控制項名稱", "ctrl", "control"),
                Req("val", ParamValueType.String, 1, "寫入數值或文字", "value", "text")
            },
            ImplicitEffects = { "sets UI $(ui)=$(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@QueryControls",
            Summary = "查詢面板控制項清單並輸出逗號分隔結果至變數",
            Parameters =
            {
                Opt("panel", ParamValueType.String, "Active", null, "目標面板篩選（Active / * / 面板名）", ParamDisplayTier.Secondary, false, "Panel"),
                Opt("role", ParamValueType.String, "All", null, "控制項角色篩選（All / Control / Indicator）", ParamDisplayTier.Secondary, false, "Role"),
                Opt("kind", ParamValueType.String, "All", null, "控制項型別篩選（All / Button / TextBox...）", ParamDisplayTier.Secondary, false, "Kind"),
                OutPos("var", ParamValueType.VariableName, 0, "可選：接收控制項清單之目標變數（預設 $UserPanelControls）", false, "dest")
            },
            ImplicitEffects = { "writes control list -> var (default $UserPanelControls)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@ButtonValue",
            Summary = "設定按鈕開關狀態（不觸發點擊事件；支援 $logic 邏輯運算式）",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "按鈕控制項名稱", "ctrl", "btn", "control"),
                Req("logic", ParamValueType.LogicExpr, 1, "開關狀態或 $logic 邏輯運算式（如 True/False/AutoMode?）", "on", "val", "state")
            },
            ImplicitEffects = { "sets button $(ui)=eval($(logic))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@ButtonTrigger",
            Summary = "程式觸發按鈕 ON/OFF 狀態並同步觸發事件與 UserPanel#$(ui)? 旗標",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "按鈕控制項名稱", "ctrl", "btn", "control"),
                Req("logic", ParamValueType.LogicExpr, 1, "觸發 ON/OFF 狀態或 $logic 邏輯運算式（如 True/False/Ready?）", "on", "val", "state")
            },
            ImplicitEffects = { "fires button $(ui)=eval($(logic)) & sets UserPanel#$(ui)?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@ButtonText",
            Summary = "更新按鈕顯示文字標題",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "按鈕控制項名稱", "ctrl", "btn", "control"),
                Req("text", ParamValueType.String, 1, "按鈕顯示文字", "val")
            },
            ImplicitEffects = { "sets button $(ui).Text" }
        });

        foreach (var (blinkKey, blinkDesc) in new[]
        {
            ("User@ButtonBlinking", "控制按鈕警示閃爍開關"),
            ("User@TextBoxBlinking", "控制文字框警示閃爍開關"),
            ("User@TextRingBlinking", "控制下拉選單 (TextRing) 警示閃爍開關")
        })
        {
            Reg(new CommandSchema
            {
                CommandKey = blinkKey,
                Summary = blinkDesc,
                Parameters =
                {
                    Req("ui", ParamValueType.String, 0, "控制項名稱", "ctrl", "btn", "box", "combo", "control"),
                    Req("logic", ParamValueType.LogicExpr, 1, "閃爍開關狀態或 $logic 邏輯運算式（如 True/False/Alarm?）", "on", "val", "state")
                },
                ImplicitEffects = { "sets $(ui).Blinking=eval($(logic))" }
            });
        }

        Reg(new CommandSchema
        {
            CommandKey = "User@TextBoxValue",
            Summary = "更新文字框顯示內容（不觸發變更事件）",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "文字框控制項名稱", "ctrl", "box", "control"),
                Req("text", ParamValueType.String, 1, "設定文字內容", "val")
            },
            ImplicitEffects = { "sets TextBox $(ui)=$(text)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@TextBoxTrigger",
            Summary = "更新文字框內容並觸發事件與綁定變數同步",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "文字框控制項名稱", "ctrl", "box", "control"),
                Req("text", ParamValueType.String, 1, "設定文字內容", "val")
            },
            ImplicitEffects = { "fires TextBox $(ui) & syncs VAR binding" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@TextRingValue",
            Summary = "切換下拉選單 (TextRing) 選項（不觸發變更事件）",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "下拉選單控制項名稱", "ctrl", "combo", "control"),
                Req("val", ParamValueType.String, 1, "選項名稱或索引", "text")
            },
            ImplicitEffects = { "sets TextRing $(ui)=$(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@TextRingTrigger",
            Summary = "切換下拉選單選項並觸發事件與綁定變數同步",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "下拉選單控制項名稱", "ctrl", "combo", "control"),
                Req("val", ParamValueType.String, 1, "選項名稱或索引", "text")
            },
            ImplicitEffects = { "fires TextRing $(ui) & syncs VAR binding" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@TabValue",
            Summary = "切換分頁控制項 (TabControl) 作用頁籤（不觸發事件）",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "分頁控制項名稱", "ctrl", "tab", "control"),
                Req("page", ParamValueType.String, 1, "頁籤索引或名稱", "val", "index")
            },
            ImplicitEffects = { "selects Tab $(ui)=$(page)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "User@TabTrigger",
            Summary = "切換分頁控制項作用頁籤並觸發切換事件",
            Parameters =
            {
                Req("ui", ParamValueType.String, 0, "分頁控制項名稱", "ctrl", "tab", "control"),
                Req("page", ParamValueType.String, 1, "頁籤索引或名稱", "val", "index")
            },
            ImplicitEffects = { "fires Tab $(ui)=$(page)" }
        });

        // =====================================================================
        // 10. PYTHON 模組 (10 條指令) — session / py / var / pt / cam / code
        // =====================================================================

        Reg(new CommandSchema
        {
            CommandKey = "Python@Init",
            Summary = "初始化 Python 執行環境並於存在時自動載入 init.py",
            Parameters =
            {
                Opt("session", ParamValueType.String, "", null, "指定隔離 Session 名稱（留空隨 Section/Thread）", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "init Python runtime" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Python@Reset",
            Summary = "重置 Python Session 狀態（留空重置預設 Session，填名稱重置指定 Session，填 ALL/* 重置全部 Session）",
            Parameters =
            {
                Opt("session", ParamValueType.String, "", 0, "目標 Session 名稱（留空=預設 Session，ALL/*=全部重置）", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "resets Python session $(session) (empty=default, ALL/*=all)" }
        }, "Python@ResetSession");

        Reg(new CommandSchema
        {
            CommandKey = "Python@File",
            Summary = "於指定 Python Session 執行外部 .py 腳本檔案",
            Parameters =
            {
                Req("file", ParamValueType.FilePath, 0, "Python 腳本檔案路徑", "file"),
                Opt("session", ParamValueType.String, "", null, "指定隔離 Session 名稱", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "executes $(file) in Python" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Python@Script",
            Summary = "於指定 Python Session 執行內嵌單行或多行 Python 程式碼",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("code", ParamValueType.MultiLineText, 0, "內嵌 Python 程式碼內容", "script"),
                Opt("session", ParamValueType.String, "", null, "指定隔離 Session 名稱", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "executes inline Python code" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Python@SetVariable",
            Summary = "將 ARCOS 變數或常數送入 Python Session 命名空間",
            Parameters =
            {
                Req("var", ParamValueType.VariableName, 0, "來源 ARCOS 變數名稱（裸名稱）", "name"),
                Opt("val", ParamValueType.String, "", 1, "直接指定常數值覆寫（留空則讀取 ARCOS 變數值）", ParamDisplayTier.Secondary, false, "value"),
                Opt("py", ParamValueType.String, "", null, "Python 端目標變數名稱（留空與 var 同名）", ParamDisplayTier.Secondary, false, "PythonName", "pyName"),
                Opt("session", ParamValueType.String, "", null, "指定隔離 Session 名稱", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "pushes var $(var) -> Python $(py)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Python@GetVariable",
            Summary = "自 Python Session 讀取變數值並回寫至 ARCOS VariableEngine",
            Parameters =
            {
                Req("py", ParamValueType.String, 0, "來源 Python 變數名稱", "pyName"),
                Opt("session", ParamValueType.String, "", null, "指定隔離 Session 名稱", ParamDisplayTier.Secondary, false, "Session"),
                Out("var", ParamValueType.VariableName, "可選：接收之 ARCOS 變數名稱（留空與 py 同名）", "ARCOSName", "arcosName")
            },
            ImplicitEffects = { "pulls Python $(py) -> ARCOS var" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Python@SetPoint",
            Summary = "將 ARCOS 點位送入 Python Session（含 XYZUVW 座標）",
            Parameters =
            {
                Req("pt", ParamValueType.String, 0, "來源 ARCOS 點位名稱", "point"),
                Opt("py", ParamValueType.String, "", null, "Python 端目標名稱（留空與 pt 同名）", ParamDisplayTier.Secondary, false, "PythonName", "pyName"),
                Opt("session", ParamValueType.String, "", null, "指定隔離 Session 名稱", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "pushes pt $(pt) -> Python $(py)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Python@GetPoint",
            Summary = "自 Python Session 讀取點位並回寫至 ARCOS PointEngine",
            Parameters =
            {
                Req("py", ParamValueType.String, 0, "來源 Python 點位物件名稱", "pyName"),
                Opt("session", ParamValueType.String, "", null, "指定隔離 Session 名稱", ParamDisplayTier.Secondary, false, "Session"),
                Out("pt", ParamValueType.String, "可選：接收之 ARCOS 點位名稱（留空與 py 同名）", "ARCOSName", "arcosName", "point")
            },
            ImplicitEffects = { "pulls Python $(py) -> ARCOS pt" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Python@SetCapture",
            Summary = "將 ARCOS ImageEngine 影像緩衝直接傳入 Python Session (numpy/Mat)",
            Parameters =
            {
                Req("cam", ParamValueType.String, 0, "來源 ARCOS 影像槽位名稱", "imaqs", "imaq"),
                Opt("py", ParamValueType.String, "", null, "Python 端目標變數名稱（留空與 cam 同名）", ParamDisplayTier.Secondary, false, "PythonName", "pyName"),
                Opt("session", ParamValueType.String, "", null, "指定隔離 Session 名稱", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "pushes cam $(cam) -> Python $(py)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Python@GetCapture",
            Summary = "自 Python Session 取得處理後影像並顯式回寫至 ARCOS ImageEngine 指定影像槽",
            Parameters =
            {
                Req("py", ParamValueType.String, 0, "來源 Python 影像物件名稱", "pyName"),
                Opt("session", ParamValueType.String, "", null, "指定隔離 Session 名稱", ParamDisplayTier.Secondary, false, "Session"),
                OutPos("cam", ParamValueType.String, null, "接收之 ARCOS 影像槽位名稱（顯式必填）", true, "ARCOSName", "arcosName", "imaqs", "imaq")
            },
            ImplicitEffects = { "pulls Python $(py) -> ARCOS cam $(cam)" }
        });

        // =====================================================================
        // 11. REDIS 模組 (9 條指令) — session / key / var
        //     規則：載入 REDIS 模組時自動啟動本機 127.0.0.1:6379 作為預設 Session；CreateSession 用於連線其他遠端 Redis 或獨立 DB
        // =====================================================================

        Reg(new CommandSchema
        {
            CommandKey = "Redis@CreateSession",
            Summary = "建立具名 Redis 連線 Session（用於連線其他 Redis 伺服器或獨立 DB，不覆蓋本機預設 Session）",
            Parameters =
            {
                Req("session", ParamValueType.String, 0, "Redis Session 名稱（如 MES、Line2）", "name"),
                Opt("url", ParamValueType.String, "127.0.0.1:6379", 1, "Redis 伺服器位址（host:port、host:port/db 或 redis://...）", ParamDisplayTier.Secondary, false, "endpoint", "addr", "host")
            },
            ImplicitEffects = { "sets RedisErr#$(session)=OK (connects external/named Redis session)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Redis@Set",
            Summary = "寫入 Redis Key-Value 鍵值資料（未指定 session 則使用預設 Session）",
            Parameters =
            {
                Req("key", ParamValueType.String, 0, "Redis 鍵名稱"),
                Req("val", ParamValueType.String, 1, "寫入值", "value"),
                Opt("session", ParamValueType.String, "", null, "目標 Redis Session（留空使用預設 Session）", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "sets RedisKV[$(key)]=$(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Redis@Get",
            Summary = "讀取 Redis Key 值並寫入 ARCOS 變數（未指定 var 則同名寫回）",
            Parameters =
            {
                Req("key", ParamValueType.String, 0, "Redis 鍵名稱"),
                Opt("session", ParamValueType.String, "", null, "目標 Redis Session（留空使用預設 Session）", ParamDisplayTier.Secondary, false, "Session"),
                OutPos("var", ParamValueType.VariableName, 1, "可選：接收之 ARCOS 變數名稱（支援 Var@Section）", false, "target", "dest")
            },
            ImplicitEffects = { "reads RedisKV[$(key)] -> var (default $(key))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Redis@LPush",
            Summary = "將資料推入指定 Redis 佇列清單",
            Parameters =
            {
                Req("key", ParamValueType.String, 0, "Redis 佇列鍵名稱", "queue"),
                Req("val", ParamValueType.String, 1, "推入內容", "value"),
                Opt("session", ParamValueType.String, "", null, "目標 Redis Session（留空使用預設 Session）", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "pushes $(val) -> RedisList[$(key)]" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Redis@LPop",
            Summary = "非阻塞自 Redis 佇列彈出首筆資料並存入變數",
            Parameters =
            {
                Req("key", ParamValueType.String, 0, "Redis 佇列鍵名稱", "queue"),
                Opt("session", ParamValueType.String, "", null, "目標 Redis Session（留空使用預設 Session）", ParamDisplayTier.Secondary, false, "Session"),
                Out("var", ParamValueType.VariableName, "可選：接收彈出值之變數名稱（預設同名 $(key)）", "out", "dest")
            },
            ImplicitEffects = { "pops RedisList[$(key)] -> var (default $(key))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Redis@BLPop",
            Summary = "阻塞等待自 Redis 佇列彈出資料，逾時可跳轉錯誤標籤",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("key", ParamValueType.String, 0, "Redis 佇列鍵名稱", "queue"),
                Opt("session", ParamValueType.String, "", null, "目標 Redis Session（留空使用預設 Session）", ParamDisplayTier.Secondary, false, "Session"),
                Opt("timeout", ParamValueType.Number, "5", null, "等待逾時秒數（預設 5s）", ParamDisplayTier.Critical, false, "TimeOut"),
                Out("var", ParamValueType.VariableName, "可選：接收彈出值之變數名稱（預設同名 $(key)）", "out", "dest")
            },
            ImplicitEffects = { "blocks & pops RedisList[$(key)] -> var (default $(key))" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Redis@Publish",
            Summary = "向指定 Redis Pub/Sub 頻道鍵發佈訊息",
            Parameters =
            {
                Req("key", ParamValueType.String, 0, "Pub/Sub 頻道鍵名稱"),
                Req("val", ParamValueType.String, 1, "發佈內容", "value"),
                Opt("session", ParamValueType.String, "", null, "目標 Redis Session（留空使用預設 Session）", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "publishes to $(key)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Redis@Subscribe",
            Summary = "訂閱指定 Redis Pub/Sub 頻道鍵",
            Parameters =
            {
                Req("key", ParamValueType.String, 0, "Pub/Sub 頻道鍵名稱"),
                Opt("session", ParamValueType.String, "", null, "目標 Redis Session（留空使用預設 Session）", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "subscribes $(key)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Redis@CMD",
            Summary = "向 Redis Session 發送原生指令字串（未指定 session 則使用預設 Session）",
            Parameters =
            {
                Req("cmd", ParamValueType.String, 0, "原生 Redis 指令（如 PING）", "command"),
                Opt("session", ParamValueType.String, "", null, "目標 Redis Session（留空使用預設 Session）", ParamDisplayTier.Secondary, false, "Session")
            },
            ImplicitEffects = { "runs Redis $(cmd)" }
        });

        // =====================================================================
        // 12. MULTISTATION 模組 (12 條指令) — station (工站 或 工站.工位) / items / slots / logic / var / index
        // =====================================================================

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@AddStation",
            Summary = "建立多工站群組：支援命名工位清單 items 或 1-based 工位數量 slots",
            Parameters =
            {
                Req("station", ParamValueType.String, 0, "工站群組名稱（如 FeedInStation、TrayA）", "name"),
                Opt("items", ParamValueType.PortList, "", null, "命名工位有序清單（如 [入料;加工;檢驗;出料]）", ParamDisplayTier.Primary, false, "slotsList"),
                Opt("slots", ParamValueType.Integer, "0", 1, "1-based 工位數量（如 20）", ParamDisplayTier.Primary, false, "count")
            },
            ImplicitEffects = { "registers MultiStation $(station) & initializes IO/Variable bindings" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@SetOn",
            Summary = "以 $logic 邏輯設定指定工位有料/無料（True=#On?=true；False=#On?=false 且預設清空 OK/NG 與隨行資料）",
            Parameters =
            {
                Req("station", ParamValueType.String, 0, "目標工位（格式：工站.工位，如 FeedInStation.入料 或 TrayA.3；相容舊版整數）", "st", "stNo"),
                Req("logic", ParamValueType.LogicExpr, 1, "有料/無料狀態或 $logic 運算式（如 True、False、InSensor?）", "on", "val"),
                Opt("clearData", ParamValueType.Boolean, "true", 2, "當 logic 為 False（無料）時是否一併清空 OK/NG 與隨行資料（預設 true）", ParamDisplayTier.Secondary)
            },
            ImplicitEffects = { "sets $(station)#On?=eval($(logic)) (clears OK/NG+Data on false)" }
        }, "MultiStation@SetStationON", "MultiStation@SetStationOFF");

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@SetOK",
            Summary = "以 $logic 邏輯設定指定工位品質判定（True=#OK?=true/#NG?=false；False=#NG?=true/#OK?=false）",
            Parameters =
            {
                Req("station", ParamValueType.String, 0, "目標工位（格式：工站.工位，如 FeedInStation.檢驗 或 TrayA.3；相容舊版整數）", "st", "stNo"),
                Req("logic", ParamValueType.LogicExpr, 1, "品質判定結果或 $logic 運算式（如 True、False、Cam0#OK? & PressCurve#OK?）", "ok", "val")
            },
            ImplicitEffects = { "sets $(station)#OK?=eval($(logic)), #NG?=!eval($(logic))" }
        }, "MultiStation@SetStationOK", "MultiStation@SetStationNG");

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@SetData",
            Summary = "寫入指定工位之隨行資料至 VariableEngine（$(station)#$(field)，隨 StateShift 同步搬移）",
            Parameters =
            {
                Req("station", ParamValueType.String, 0, "目標工位（格式：工站.工位，如 FeedInStation.入料）", "st"),
                Req("val", ParamValueType.String, 1, "寫入資料內容（如條碼 SN、量測值）", "value"),
                Opt("field", ParamValueType.String, "Data", 2, "工位資料欄位名稱（預設 Data，如 SN、ErrCode）", ParamDisplayTier.Primary, false, "key")
            },
            ImplicitEffects = { "sets var $(station)#$(field)=$(val)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@GetData",
            Summary = "讀取指定工位之隨行資料並輸出至指定變數",
            Parameters =
            {
                Req("station", ParamValueType.String, 0, "來源工位（格式：工站.工位，如 FeedInStation.加工）", "st"),
                Opt("field", ParamValueType.String, "Data", 1, "工位資料欄位名稱（預設 Data）", ParamDisplayTier.Primary, false, "key"),
                OutPos("var", ParamValueType.VariableName, 2, "接收數值之目標變數名稱（裸名稱）", false, "dest")
            },
            ImplicitEffects = { "reads $(station)#$(field) -> var" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@ChkStationRunState",
            Summary = "檢查指定工位忙碌狀態並更新 $(station)#Busy?",
            Parameters = { Req("station", ParamValueType.String, 0, "目標工位（格式：工站.工位，相容舊版整數）", "st", "stNo") },
            ImplicitEffects = { "updates $(station)#Busy?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@StateShift",
            Summary = "以環形緩衝區向前（+）或向後（-）位移指定格數，同步轉移有料/OK/NG 與全部隨行資料（直線輸送帶請另對頭/尾呼叫 SetStationOFF）",
            Parameters =
            {
                Opt("station", ParamValueType.String, "", 0, "目標工站群組名稱（留空對全部工站轉移）", ParamDisplayTier.Primary, false, "st"),
                Opt("shift", ParamValueType.String, "1", 1, "環形位移格數或方向（預設 1；正數向前、負數向後，亦相容 CW/CCW/Left/Right）", ParamDisplayTier.Primary, false, "dir", "step")
            },
            ImplicitEffects = { "shifts $(station) ring-buffer IO states & Variable data by $(shift)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@Transfer",
            Summary = "跨工站/跨料盤整包搬移交接（ON + OK/NG + 全部隨行 Variable 資料），並預設清空來源工位",
            Parameters =
            {
                Req("from", ParamValueType.String, 0, "來源工位（格式：工站.工位，如 FeedInStation.出料）", "src"),
                Req("to", ParamValueType.String, 1, "目標工位（格式：工站.工位，如 OutTray.3）", "dst"),
                Opt("clearSource", ParamValueType.Boolean, "true", 2, "搬移後是否清空來源工位（預設 true）", ParamDisplayTier.Secondary)
            },
            ImplicitEffects = { "moves state & data $(from) -> $(to)" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@FindFirstOK",
            Summary = "尋找入料/目標料盤第一個可取料 OK 工位，更新 $(station)#Found? 並輸出工位鍵與 0-based 索引（供 Point&Path@ResetPalletIndex 使用）",
            Parameters =
            {
                Req("station", ParamValueType.String, 0, "目標工站/料盤名稱（如 InTray）", "st"),
                Opt("allowUnchecked", ParamValueType.Boolean, "false", 1, "是否允許尚未標記 OK 之有料位（IsOn && !NG，預設 false）", ParamDisplayTier.Secondary),
                OutPos("var", ParamValueType.VariableName, 2, "可選：輸出找到之完整工位鍵（如 InTray.3）", false, "slot", "dest"),
                OutPos("index", ParamValueType.VariableName, 3, "可選：輸出找到之 0-based 工位索引變數（供 Point&Path@ResetPalletIndex 使用；未找到為 -1）", false, "idx")
            },
            ImplicitEffects = { "sets $(station)#Found?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@FindEmpty",
            Summary = "尋找出料料盤第一個空位（!On），更新 $(station)#Found? 並輸出空位鍵與 0-based 索引（供 Point&Path@ResetPalletIndex 使用）",
            Parameters =
            {
                Req("station", ParamValueType.String, 0, "目標工站/料盤名稱（如 OutTray）", "st"),
                OutPos("var", ParamValueType.VariableName, 1, "可選：輸出找到之完整空位鍵（如 OutTray.5）", false, "slot", "dest"),
                OutPos("index", ParamValueType.VariableName, 2, "可選：輸出找到之 0-based 空位索引變數（供 Point&Path@ResetPalletIndex 使用；未找到為 -1）", false, "idx")
            },
            ImplicitEffects = { "sets $(station)#Found?" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@ResetDefault",
            Summary = "重置指定工站（留空為全部工站）之有料、OK/NG、Busy 與隨行 Variable 資料",
            Parameters =
            {
                Opt("station", ParamValueType.String, "", 0, "目標工站群組名稱（留空重置全部）", ParamDisplayTier.Primary, false, "st")
            },
            ImplicitEffects = { "resets $(station) IO flags & slot data" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "MultiStation@UpdateIndicator",
            Summary = "將指定工站（留空為全部工站）內部狀態同步刷新至虛擬 IO 指示旗標",
            Parameters =
            {
                Opt("station", ParamValueType.String, "", 0, "目標工站群組名稱（留空刷新全部）", ParamDisplayTier.Primary, false, "st")
            },
            ImplicitEffects = { "syncs $(station) indicator IOs" }
        });

        // =====================================================================
        // 13. AIAGENT 模組 (3 條指令) — prompt / bypass*
        // =====================================================================

        Reg(new CommandSchema
        {
            CommandKey = "AIAgent@SuperviseStart",
            Summary = "開啟一段 AI 監督區間並記錄診斷 Prompt（bypass=true 可略過 AI 呼叫）",
            Parameters =
            {
                Req("prompt", ParamValueType.String, 0, "監督任務描述或檢驗目標 Prompt"),
                Opt("bypass", ParamValueType.Boolean, "false", null, "是否略過實際 AI 請求直接放行（對應舊版 ~ 前綴）", ParamDisplayTier.Critical)
            },
            ImplicitEffects = { "marks AIAgent_Supervising=true" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "AIAgent@SuperviseEnd",
            Summary = "結束 AI 監督區間並送交監督判斷，異常或離線可跳轉 [[FallbackLabel]]",
            SupportsErrorTrap = true,
            Parameters = { },
            ImplicitEffects = { "marks AIAgent_Supervising=false & invokes AI supervise" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "AIAgent@ErrorHandle",
            Summary = "呼叫 AI 進行異常診斷與處置，失敗或 bypass=true 時跳轉 [[FallbackLabel]]",
            SupportsErrorTrap = true,
            Parameters =
            {
                Req("prompt", ParamValueType.String, 0, "異常情境描述或自癒指令 Prompt"),
                Opt("bypass", ParamValueType.Boolean, "false", null, "是否略過 AI 直接跳轉 Fallback 標籤（對應舊版 ~ 前綴）", ParamDisplayTier.Critical)
            },
            ImplicitEffects = { "invokes AI error_handle" }
        });

        // =====================================================================
        // 14. HELLO 模組 (2 條指令) — 示範模組
        // =====================================================================

        Reg(new CommandSchema
        {
            CommandKey = "Hello@Init",
            Summary = "初始化 Hello 示範模組並設定 Hello_Status@Hello=INITIALIZED",
            Parameters = { },
            ImplicitEffects = { "sets Hello_Status@Hello=INITIALIZED" }
        });

        Reg(new CommandSchema
        {
            CommandKey = "Hello@HelloWorld",
            Summary = "輸出問候訊息至執行日誌與 SYSTEMLOG",
            Parameters =
            {
                Opt("msg", ParamValueType.String, "hello world", 0, "輸出問候文字（留空預設 hello world）", ParamDisplayTier.Primary, false, "text")
            },
            ImplicitEffects = { "logs $(msg)" }
        });

        return dict;
    }
}
