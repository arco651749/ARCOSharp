using System.Collections.Concurrent;
using MVCON.Core.Drivers;

namespace MVCON.Core.Execution;

/// <summary>
/// MVCON 多執行緒虛擬機 (Thread Virtual Machine)
/// 統一驅動 Thread Main (0) + 12 個 Worker 執行緒與動態 SubThread，
/// 實作 Program 0~99 程式段分派、`&` 特權繞過 Pause 閘門與虛擬 IO 生命週期管理。
/// </summary>
public sealed class MvconThreadVM
{
    private readonly ProgramTable[] _programs = new ProgramTable[100];
    private readonly ExecutionContext[] _mainThreads = new ExecutionContext[13];
    private readonly ConcurrentDictionary<string, ExecutionContext> _subThreads = new(StringComparer.OrdinalIgnoreCase);
    private readonly CommandExecutor _executor;
    private readonly BasicEngines _engines;
    private readonly DriverManager _drivers;
    private readonly object _lock = new();

    public CommandExecutor Executor => _executor;
    public BasicEngines Engines => _engines;
    public DriverManager Drivers => _drivers;
    public IReadOnlyList<ExecutionContext> MainThreads => _mainThreads;
    public IReadOnlyDictionary<string, ExecutionContext> SubThreads => _subThreads;

    public MvconThreadVM(BasicEngines engines, DriverManager drivers, CommandExecutor? executor = null)
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
            ctx.Program = GetProgram(progNo);
            ctx.ProgIndex = startIndex;
            ctx.CallStack.Clear();
            ctx.State.Reset();
            ctx.IsRunning = true;
            _engines.Io.SetPort($"{ctx.ThreadName}#Run?", true);
        }
    }

    /// <summary>
    /// 掛起/暫停主執行緒
    /// </summary>
    public void SuspendThread(int threadNo)
    {
        if (threadNo < 0 || threadNo >= 13) return;
        var ctx = _mainThreads[threadNo];
        lock (_lock)
        {
            ctx.IsRunning = false;
            _engines.Io.SetPort($"{ctx.ThreadName}#Run?", false);
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
            ctx.IsRunning = true;
            _engines.Io.SetPort($"{ctx.ThreadName}#Run?", true);
        }
    }

    /// <summary>
    /// 啟動動態 SubThread (支援帶 & 特權後綴)
    /// </summary>
    public ExecutionContext StartSubThread(string subName, ProgramTable program, int startIndex = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subName);
        ArgumentNullException.ThrowIfNull(program);

        string cleanName = subName.Trim();
        var ctx = new ExecutionContext(100 + _subThreads.Count, cleanName, program, _engines, _drivers)
        {
            ProgIndex = startIndex,
            IsRunning = true,
            CurrentSection = cleanName,
            VM = this
        };

        _subThreads[cleanName] = ctx;
        _engines.Io.AddVirtualIO($"{cleanName}#Run?", true);
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
            ctx.IsRunning = false;
            _engines.Io.SetPort($"{ctx.ThreadName}#Run?", false);
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
    /// 遍歷所有活動執行緒執行單步指令，落實 SystemPause 與 & 特權閘門控制
    /// </summary>
    public void Tick()
    {
        bool isPaused = _engines.Timer.IsSystemPaused;

        // 1. 執行 13 個主執行緒
        for (int i = 0; i < 13; i++)
        {
            var ctx = _mainThreads[i];
            if (!ctx.IsRunning) continue;

            // Pause 閘門控制：若全機暫停且該執行緒不具 & 特權，跳過此輪
            if (isPaused && !ctx.IsBypassPause)
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

            if (isPaused && !ctx.IsBypassPause)
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
