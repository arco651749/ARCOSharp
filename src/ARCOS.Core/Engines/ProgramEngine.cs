using System.Collections.Concurrent;
using ARCOS.Core.Execution;

namespace ARCOS.Core.Engines;

/// <summary>
/// 程式區與標籤導引引擎 (ProgramEngine)
/// 將載入中或編輯中的全域 ProgramTable 區段 (0..99) 與目前焦點程式表暴露給所有 CSVI 參數導引 UI，
/// 讓個別指令的 Guide UI 可自行過濾 Thread@ErrorHandler、Label、ProgramSection 等程式區資料。
/// </summary>
public class ProgramEngine
{
    private readonly ConcurrentDictionary<int, ProgramTable> _programs = new();

    /// <summary>
    /// 使用者目前正在編輯或觸發導引的程式區段編號 (例如 0 = Main, 1..12 = Prog 1..12)
    /// </summary>
    public int ActiveProgramNumber { get; set; } = 0;

    /// <summary>
    /// 使用者觸發導引的 0-based 列索引 (若未指定為 -1)
    /// </summary>
    public int ActiveRowIndex { get; set; } = -1;

    public void SetProgram(int progNo, ProgramTable program)
    {
        if (program == null) return;
        _programs[progNo] = program;
    }

    public void BindPrograms(IEnumerable<ProgramTable> programs)
    {
        if (programs == null) return;
        foreach (var p in programs)
        {
            if (p != null)
            {
                _programs[p.ProgramNumber] = p;
            }
        }
    }

    public ProgramTable? GetProgram(int progNo)
    {
        return _programs.TryGetValue(progNo, out var table) ? table : null;
    }

    public ProgramTable? GetActiveProgram()
    {
        if (_programs.TryGetValue(ActiveProgramNumber, out var active))
        {
            return active;
        }
        return _programs.Values.OrderBy(p => p.ProgramNumber).FirstOrDefault();
    }

    public IReadOnlyList<ProgramTable> GetAllPrograms()
    {
        return _programs.Values.OrderBy(p => p.ProgramNumber).ToList();
    }
}
