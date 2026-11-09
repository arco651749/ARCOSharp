using System;
using System.Collections.Generic;
using ARCOS.Core.Execution;

namespace ARCOS.App.ViewModels;

/// <summary>
/// .PRO 專案腳本程式區 (Program Slot / Program Section) 下拉項目 ViewModel
/// 格式：[編號]: [名稱] (例如 0: Main, 1: Station_Feeder, 96: Execute On)
/// </summary>
public sealed class ProgramSlotItem
{
    public int SlotNumber { get; set; }
    public string Name { get; set; } = "";

    public static string GetDefaultName(int slot) => slot switch
    {
        0 => "Main",
        94 => "Pause Off",
        95 => "Pause On",
        96 => "Execute On",
        97 => "Execute Off",
        98 => "End Script",
        99 => "EMG Stop",
        _ => $"Prog {slot}"
    };

    public string DisplayText
    {
        get
        {
            string n = string.IsNullOrWhiteSpace(Name) ? GetDefaultName(SlotNumber) : Name;
            return $"{SlotNumber}: {n}";
        }
    }

    public override string ToString() => DisplayText;

    /// <summary>
    /// 建構標準下拉程式區清單 (包含 0~12 使用者程式區、任何已載入的中繼程式區以及 94~99 系統事件區)
    /// </summary>
    public static List<ProgramSlotItem> BuildStandardSlotList(ArcosThreadVM? vm = null)
    {
        var list = new List<ProgramSlotItem>();

        // 1. 常規程式區 0 ~ 12
        for (int i = 0; i <= 12; i++)
        {
            string name = "";
            if (vm != null)
            {
                var p = vm.GetProgram(i);
                if (!string.IsNullOrWhiteSpace(p.Name) && !p.Name.Equals($"Program_{i}", StringComparison.OrdinalIgnoreCase))
                {
                    name = p.Name;
                }
            }
            list.Add(new ProgramSlotItem { SlotNumber = i, Name = name });
        }

        // 2. 若專案中包含 13 ~ 93 的已定義/有指令程式區，動態納入
        if (vm != null)
        {
            for (int i = 13; i < 94; i++)
            {
                var p = vm.GetProgram(i);
                if (p.Count > 0 || (!string.IsNullOrWhiteSpace(p.Name) && !p.Name.Equals($"Program_{i}", StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(new ProgramSlotItem { SlotNumber = i, Name = p.Name });
                }
            }
        }

        // 3. 系統保留事件區 94 ~ 99
        for (int i = 94; i <= 99; i++)
        {
            string name = "";
            if (vm != null)
            {
                var p = vm.GetProgram(i);
                if (!string.IsNullOrWhiteSpace(p.Name) && !p.Name.Equals($"Program_{i}", StringComparison.OrdinalIgnoreCase))
                {
                    name = p.Name;
                }
            }
            list.Add(new ProgramSlotItem { SlotNumber = i, Name = name });
        }

        return list;
    }
}
