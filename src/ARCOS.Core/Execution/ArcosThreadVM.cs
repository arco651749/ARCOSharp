using System.Collections.Concurrent;
using ARCOS.Core.Drivers;

namespace ARCOS.Core.Execution;

/// <summary>
/// ARCOS 多執行緒虛擬機 (Thread Virtual Machine)
/// 統一驅動 Thread Main (0) + 12 個 Worker 執行緒與動態 SubThread，
/// 實作 Program 0~99 程式段分派、`&` 特權繞過 Pause 閘門與虛擬 IO 生命週期管理。
/// </summary>
public sealed class ArcosThreadVM
{
    private readonly ProgramTable[] _programs = new ProgramTable[100];
    private readonly ExecutionContext[] _mainThreads = new ExecutionContext[13];
    private readonly ConcurrentDictionary<string, ExecutionContext> _subThreads = new(StringComparer.OrdinalIgnoreCase);
    private int _concurrentSubCounter;

    /// <summary>
    /// 跨執行緒共用之互斥資源鎖表 (供 KeySectionLock.csvi 與 KeySectionUnLock.csvi 共享)
    /// </summary>
    public static ConcurrentDictionary<string, string> ActiveKeyLocks { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly CommandExecutor _executor;
    private readonly BasicEngines _engines;
    private readonly DriverManager _drivers;
    private readonly object _lock = new();

    /// <summary>
    /// 是否在 StartThread / StartSubThread 時自動啟動該 Thread 專屬的平行背景高速迴圈
    /// (App Runtime 啟用為 true；純手動單步測試可保持 false 由 Tick() 推進)
    /// </summary>
    public bool AutoSpawnBackgroundLoop { get; set; } = false;

    public CommandExecutor Executor => _executor;
    public BasicEngines Engines => _engines;
    public DriverManager Drivers => _drivers;
    public IReadOnlyList<ExecutionContext> MainThreads => _mainThreads;
    public IReadOnlyDictionary<string, ExecutionContext> SubThreads => _subThreads;

    public event Action<string, bool>? OnProgramLoaded;
    public event Action<int>? OnProgramChanged;
    public event Action<string>? OnUserPanelOpenRequested;

    /// <summary>
    /// 系統彈窗通知委派 (System@MessageBox::$message)
    /// 參數：(threadName, message) => Task (等待操作員按下確定)
    /// </summary>
    public Func<string, string, Task>? OnMessageBoxRequested { get; set; }

    /// <summary>
    /// 系統警告確認框委派 (System@WarningBox::$logic, $message)
    /// 參數：(threadName, message) => Task&lt;bool&gt; (回傳 true 代表繼續執行，false 代表停止該執行緒)
    /// </summary>
    public Func<string, string, Task<bool>>? OnWarningBoxRequested { get; set; }

    public void NotifyProgramLoaded(string filePath, bool isPro)
    {
        OnProgramLoaded?.Invoke(filePath, isPro);
    }

    public void NotifyUserPanelOpen(string csviPath)
    {
        OnUserPanelOpenRequested?.Invoke(csviPath);
    }

    public ArcosThreadVM(BasicEngines engines, DriverManager drivers, CommandExecutor? executor = null)
    {
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _drivers = drivers ?? throw new ArgumentNullException(nameof(drivers));
        _executor = executor ?? new CommandExecutor(ModuleRegistry.CreateDefault());

        // 初始化 100 個程式區段插槽 (Program 0 ~ 99)
        for (int i = 0; i < 100; i++)
        {
            _programs[i] = new ProgramTable { ProgramNumber = i, Name = GetDefaultProgramSlotName(i) };
        }

        // 寫入系統事件程式區 (94~99) 的出廠預設指令
        InitializeDefaultSystemPrograms();

        // 初始化 13 個主執行緒 (Thread 0 ~ 12)
        for (int i = 0; i < 13; i++)
        {
            string tName = i == 0 ? "ThreadMain" : $"Thread{i}";
            _mainThreads[i] = new ExecutionContext(i, tName, _programs[i], _engines, _drivers)
            {
                IsRunning = false,
                VM = this
            };
            _engines.Io.AddVirtualIO($"{tName}#Run?", false);
            _engines.Io.AddVirtualIO($"{tName}#Pause?", false);
        }
    }

    public static string GetDefaultProgramSlotName(int slotNumber) => slotNumber switch
    {
        0 => "Main",
        94 => "Pause Off",
        95 => "Pause On",
        96 => "Execute On",
        97 => "Execute Off",
        98 => "End Script",
        99 => "EMG Stop",
        _ => $"Prog {slotNumber}"
    };

    /// <summary>
    /// 初始化系統事件程式區 (94~99) 預設指令：
    /// 94 (Pause Off): System@Resume
    /// 95 (Pause On):  System@Pause
    /// 96 (Execute On): Thread@Run::0,0
    /// 99 (EMG Stop):  Motion@Stop
    /// </summary>
    public void InitializeDefaultSystemPrograms()
    {
        if (_programs[94].Count == 0)
        {
            _programs[94].Name = "Pause Off";
            _programs[94].Add(CommandItem.Parse("System@Resume\t", 0));
        }

        if (_programs[95].Count == 0)
        {
            _programs[95].Name = "Pause On";
            _programs[95].Add(CommandItem.Parse("System@Pause\t", 0));
        }

        if (_programs[96].Count == 0)
        {
            _programs[96].Name = "Execute On";
            _programs[96].Add(CommandItem.Parse("Thread@Run\t0,0", 0));
        }

        if (string.IsNullOrWhiteSpace(_programs[97].Name))
            _programs[97].Name = "Execute Off";

        if (string.IsNullOrWhiteSpace(_programs[98].Name))
            _programs[98].Name = "End Script";

        if (_programs[99].Count == 0)
        {
            _programs[99].Name = "EMG Stop";
            _programs[99].Add(CommandItem.Parse("Motion@Stop\t", 0));
        }
    }

    /// <summary>
    /// 依編號 ("0".."12") 或名稱 ("ThreadMain", "Thread1", SubThread 名稱) 尋找平行執行單元
    /// </summary>
    public ExecutionContext? FindThread(string nameOrIndex)
    {
        if (string.IsNullOrWhiteSpace(nameOrIndex)) return null;
        string key = nameOrIndex.Trim();

        if (int.TryParse(key, out int idx) && idx >= 0 && idx < _mainThreads.Length)
        {
            return _mainThreads[idx];
        }

        if (key.Equals("Main", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ThreadMain", StringComparison.OrdinalIgnoreCase))
        {
            return _mainThreads[0];
        }

        for (int i = 0; i < _mainThreads.Length; i++)
        {
            if (_mainThreads[i].ThreadName.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return _mainThreads[i];
            }
        }

        if (_subThreads.TryGetValue(key, out var subCtx))
        {
            return subCtx;
        }

        return null;
    }

    /// <summary>
    /// 設定單一或多個指定 Thread 的 Local Pause 狀態 (System@Pause::$Thread / System@Resume::$Thread)
    /// </summary>
    public int SetThreadLocalPause(IEnumerable<string> targets, bool isPaused)
    {
        int count = 0;
        foreach (var rawTarget in targets)
        {
            if (string.IsNullOrWhiteSpace(rawTarget)) continue;
            string key = rawTarget.Trim();

            var ctx = FindThread(key);
            if (ctx != null)
            {
                ctx.IsLocalPaused = isPaused;
                count++;
                continue;
            }

            // 亦支援前綴匹配併發 SubThread (如 process&&_1, process&&_2)
            foreach (var kvp in _subThreads)
            {
                if (kvp.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    kvp.Value.IsLocalPaused = isPaused;
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// 設定全機 Global Pause 狀態，同步通知所有平行單元結算暫停時鐘，並觸發 Prog 95 (Pause On) / Prog 94 (Pause Off)
    /// </summary>
    public void SetGlobalPause(bool isPaused, bool triggerHook = true)
    {
        bool prev = _engines.Timer.IsSystemPaused;
        _engines.Timer.SetSystemPause(isPaused);

        if (prev != isPaused)
        {
            for (int i = 0; i < _mainThreads.Length; i++)
            {
                _mainThreads[i].OnGlobalPauseChanged(isPaused);
            }
            foreach (var kvp in _subThreads)
            {
                kvp.Value.OnGlobalPauseChanged(isPaused);
            }

            if (triggerHook)
            {
                int hookProg = isPaused ? 95 : 94;
                var prog = GetProgram(hookProg);
                if (prog.Count > 0)
                {
                    TriggerSystemHook(hookProg);
                }
            }
        }
    }

    /// <summary>
    /// 載入特定程式區段 (0~99)
    /// </summary>
    public void LoadProgram(int progNo, ProgramTable program)
    {
        if (progNo < 0 || progNo >= 100)
            throw new ArgumentOutOfRangeException(nameof(progNo), "Program number must be 0..99");

        _programs[progNo] = program ?? throw new ArgumentNullException(nameof(program));
        _programs[progNo].ProgramNumber = progNo;

        // 同步更新預設綁定或當前正在指向此 progNo 的主執行緒 Program 參照
        for (int i = 0; i < _mainThreads.Length; i++)
        {
            if (_mainThreads[i] != null && (_mainThreads[i].Program == null || _mainThreads[i].Program.ProgramNumber == progNo || (!_mainThreads[i].IsRunning && i == progNo)))
            {
                _mainThreads[i].Program = _programs[progNo];
            }
        }

        OnProgramChanged?.Invoke(progNo);
    }

    public ProgramTable GetProgram(int progNo)
    {
        if (progNo < 0 || progNo >= 100)
            throw new ArgumentOutOfRangeException(nameof(progNo), "Program number must be 0..99");
        return _programs[progNo];
    }

    /// <summary>
    /// 依程式區段編號 ("0"~"99", "Prog 1") 或區段名稱尋找對應的 ProgramTable
    /// </summary>
    public ProgramTable? FindProgram(string? progNumberOrName)
    {
        if (string.IsNullOrWhiteSpace(progNumberOrName)) return null;
        string key = progNumberOrName.Trim();

        if (int.TryParse(key, out int progNo) && progNo >= 0 && progNo < 100)
        {
            return _programs[progNo];
        }

        if (key.StartsWith("Prog ", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key.Substring(5).Trim(), out int pNum) && pNum >= 0 && pNum < 100)
        {
            return _programs[pNum];
        }

        for (int i = 0; i < _programs.Length; i++)
        {
            if (_programs[i] != null && string.Equals(_programs[i].Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return _programs[i];
            }
        }

        return null;
    }

    /// <summary>
    /// 指派並啟動主執行緒 (Thread 0~12)
    /// </summary>
    public void StartThread(int threadNo, int progNo, int startIndex = 1)
    {
        if (threadNo < 0 || threadNo >= 13)
            throw new ArgumentOutOfRangeException(nameof(threadNo), "Thread number must be 0..12");

        var ctx = _mainThreads[threadNo];
        lock (_lock)
        {
            ctx.StopBackgroundLoop(resetRunningFlag: false);
            ctx.Program = GetProgram(progNo);
            ctx.ProgIndex = startIndex;
            ctx.CallStack.Clear();
            ctx.State.Reset();
            ctx.IsLocalPaused = false;
            ctx.IsRunning = true;
            _engines.Io.SetPort($"{ctx.ThreadName}#Run?", true);
        }

        OnProgramChanged?.Invoke(progNo);

        if (AutoSpawnBackgroundLoop)
        {
            ctx.StartBackgroundLoop(_executor);
        }
    }

    /// <summary>
    /// 掛起/停止主執行緒
    /// </summary>
    public void SuspendThread(int threadNo)
    {
        if (threadNo < 0 || threadNo >= 13) return;
        var ctx = _mainThreads[threadNo];
        lock (_lock)
        {
            ctx.StopBackgroundLoop(resetRunningFlag: true);
        }
    }

    /// <summary>
    /// 恢復被掛起的主執行緒
    /// </summary>
    public void ResumeThread(int threadNo)
    {
        if (threadNo < 0 || threadNo >= 13) return;
        var ctx = _mainThreads[threadNo];
        lock (_lock)
        {
            ctx.IsLocalPaused = false;
            ctx.IsRunning = true;
            _engines.Io.SetPort($"{ctx.ThreadName}#Run?", true);
            if (AutoSpawnBackgroundLoop)
            {
                ctx.StartBackgroundLoop(_executor);
            }
        }
    }

    /// <summary>
    /// 啟動動態平行執行緒 (SubThread，支援單行程、& 特權免暫停後綴、以及 && 併發多實例後綴)
    /// </summary>
    public ExecutionContext StartSubThread(string subName, ProgramTable program, int startIndex = 1, string? parentSection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subName);
        ArgumentNullException.ThrowIfNull(program);

        string cleanName = subName.Trim();
        bool isConcurrentMulti = cleanName.EndsWith("&&", StringComparison.Ordinal);

        string instanceKey = cleanName;
        if (isConcurrentMulti)
        {
            int seq = Interlocked.Increment(ref _concurrentSubCounter);
            // 保留尾端 '&' 使 IsBypassPause 生效，或以 cleanName + "_" + seq 命名
            instanceKey = $"{cleanName}_{seq}";
        }
        else if (_subThreads.TryGetValue(cleanName, out var oldCtx))
        {
            oldCtx.StopBackgroundLoop(resetRunningFlag: true);
        }

        string sectionToUse = isConcurrentMulti ? instanceKey : (string.IsNullOrWhiteSpace(parentSection) ? cleanName : parentSection);
        if (isConcurrentMulti)
        {
            _engines.Variable.SnapshotCurrentValuesToSection(parentSection ?? "Global", sectionToUse);
        }

        var ctx = new ExecutionContext(100 + _subThreads.Count, instanceKey, program, _engines, _drivers)
        {
            ProgIndex = startIndex,
            IsRunning = true,
            CurrentSection = sectionToUse,
            VM = this
        };

        _subThreads[instanceKey] = ctx;
        _engines.Io.AddVirtualIO($"{instanceKey}#Run?", true);
        _engines.Io.AddVirtualIO($"{instanceKey}#Pause?", false);
        if (isConcurrentMulti)
        {
            _engines.Io.AddVirtualIO($"{cleanName}#Run?", true);
        }

        if (AutoSpawnBackgroundLoop)
        {
            ctx.StartBackgroundLoop(_executor);
        }

        return ctx;
    }

    /// <summary>
    /// 檢查指定的 SubThread（或 && 併發群組）是否曾經被宣告/啟動過
    /// </summary>
    public bool HasSubThread(string subName)
    {
        if (string.IsNullOrWhiteSpace(subName)) return false;
        string clean = subName.Trim();
        if (_subThreads.ContainsKey(clean)) return true;

        string prefix = clean + "_";
        foreach (var key in _subThreads.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 檢查指定的 SubThread（或 && 併發群組的任何一個實例）是否仍在運行中
    /// </summary>
    public bool IsSubThreadRunning(string subName)
    {
        if (string.IsNullOrWhiteSpace(subName)) return false;
        string clean = subName.Trim();
        if (_subThreads.TryGetValue(clean, out var directCtx) && directCtx.IsRunning)
        {
            return true;
        }
        if (_engines.Io.GetPort($"{clean}#Run?") && !clean.EndsWith("&&", StringComparison.Ordinal))
        {
            return true;
        }

        string prefix = clean + "_";
        bool anyRunning = false;
        foreach (var kvp in _subThreads)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && kvp.Value.IsRunning)
            {
                anyRunning = true;
                break;
            }
        }

        if (clean.EndsWith("&&", StringComparison.Ordinal))
        {
            _engines.Io.SetPort($"{clean}#Run?", anyRunning);
        }
        return anyRunning;
    }

    /// <summary>
    /// 掛起/終止指定 SubThread（若為 && 併發群組則終止所有對應實例）
    /// </summary>
    public void SuspendSubThread(string subName)
    {
        if (string.IsNullOrWhiteSpace(subName)) return;
        string clean = subName.Trim();
        if (_subThreads.TryGetValue(clean, out var ctx))
        {
            ctx.StopBackgroundLoop(resetRunningFlag: true);
        }

        string prefix = clean + "_";
        foreach (var kvp in _subThreads)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                kvp.Value.StopBackgroundLoop(resetRunningFlag: true);
            }
        }
        _engines.Io.SetPort($"{clean}#Run?", false);
    }

    /// <summary>
    /// 觸發系統硬體事件鉤子程式區段 (Program 94~99)
    /// 以獨立非同步平行執行緒啟動 (名稱結尾必帶 & 特權，確保暫停時亦可執行)
    /// </summary>
    public ExecutionContext? TriggerSystemHook(int hookProgNo)
    {
        if (hookProgNo < 90 || hookProgNo > 99) return null;
        var prog = GetProgram(hookProgNo);
        string hookName = hookProgNo switch
        {
            94 => "PauseOff&",
            95 => "PauseOn&",
            96 => "ExecuteOn&",
            97 => "ExecuteOff&",
            98 => "EndScript&",
            99 => "EMGStop&",
            _ => $"SystemHook_{hookProgNo}&"
        };
        return StartSubThread(hookName, prog, 1);
    }

    /// <summary>
    /// 單步排程輪詢 (Single Scheduling Tick)
    /// 遍歷所有活動執行緒執行單步指令，落實 LocalPause、SystemPause 與 & 特權閘門控制
    /// </summary>
    public void Tick()
    {
        // 1. 執行 13 個主執行緒
        for (int i = 0; i < 13; i++)
        {
            var ctx = _mainThreads[i];
            if (!ctx.IsRunning) continue;

            // Pause 閘門控制：若該 Thread 處於 Local Pause 或全機暫停 (且不具 & 特權)，跳過此輪
            if (ctx.IsEffectivelyPaused)
            {
                continue;
            }

            _executor.ExecuteStep(ctx);

            if (!ctx.IsRunning)
            {
                _engines.Io.SetPort($"{ctx.ThreadName}#Run?", false);
            }
        }

        // 2. 執行動態 SubThreads
        foreach (var kvp in _subThreads)
        {
            var ctx = kvp.Value;
            if (!ctx.IsRunning) continue;

            if (ctx.IsEffectivelyPaused)
            {
                continue;
            }

            _executor.ExecuteStep(ctx);

            if (!ctx.IsRunning)
            {
                _engines.Io.SetPort($"{ctx.ThreadName}#Run?", false);
            }
        }
    }

    /// <summary>
    /// 停止全機所有執行緒並歸零狀態
    /// </summary>
    public void StopAll()
    {
        for (int i = 0; i < 13; i++)
        {
            SuspendThread(i);
        }
        foreach (var key in _subThreads.Keys)
        {
            SuspendSubThread(key);
        }
        ActiveKeyLocks.Clear();
    }

    /// <summary>
    /// 背景常駐輪詢迴圈 (非同步背景任務)
    /// </summary>
    public async Task RunLoopAsync(CancellationToken ct, int loopIntervalMs = 5)
    {
        while (!ct.IsCancellationRequested)
        {
            Tick();
            if (loopIntervalMs > 0)
            {
                await Task.Delay(loopIntervalMs, ct).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }
}
