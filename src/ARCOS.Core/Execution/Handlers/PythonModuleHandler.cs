using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ARCOS.Core.Common;
using ARCOS.Core.Model;
using OpenCvSharp;

namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// Python 內嵌運算、多 Session 隔離與數據交換處理器 (Python@)
/// 提供腳本執行 (Script)、檔案執行 (File)、變數/NumPy點位/OpenCV影像與 Python 之間的即時橋接
/// </summary>
public class PythonModuleHandler : IModuleHandler, IDisposable
{
    public virtual string ModuleName => "Python";

    private static readonly string[] Commands = new[]
    {
        "Init",
        "Reset",
        "Script",
        "File",
        "SetVariable",
        "GetVariable",
        "SetPoint",
        "GetPoint",
        "SetCapture",
        "GetCapture",
        "SetCaptureFile",
        "ResetSession"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    // 回退 Mock 快取（當系統未安裝 Python 3.12 DLL 時維持無崩潰運作）
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> MockSessions = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        ResetAll();
    }

    public static void ResetAll()
    {
        PythonNative.ResetAllSessions();
        MockSessions.Clear();
    }

    public virtual CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);
        string sessionName = ResolveSession(parsed, filteredParam, ctx);

        // 確保 Native Python 嘗試初始化
        PythonNative.EnsureInitialized(ctx);

        // 0. Init (由 System@IncludeModule 透過 Init.csvi 調用)
        if (action.Equals("Init", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log("[Python] Initializing Python module environment via Init.csvi...");
            PythonNative.EnsureInitialized(ctx);

            string initPy = PythonNative.ResolveFilePath("init.py");
            if (File.Exists(initPy))
            {
                ctx.Log($"[Python] Found custom bootstrap script '{initPy}'. Executing...");
                if (PythonNative.IsAvailable)
                {
                    string code = File.ReadAllText(initPy);
                    bool ok = PythonNative.RunScript("ThreadMain", code, out var err);
                    if (!ok && !string.IsNullOrEmpty(err))
                    {
                        ctx.Log($"[Python] init.py error: {err}", isError: true);
                    }
                    else
                    {
                        ctx.Log("[Python] Custom init.py executed successfully in ThreadMain session.");
                    }
                }
            }
            ctx.Log("[Python] Module initialization completed.");
            return CommandResult.Done();
        }

        // 1. Script::$pythonCode
        if (action.Equals("Script", StringComparison.OrdinalIgnoreCase))
        {
            string script = ExtractScriptBody(filteredParam);
            ctx.Log($"[Python][Session:{sessionName}] Executing inline script:\n{script}");

            if (PythonNative.IsAvailable)
            {
                bool ok = PythonNative.RunScript(sessionName, script, out var err);
                if (!ok && !string.IsNullOrEmpty(err))
                {
                    ctx.Log($"[Python][Session:{sessionName}] Script error: {err}", isError: true);
                }
            }
            else
            {
                ExecuteMockScript(sessionName, script);
            }
            return CommandResult.Done();
        }

        // 2. File::$script.py
        if (action.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string pyFile = posArgs[0];
            string resolvedPath = PythonNative.ResolveFilePath(pyFile);

            ctx.Log($"[Python][Session:{sessionName}] Executing file: {resolvedPath}");

            if (File.Exists(resolvedPath))
            {
                string fileContent = File.ReadAllText(resolvedPath);
                if (PythonNative.IsAvailable)
                {
                    bool ok = PythonNative.RunScript(sessionName, fileContent, out var err);
                    if (!ok && !string.IsNullOrEmpty(err))
                    {
                        ctx.Log($"[Python][Session:{sessionName}] File execution error: {err}", isError: true);
                    }
                }
                else
                {
                    ExecuteMockFile(resolvedPath, ctx);
                }
            }
            else
            {
                ctx.Log($"[Python] File not found: {resolvedPath}", isError: true);
            }
            return CommandResult.Done();
        }

        // 3. SetVariable::$var(, $val)(, Session=$session)(, PythonName=$pyvar)
        if (action.Equals("SetVariable", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string varName = posArgs[0];
            string val;

            if (posArgs.Count > 1)
            {
                val = posArgs[1];
            }
            else
            {
                val = ctx.Engines.Variable.GetVariable(varName, ctx.CurrentSection);
            }

            string pyName = parsed.TryGetValue("PythonName", out var p) ? p : varName;

            if (PythonNative.IsAvailable)
            {
                PythonNative.SetVariable(sessionName, pyName, val);
            }
            else
            {
                var mockDict = MockSessions.GetOrAdd(sessionName, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                mockDict[pyName] = val;
            }

            ctx.Log($"[Python][Session:{sessionName}] SetVariable '{pyName}' = '{val}'");
            return CommandResult.Done();
        }

        // 4. GetVariable::$pyvar(, Session=$session)(, ARCOSName=$var)
        if (action.Equals("GetVariable", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string pyName = posArgs[0];
            string arcosName = parsed.TryGetValue("ARCOSName", out var a) ? a : pyName;

            string? val = "";
            if (PythonNative.IsAvailable)
            {
                val = PythonNative.GetVariable(sessionName, pyName) ?? "";
            }
            else
            {
                if (MockSessions.TryGetValue(sessionName, out var mockDict))
                {
                    mockDict.TryGetValue(pyName, out val);
                }
                val ??= "";
            }

            ctx.Engines.Variable.SetVariable(arcosName, val ?? "", ctx.CurrentSection);
            ctx.Log($"[Python][Session:{sessionName}] GetVariable '{pyName}' -> ${arcosName} = '{val}'");
            return CommandResult.Done();
        }

        // 5. SetPoint::$point(, Session=$session)(, PythonName=$py)
        if (action.Equals("SetPoint", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string ptName = posArgs[0];
            string pyName = parsed.TryGetValue("PythonName", out var p) ? p : ptName;

            var pt = ctx.Engines.Point.GetPoint(ptName);

            if (PythonNative.IsAvailable)
            {
                PythonNative.SetPoint(sessionName, pyName, pt);
            }
            else
            {
                string ptJson = $"[{pt.X},{pt.Y},{pt.Z},{pt.U},{pt.V},{pt.W}]";
                var mockDict = MockSessions.GetOrAdd(sessionName, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                mockDict[pyName] = ptJson;
            }

            ctx.Log($"[Python][Session:{sessionName}] SetPoint '{pyName}' = ({pt.X},{pt.Y},{pt.Z},{pt.U},{pt.V},{pt.W})");
            return CommandResult.Done();
        }

        // 6. GetPoint::$py(, Session=$session)(, ARCOSName=$point)
        if (action.Equals("GetPoint", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string pyName = posArgs[0];
            string ptName = parsed.TryGetValue("ARCOSName", out var a) ? a : pyName;

            if (PythonNative.IsAvailable)
            {
                if (PythonNative.GetPoint(sessionName, pyName, out var pt))
                {
                    ctx.Engines.Point.SetPoint(ptName, pt);
                    ctx.Log($"[Python][Session:{sessionName}] GetPoint '{pyName}' -> Point '{ptName}' ({pt.X},{pt.Y},{pt.Z},{pt.U},{pt.V},{pt.W})");
                }
                else
                {
                    ctx.Log($"[Python][Session:{sessionName}] GetPoint failed for '{pyName}'", isError: true);
                }
            }
            else
            {
                if (MockSessions.TryGetValue(sessionName, out var mockDict) &&
                    mockDict.TryGetValue(pyName, out var jsonStr))
                {
                    var trimmed = jsonStr.Trim('[', ']', ' ');
                    var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    var pt = new MachinePoint();
                    if (parts.Length > 0 && double.TryParse(parts[0], out double x)) pt.X = x;
                    if (parts.Length > 1 && double.TryParse(parts[1], out double y)) pt.Y = y;
                    if (parts.Length > 2 && double.TryParse(parts[2], out double z)) pt.Z = z;
                    if (parts.Length > 3 && double.TryParse(parts[3], out double u)) pt.U = u;
                    if (parts.Length > 4 && double.TryParse(parts[4], out double v)) pt.V = v;
                    if (parts.Length > 5 && double.TryParse(parts[5], out double w)) pt.W = w;

                    ctx.Engines.Point.SetPoint(ptName, pt);
                    ctx.Log($"[Python][Session:{sessionName}] GetPoint '{pyName}' -> Point '{ptName}' ({pt.X},{pt.Y},{pt.Z})");
                }
            }
            return CommandResult.Done();
        }

        // 7. SetCapture::$capture(, Session=$session)(, PythonName=$py)
        if (action.Equals("SetCapture", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string capName = posArgs[0];
            string pyName = parsed.TryGetValue("PythonName", out var p) ? p : capName;

            try
            {
                var frame = ctx.Engines.Image.GetFrame(capName);
                if (frame.Image != null && PythonNative.IsAvailable)
                {
                    PythonNative.SetCapture(sessionName, pyName, frame.Image);
                    ctx.Log($"[Python][Session:{sessionName}] SetCapture '{capName}' -> '{pyName}' ({frame.Image.Width}x{frame.Image.Height})");
                }
            }
            catch (Exception ex)
            {
                ctx.Log($"[Python][Session:{sessionName}] SetCapture '{capName}' error: {ex.Message}", isError: true);
            }
            return CommandResult.Done();
        }

        // 8. GetCapture::$py(, Session=$session)(, ARCOSName=$capture)
        if (action.Equals("GetCapture", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string pyName = posArgs[0];
            string capName = parsed.TryGetValue("ARCOSName", out var a) ? a : pyName;

            if (PythonNative.IsAvailable)
            {
                if (PythonNative.GetCapture(sessionName, pyName, out var mat))
                {
                    ctx.Engines.Image.SetSingleImage(capName, mat);
                    ctx.Log($"[Python][Session:{sessionName}] GetCapture '{pyName}' -> ARCOS Image '{capName}' ({mat.Width}x{mat.Height})");
                }
                else
                {
                    ctx.Log($"[Python][Session:{sessionName}] GetCapture failed for '{pyName}'", isError: true);
                }
            }
            return CommandResult.Done();
        }

        // 9. SetCaptureFile::$capture, $file.png, $pyvar(, Session=$session)
        if (action.Equals("SetCaptureFile", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 3)
            {
                string capName = posArgs[0];
                string file = posArgs[1];
                string pyName = posArgs[2];

                try
                {
                    var frame = ctx.Engines.Image.GetFrame(capName);
                    string fullPath = PythonNative.ResolveFilePath(file);
                    var parentDir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }
                    frame.Image.SaveImage(fullPath);

                    if (PythonNative.IsAvailable)
                    {
                        PythonNative.LoadCaptureFromFile(sessionName, pyName, fullPath);
                    }
                    ctx.Log($"[Python][Session:{sessionName}] SetCaptureFile saved '{capName}' -> '{fullPath}' and bound to '{pyName}'");
                }
                catch (Exception ex)
                {
                    ctx.Log($"[Python][Session:{sessionName}] SetCaptureFile error: {ex.Message}", isError: true);
                }
            }
            return CommandResult.Done();
        }

        // 10. ResetSession::($session)
        if (action.Equals("ResetSession", StringComparison.OrdinalIgnoreCase))
        {
            string targetSession = posArgs.Count > 0 ? posArgs[0].Trim() : sessionName;
            if (PythonNative.IsAvailable)
            {
                PythonNative.ResetSession(targetSession);
            }
            if (MockSessions.TryGetValue(targetSession, out var dict))
            {
                dict.Clear();
            }
            ctx.Log($"[Python] Session '{targetSession}' reset successfully.");
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }

    private static string ResolveSession(Dictionary<string, string> parsed, string filteredParam, ExecutionContext ctx)
    {
        if (parsed.TryGetValue("Session", out var s) && !string.IsNullOrWhiteSpace(s))
        {
            return s.Trim();
        }

        // 亦檢查前綴例如 Session=VisionAI,
        if (filteredParam.StartsWith("Session=", StringComparison.OrdinalIgnoreCase))
        {
            int commaIdx = filteredParam.IndexOfAny(new[] { ',', '\n', '\r' });
            if (commaIdx > 0)
            {
                string sessPart = filteredParam.Substring(8, commaIdx - 8).Trim();
                if (!string.IsNullOrEmpty(sessPart)) return sessPart;
            }
        }

        // 隱式預設 Session：以當前 Thread / Section 名稱自動隔離
        string raw = !string.IsNullOrWhiteSpace(ctx.CurrentSection)
            ? ctx.CurrentSection
            : (!string.IsNullOrWhiteSpace(ctx.ThreadName) ? ctx.ThreadName : "ThreadMain");

        return raw.Trim('[', ']', ' ', '\t');
    }

    private static string ExtractScriptBody(string filteredParam)
    {
        string script = filteredParam.Trim();
        if (script.StartsWith("Session=", StringComparison.OrdinalIgnoreCase))
        {
            int commaIdx = script.IndexOfAny(new[] { ',', '\n', '\r' });
            if (commaIdx > 0)
            {
                script = script.Substring(commaIdx + 1).Trim();
            }
        }
        return script.TrimEnd(';');
    }

    private static void ExecuteMockScript(string sessionName, string script)
    {
        var mockDict = MockSessions.GetOrAdd(sessionName, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var lines = script.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim().TrimEnd(';');
            if (trimmed.Contains('='))
            {
                var kv = trimmed.Split('=', 2);
                string k = kv[0].Trim();
                string v = kv[1].Trim();
                mockDict[k] = v;
            }
        }
    }

    private static void ExecuteMockFile(string pyFile, ExecutionContext ctx)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python.exe",
                Arguments = $"-X utf8 \"{pyFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(3000);
                string output = proc.StandardOutput.ReadToEnd();
                ctx.Log($"[Python] Script output: {output}");
            }
        }
        catch (Exception ex)
        {
            ctx.Log($"[Python] Process execution error: {ex.Message}", isError: true);
        }
    }

    #region Native Python 3.12 C-API Bridge

    internal static class PythonNative
    {
        private static readonly object GilLock = new();
        private static bool _initialized = false;
        private static bool _available = false;
        private static IntPtr _dllHandle = IntPtr.Zero;
        private static string _workingDir = "";

        private static readonly ConcurrentDictionary<string, IntPtr> SessionScopes = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsAvailable => _available;
        public static string WorkingDir => _workingDir;

        #region Delegates
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Py_Initialize();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Py_IsInitialized();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PyGILState_Ensure();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PyGILState_Release(int state);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PyEval_SaveThread();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PyDict_New();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PyDict_SetItemString(IntPtr dp, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, IntPtr item);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PyDict_GetItemString(IntPtr dp, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PyDict_Clear(IntPtr dp);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PyEval_GetBuiltins();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PyRun_StringFlags([MarshalAs(UnmanagedType.LPUTF8Str)] string str, int start, IntPtr globals, IntPtr locals, IntPtr flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PyObject_Str(IntPtr o);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PyUnicode_AsUTF8(IntPtr unicode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PyErr_Occurred();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PyErr_Clear();

        private static Py_Initialize? _pyInitialize;
        private static Py_IsInitialized? _pyIsInitialized;
        private static PyGILState_Ensure? _pyGilStateEnsure;
        private static PyGILState_Release? _pyGilStateRelease;
        private static PyEval_SaveThread? _pyEvalSaveThread;
        private static PyDict_New? _pyDictNew;
        private static PyDict_SetItemString? _pyDictSetItemString;
        private static PyDict_GetItemString? _pyDictGetItemString;
        private static PyDict_Clear? _pyDictClear;
        private static PyEval_GetBuiltins? _pyEvalGetBuiltins;
        private static PyRun_StringFlags? _pyRunStringFlags;
        private static PyObject_Str? _pyObjectStr;
        private static PyUnicode_AsUTF8? _pyUnicodeAsUtf8;
        private static PyErr_Occurred? _pyErrOccurred;
        private static PyErr_Clear? _pyErrClear;
        #endregion

        public static void EnsureInitialized(ExecutionContext? ctx = null)
        {
            if (_initialized) return;

            lock (GilLock)
            {
                if (_initialized) return;
                _initialized = true;

                // 決定預設工作目錄：指向 MODULES/PYTHON
                _workingDir = ResolveDefaultModulesPythonDir();
                try
                {
                    if (!Directory.Exists(_workingDir))
                    {
                        Directory.CreateDirectory(_workingDir);
                    }
                }
                catch { }

                string? dllPath = FindPython312Dll();
                if (dllPath == null)
                {
                    ctx?.Log("[Python] Python 3.12 DLL not found on system. Falling back to Mock mode.");
                    return;
                }

                try
                {
                    _dllHandle = NativeLibrary.Load(dllPath);

                    _pyInitialize = GetExport<Py_Initialize>("Py_Initialize");
                    _pyIsInitialized = GetExport<Py_IsInitialized>("Py_IsInitialized");
                    _pyGilStateEnsure = GetExport<PyGILState_Ensure>("PyGILState_Ensure");
                    _pyGilStateRelease = GetExport<PyGILState_Release>("PyGILState_Release");
                    _pyEvalSaveThread = GetExport<PyEval_SaveThread>("PyEval_SaveThread");
                    _pyDictNew = GetExport<PyDict_New>("PyDict_New");
                    _pyDictSetItemString = GetExport<PyDict_SetItemString>("PyDict_SetItemString");
                    _pyDictGetItemString = GetExport<PyDict_GetItemString>("PyDict_GetItemString");
                    _pyDictClear = GetExport<PyDict_Clear>("PyDict_Clear");
                    _pyEvalGetBuiltins = GetExport<PyEval_GetBuiltins>("PyEval_GetBuiltins");
                    _pyRunStringFlags = GetExport<PyRun_StringFlags>("PyRun_StringFlags");
                    _pyObjectStr = GetExport<PyObject_Str>("PyObject_Str");
                    _pyUnicodeAsUtf8 = GetExport<PyUnicode_AsUTF8>("PyUnicode_AsUTF8");
                    _pyErrOccurred = GetExport<PyErr_Occurred>("PyErr_Occurred");
                    _pyErrClear = GetExport<PyErr_Clear>("PyErr_Clear");

                    if (_pyIsInitialized() == 0)
                    {
                        _pyInitialize();
                    }

                    // 釋放初始執行緒之 GIL，讓背景多執行緒可透過 PyGILState_Ensure 自由調度
                    _pyEvalSaveThread();

                    _available = true;
                    ctx?.Log($"[Python] Python 3.12 native engine initialized successfully from '{dllPath}'. WorkingDir: '{_workingDir}'");

                    // 初始化全域路徑與常用模組
                    int gstate = _pyGilStateEnsure();
                    try
                    {
                        IntPtr rootDict = _pyDictNew();
                        _pyDictSetItemString(rootDict, "__builtins__", _pyEvalGetBuiltins());
                        string initSetup = $@"
import sys, os
w_dir = r'{_workingDir}'
if os.path.exists(w_dir):
    os.chdir(w_dir)
    if w_dir not in sys.path:
        sys.path.insert(0, w_dir)
";
                        _pyRunStringFlags(initSetup, 257, rootDict, rootDict, IntPtr.Zero);
                        if (_pyErrOccurred() != IntPtr.Zero) _pyErrClear();
                    }
                    finally
                    {
                        _pyGilStateRelease(gstate);
                    }
                }
                catch (Exception ex)
                {
                    _available = false;
                    ctx?.Log($"[Python] Failed to initialize native Python engine: {ex.Message}. Falling back to Mock mode.", isError: true);
                }
            }
        }

        private static T GetExport<T>(string name) where T : Delegate
        {
            var ptr = NativeLibrary.GetExport(_dllHandle, name);
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        private static string? FindPython312Dll()
        {
            // 1. 標準 C:\Python312
            if (File.Exists(@"C:\Python312\python312.dll")) return @"C:\Python312\python312.dll";

            // 2. PATH 中的 python.exe 目錄
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                var paths = pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in paths)
                {
                    string candidate = Path.Combine(p.Trim(), "python312.dll");
                    if (File.Exists(candidate)) return candidate;
                }
            }

            // 3. 使用者 LocalAppData 與 ProgramFiles
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localPy = Path.Combine(localApp, @"Programs\Python\Python312\python312.dll");
            if (File.Exists(localPy)) return localPy;

            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progPy = Path.Combine(progFiles, @"Python312\python312.dll");
            if (File.Exists(progPy)) return progPy;

            return null;
        }

        private static string ResolveDefaultModulesPythonDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "MODULES", "PYTHON"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES", "PYTHON")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "src", "MODULES", "PYTHON")),
                Path.Combine(Directory.GetCurrentDirectory(), "MODULES", "PYTHON")
            };

            foreach (var c in candidates)
            {
                if (Directory.Exists(c)) return c;
            }

            return candidates[0];
        }

        public static string ResolveFilePath(string file)
        {
            if (Path.IsPathRooted(file)) return file;

            string inMod = Path.Combine(_workingDir, file);
            if (File.Exists(inMod)) return inMod;

            string inBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
            if (File.Exists(inBase)) return inBase;

            return inMod;
        }

        private static IntPtr GetOrCreateSessionDict(string sessionName)
        {
            return SessionScopes.GetOrAdd(sessionName, name =>
            {
                IntPtr dict = _pyDictNew!();
                _pyDictSetItemString!(dict, "__builtins__", _pyEvalGetBuiltins!());

                // 在該 Session 內初始化必要模組與工作目錄
                string sessionInit = $@"
import sys, os, json, base64
w_dir = r'{_workingDir}'
if os.path.exists(w_dir):
    os.chdir(w_dir)
    if w_dir not in sys.path:
        sys.path.insert(0, w_dir)
try:
    import numpy as np
except ImportError:
    pass
try:
    import cv2
except ImportError:
    pass
";
                _pyRunStringFlags!(sessionInit, 257, dict, dict, IntPtr.Zero);
                if (_pyErrOccurred!() != IntPtr.Zero) _pyErrClear!();
                return dict;
            });
        }

        public static void ResetAllSessions()
        {
            lock (GilLock)
            {
                if (_available && _pyGilStateEnsure != null && _pyGilStateRelease != null && _pyDictClear != null)
                {
                    int gstate = _pyGilStateEnsure();
                    try
                    {
                        foreach (var kvp in SessionScopes)
                        {
                            try
                            {
                                _pyDictClear(kvp.Value);
                            }
                            catch { }
                        }
                        SessionScopes.Clear();
                    }
                    finally
                    {
                        _pyGilStateRelease(gstate);
                    }
                }
                else
                {
                    SessionScopes.Clear();
                }
            }
        }

        public static void ResetSession(string sessionName)
        {
            if (!_available) return;

            lock (GilLock)
            {
                if (SessionScopes.TryGetValue(sessionName, out var dict))
                {
                    int gstate = _pyGilStateEnsure!();
                    try
                    {
                        _pyDictClear!(dict);
                        _pyDictSetItemString!(dict, "__builtins__", _pyEvalGetBuiltins!());
                        string reInit = $@"
import sys, os, json, base64
w_dir = r'{_workingDir}'
if os.path.exists(w_dir):
    os.chdir(w_dir)
    if w_dir not in sys.path:
        sys.path.insert(0, w_dir)
try:
    import numpy as np
except ImportError:
    pass
try:
    import cv2
except ImportError:
    pass
";
                        _pyRunStringFlags!(reInit, 257, dict, dict, IntPtr.Zero);
                        if (_pyErrOccurred!() != IntPtr.Zero) _pyErrClear!();
                    }
                    finally
                    {
                        _pyGilStateRelease!(gstate);
                    }
                }
            }
        }

        public static bool RunScript(string sessionName, string script, out string? errorMessage)
        {
            errorMessage = null;
            if (!_available) return false;

            lock (GilLock)
            {
                int gstate = _pyGilStateEnsure!();
                try
                {
                    IntPtr sessionDict = GetOrCreateSessionDict(sessionName);
                    _pyRunStringFlags!(script, 257, sessionDict, sessionDict, IntPtr.Zero);

                    if (_pyErrOccurred!() != IntPtr.Zero)
                    {
                        // 取得例外資訊
                        string getTrace = @"
import sys, traceback
__arcos_err = traceback.format_exc()
";
                        _pyRunStringFlags!(getTrace, 257, sessionDict, sessionDict, IntPtr.Zero);
                        IntPtr errVar = _pyDictGetItemString!(sessionDict, "__arcos_err");
                        if (errVar != IntPtr.Zero)
                        {
                            IntPtr strObj = _pyObjectStr!(errVar);
                            IntPtr utf8Ptr = _pyUnicodeAsUtf8!(strObj);
                            errorMessage = Marshal.PtrToStringUTF8(utf8Ptr);
                        }
                        _pyErrClear!();
                        return false;
                    }
                    return true;
                }
                finally
                {
                    _pyGilStateRelease!(gstate);
                }
            }
        }

        public static void SetVariable(string sessionName, string pyName, string val)
        {
            if (!_available) return;

            lock (GilLock)
            {
                int gstate = _pyGilStateEnsure!();
                try
                {
                    IntPtr sessionDict = GetOrCreateSessionDict(sessionName);
                    string escapedVal = val.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
                    string setCode = $@"
try:
    import json
    {pyName} = json.loads(""{escapedVal}"")
except Exception:
    {pyName} = ""{escapedVal}""
";
                    _pyRunStringFlags!(setCode, 257, sessionDict, sessionDict, IntPtr.Zero);
                    if (_pyErrOccurred!() != IntPtr.Zero) _pyErrClear!();
                }
                finally
                {
                    _pyGilStateRelease!(gstate);
                }
            }
        }

        public static string? GetVariable(string sessionName, string pyName)
        {
            if (!_available) return null;

            lock (GilLock)
            {
                int gstate = _pyGilStateEnsure!();
                try
                {
                    IntPtr sessionDict = GetOrCreateSessionDict(sessionName);
                    IntPtr varObj = _pyDictGetItemString!(sessionDict, pyName);
                    if (varObj == IntPtr.Zero) return null;

                    IntPtr strObj = _pyObjectStr!(varObj);
                    IntPtr utf8Ptr = _pyUnicodeAsUtf8!(strObj);
                    return Marshal.PtrToStringUTF8(utf8Ptr);
                }
                finally
                {
                    _pyGilStateRelease!(gstate);
                }
            }
        }

        public static void SetPoint(string sessionName, string pyName, MachinePoint pt)
        {
            if (!_available) return;

            lock (GilLock)
            {
                int gstate = _pyGilStateEnsure!();
                try
                {
                    IntPtr sessionDict = GetOrCreateSessionDict(sessionName);
                    string setCode = $"{pyName} = np.array([{pt.X}, {pt.Y}, {pt.Z}, {pt.U}, {pt.V}, {pt.W}], dtype=np.float64)";
                    _pyRunStringFlags!(setCode, 257, sessionDict, sessionDict, IntPtr.Zero);
                    if (_pyErrOccurred!() != IntPtr.Zero) _pyErrClear!();
                }
                finally
                {
                    _pyGilStateRelease!(gstate);
                }
            }
        }

        public static bool GetPoint(string sessionName, string pyName, out MachinePoint pt)
        {
            pt = new MachinePoint();
            if (!_available) return false;

            lock (GilLock)
            {
                int gstate = _pyGilStateEnsure!();
                try
                {
                    IntPtr sessionDict = GetOrCreateSessionDict(sessionName);
                    string extractCode = $@"
try:
    __arcos_pt_vals = [float(v) for v in {pyName}]
    __arcos_pt_str = ','.join(str(v) for v in __arcos_pt_vals)
except Exception:
    __arcos_pt_str = ''
";
                    _pyRunStringFlags!(extractCode, 257, sessionDict, sessionDict, IntPtr.Zero);
                    if (_pyErrOccurred!() != IntPtr.Zero)
                    {
                        _pyErrClear!();
                        return false;
                    }

                    IntPtr ptStrObj = _pyDictGetItemString!(sessionDict, "__arcos_pt_str");
                    if (ptStrObj == IntPtr.Zero) return false;

                    string? ptStr = Marshal.PtrToStringUTF8(_pyUnicodeAsUtf8!(_pyObjectStr!(ptStrObj)));
                    if (string.IsNullOrEmpty(ptStr)) return false;

                    var parts = ptStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && double.TryParse(parts[0], out double x)) pt.X = x;
                    if (parts.Length > 1 && double.TryParse(parts[1], out double y)) pt.Y = y;
                    if (parts.Length > 2 && double.TryParse(parts[2], out double z)) pt.Z = z;
                    if (parts.Length > 3 && double.TryParse(parts[3], out double u)) pt.U = u;
                    if (parts.Length > 4 && double.TryParse(parts[4], out double v)) pt.V = v;
                    if (parts.Length > 5 && double.TryParse(parts[5], out double w)) pt.W = w;

                    return true;
                }
                finally
                {
                    _pyGilStateRelease!(gstate);
                }
            }
        }

        public static void SetCapture(string sessionName, string pyName, Mat mat)
        {
            if (!_available || mat == null) return;

            lock (GilLock)
            {
                int gstate = _pyGilStateEnsure!();
                try
                {
                    IntPtr sessionDict = GetOrCreateSessionDict(sessionName);
                    Cv2.ImEncode(".png", mat, out var buf);
                    string b64 = Convert.ToBase64String(buf);

                    string code = $@"
__raw_b = base64.b64decode(""{b64}"")
{pyName} = cv2.imdecode(np.frombuffer(__raw_b, np.uint8), cv2.IMREAD_UNCHANGED)
";
                    _pyRunStringFlags!(code, 257, sessionDict, sessionDict, IntPtr.Zero);
                    if (_pyErrOccurred!() != IntPtr.Zero) _pyErrClear!();
                }
                finally
                {
                    _pyGilStateRelease!(gstate);
                }
            }
        }

        public static bool GetCapture(string sessionName, string pyName, out Mat mat)
        {
            mat = new Mat();
            if (!_available) return false;

            lock (GilLock)
            {
                int gstate = _pyGilStateEnsure!();
                try
                {
                    IntPtr sessionDict = GetOrCreateSessionDict(sessionName);
                    string code = $@"
try:
    __ok, __enc = cv2.imencode('.png', {pyName})
    __arcos_cap_b64 = base64.b64encode(__enc.tobytes()).decode('ascii') if __ok else ''
except Exception:
    __arcos_cap_b64 = ''
";
                    _pyRunStringFlags!(code, 257, sessionDict, sessionDict, IntPtr.Zero);
                    if (_pyErrOccurred!() != IntPtr.Zero)
                    {
                        _pyErrClear!();
                        return false;
                    }

                    IntPtr b64Obj = _pyDictGetItemString!(sessionDict, "__arcos_cap_b64");
                    if (b64Obj == IntPtr.Zero) return false;

                    string? b64 = Marshal.PtrToStringUTF8(_pyUnicodeAsUtf8!(_pyObjectStr!(b64Obj)));
                    if (string.IsNullOrEmpty(b64)) return false;

                    byte[] raw = Convert.FromBase64String(b64);
                    mat = Mat.FromImageData(raw, ImreadModes.Unchanged);
                    return true;
                }
                finally
                {
                    _pyGilStateRelease!(gstate);
                }
            }
        }

        public static void LoadCaptureFromFile(string sessionName, string pyName, string filePath)
        {
            if (!_available) return;

            lock (GilLock)
            {
                int gstate = _pyGilStateEnsure!();
                try
                {
                    IntPtr sessionDict = GetOrCreateSessionDict(sessionName);
                    string escapedPath = filePath.Replace("\\", "/");
                    string code = $"{pyName} = cv2.imread('{escapedPath}', cv2.IMREAD_UNCHANGED)";
                    _pyRunStringFlags!(code, 257, sessionDict, sessionDict, IntPtr.Zero);
                    if (_pyErrOccurred!() != IntPtr.Zero) _pyErrClear!();
                }
                finally
                {
                    _pyGilStateRelease!(gstate);
                }
            }
        }
    }

    #endregion
}
