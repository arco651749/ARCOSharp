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
    public event Action<string>? OnUserPanelOpenRequested;

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
            _programs[i] = new ProgramTable { ProgramNumber = i, Name = $"Program_{i}" };
        }

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
    }

    public ProgramTable GetProgram(int progNo)
    {
        if (progNo < 0 || progNo >= 100)
            throw new ArgumentOutOfRangeException(nameof(progNo), "Program number must be 0..99");
        return _programs[progNo];
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

            if (AutoSpawnBackgroundLoop)
            {
                ctx.StartBackgroundLoop(_executor);
            }
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
    /// 啟動動態平行執行緒 (SubThread，本質與一般 Thread 完全相同，支援帶 & 特權後綴)
    /// </summary>
    public ExecutionContext StartSubThread(string subName, ProgramTable program, int startIndex = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subName);
        ArgumentNullException.ThrowIfNull(program);

        string cleanName = subName.Trim();
        if (_subThreads.TryGetValue(cleanName, out var oldCtx))
        {
            oldCtx.StopBackgroundLoop(resetRunningFlag: true);
        }

        var ctx = new ExecutionContext(100 + _subThreads.Count, cleanName, program, _engines, _drivers)
        {
            ProgIndex = startIndex,
            IsRunning = true,
            CurrentSection = cleanName,
            VM = this
        };

        _subThreads[cleanName] = ctx;
        _engines.Io.AddVirtualIO($"{cleanName}#Run?", true);
        _engines.Io.AddVirtualIO($"{cleanName}#Pause?", false);

        if (AutoSpawnBackgroundLoop)
        {
            ctx.StartBackgroundLoop(_executor);
        }

        return ctx;
    }

    /// <summary>
    /// 掛起/終止指定 SubThread
    /// </summary>
    public void SuspendSubThread(string subName)
    {
        if (string.IsNullOrWhiteSpace(subName)) return;
        if (_subThreads.TryGetValue(subName.Trim(), out var ctx))
        {
            ctx.StopBackgroundLoop(resetRunningFlag: true);
        }
    }

    /// <summary>
    /// 觸發系統硬體事件鉤子程式區段 (Program 94~99)
    /// 以獨立非同步 SubThread 啟動，避免阻塞主流程
    /// </summary>
    public void TriggerSystemHook(int hookProgNo)
    {
        if (hookProgNo < 90 || hookProgNo > 99) return;
        var prog = GetProgram(hookProgNo);
        string hookName = $"SystemHook_{hookProgNo}&"; // 帶 & 特權確保暫停時亦可執行
        StartSubThread(hookName, prog, 1);
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
