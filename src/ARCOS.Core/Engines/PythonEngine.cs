using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using ARCOS.Core.Common;
using ARCOS.Core.Model;
using OpenCvSharp;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Core.Engines;

/// <summary>
/// Python 模組共用引擎 (供 MODULES/PYTHON/*.csvi 調用)
/// 封裝 Native Python 3.12 C-API 橋接、多 Session 隔離與無 Python 環境時的 Mock 保底
/// </summary>
public static class PythonEngine
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> MockSessions = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsAvailable => PythonNative.IsAvailable;
    public static string WorkingDir => PythonNative.WorkingDir;
    public static void EnsureInitialized(ExecutionContext? ctx = null) => PythonNative.EnsureInitialized(ctx);
    public static string ResolveFilePath(string file) => PythonNative.ResolveFilePath(file);

    public static void ResetAll()
    {
        PythonNative.ResetAllSessions();
        MockSessions.Clear();
    }

    public static void ResetSession(string sessionName)
    {
        if (PythonNative.IsAvailable) PythonNative.ResetSession(sessionName);
        if (MockSessions.TryGetValue(sessionName, out var dict)) dict.Clear();
    }

    private static ConcurrentDictionary<string, string> Mock(string session) =>
        MockSessions.GetOrAdd(session, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>解析 Session：顯式 Session=xxx 優先，否則以當前 Section / Thread 名稱隱式隔離</summary>
    public static string ResolveSession(Dictionary<string, string> parsed, string filteredParam, ExecutionContext ctx)
    {
        if (parsed.TryGetValue("Session", out var s) && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        if (filteredParam.StartsWith("Session=", StringComparison.OrdinalIgnoreCase))
        {
            int commaIdx = filteredParam.IndexOfAny(new[] { ',', '\n', '\r' });
            if (commaIdx > 0)
            {
                string sessPart = filteredParam.Substring(8, commaIdx - 8).Trim();
                if (!string.IsNullOrEmpty(sessPart)) return sessPart;
            }
        }
        string raw = !string.IsNullOrWhiteSpace(ctx.CurrentSection)
            ? ctx.CurrentSection
            : (!string.IsNullOrWhiteSpace(ctx.ThreadName) ? ctx.ThreadName : "ThreadMain");
        return raw.Trim('[', ']', ' ', '\t');
    }

    public static string ExtractScriptBody(string filteredParam)
    {
        string script = filteredParam.Trim();
        if (script.StartsWith("Session=", StringComparison.OrdinalIgnoreCase))
        {
            int commaIdx = script.IndexOfAny(new[] { ',', '\n', '\r' });
            if (commaIdx > 0) script = script.Substring(commaIdx + 1).Trim();
        }
        return script.TrimEnd(';');
    }

    /// <summary>執行腳本 (Native 優先，無 Python 時以 Mock 解析 a = b 形式指派)</summary>
    public static bool RunScript(string sessionName, string script, out string? error)
    {
        error = null;
        if (PythonNative.IsAvailable) return PythonNative.RunScript(sessionName, script, out error);
        var dict = Mock(sessionName);
        foreach (var line in script.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim().TrimEnd(';');
            if (trimmed.Contains('='))
            {
                var kv = trimmed.Split('=', 2);
                dict[kv[0].Trim()] = kv[1].Trim();
            }
        }
        return true;
    }

    public static void SetVariable(string sessionName, string pyName, string val)
    {
        if (PythonNative.IsAvailable) PythonNative.SetVariable(sessionName, pyName, val);
        else Mock(sessionName)[pyName] = val;
    }

    public static string GetVariable(string sessionName, string pyName)
    {
        if (PythonNative.IsAvailable) return PythonNative.GetVariable(sessionName, pyName) ?? "";
        return MockSessions.TryGetValue(sessionName, out var d) && d.TryGetValue(pyName, out var v) ? v : "";
    }

    public static void SetPoint(string sessionName, string pyName, MachinePoint pt)
    {
        if (PythonNative.IsAvailable) PythonNative.SetPoint(sessionName, pyName, pt);
        else Mock(sessionName)[pyName] = $"[{pt.X},{pt.Y},{pt.Z},{pt.U},{pt.V},{pt.W}]";
    }

    public static bool GetPoint(string sessionName, string pyName, out MachinePoint pt)
    {
        if (PythonNative.IsAvailable) return PythonNative.GetPoint(sessionName, pyName, out pt);
        pt = new MachinePoint();
        if (!(MockSessions.TryGetValue(sessionName, out var d) && d.TryGetValue(pyName, out var json))) return false;
        var parts = json.Trim('[', ']', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries);
        double P(int i) => parts.Length > i && double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0;
        pt.X = P(0); pt.Y = P(1); pt.Z = P(2); pt.U = P(3); pt.V = P(4); pt.W = P(5);
        return true;
    }

    public static bool SetCapture(string sessionName, string pyName, Mat mat)
    {
        if (!PythonNative.IsAvailable) return false;
        PythonNative.SetCapture(sessionName, pyName, mat);
        return true;
    }

    public static bool GetCapture(string sessionName, string pyName, out Mat mat)
    {
        if (PythonNative.IsAvailable) return PythonNative.GetCapture(sessionName, pyName, out mat);
        mat = new Mat();
        return false;
    }

    public static void LoadCaptureFromFile(string sessionName, string pyName, string filePath)
    {
        if (PythonNative.IsAvailable) PythonNative.LoadCaptureFromFile(sessionName, pyName, filePath);
    }
}

#region Native Python 3.12 C-API Bridge
    public static class PythonNative
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
                    ctx?.Log("[Python] System Python 3.12 (python312.dll) not found. Please install Python 3.12 and add it to PATH. Running in Mock mode (results are NOT real Python).", isError: true);
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
_py_exe = os.path.join(r'{Path.GetDirectoryName(dllPath)}', 'python.exe')
if os.path.exists(_py_exe):
    sys.executable = _py_exe
    sys._base_executable = _py_exe
    try:
        import multiprocessing
        multiprocessing.set_executable(_py_exe)
    except Exception:
        pass
_arcos_env_info = f'{{sys.version.split()[0]}} | prefix={{sys.prefix}} | exe={{sys.executable}}'
w_dir = r'{_workingDir}'
if os.path.exists(w_dir):
    os.chdir(w_dir)
    if w_dir not in sys.path:
        sys.path.insert(0, w_dir)
";
                        _pyRunStringFlags(initSetup, 257, rootDict, rootDict, IntPtr.Zero);
                        if (_pyErrOccurred() != IntPtr.Zero) _pyErrClear();
                        IntPtr info = _pyDictGetItemString(rootDict, "_arcos_env_info");
                        if (info != IntPtr.Zero) ctx?.Log($"[Python] System Python environment: {Marshal.PtrToStringUTF8(_pyUnicodeAsUtf8(info))} | WorkingDir: {_workingDir}");
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

        /// <summary>
        /// 尋找與使用者終端機相同的系統 Python 3.12：
        /// 1. PATH 中第一個 python.exe (即在 CMD 輸入 python 時實際執行者) 同目錄的 python312.dll
        /// 2. PATH 中直接含 python312.dll 的目錄
        /// 3. 登錄檔 PythonCore\3.12\InstallPath (HKCU 優先，再 HKLM)
        /// 4. 常見安裝位置
        /// </summary>
        private static string? FindPython312Dll()
        {
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('"')).ToList();

            foreach (var p in pathDirs)
            {
                if (p.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue; // Store 轉址空殼
                if (File.Exists(Path.Combine(p, "python.exe")))
                {
                    string dll = Path.Combine(p, "python312.dll");
                    if (File.Exists(dll)) return dll;
                    break; // 使用者預設 python 不是 3.12，改走後續規則
                }
            }

            foreach (var p in pathDirs)
            {
                string dll = Path.Combine(p, "python312.dll");
                if (File.Exists(dll)) return dll;
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                {
                    try
                    {
                        using var key = hive.OpenSubKey(@"Software\Python\PythonCore\3.12\InstallPath");
                        if (key?.GetValue("") is string dir)
                        {
                            string dll = Path.Combine(dir, "python312.dll");
                            if (File.Exists(dll)) return dll;
                        }
                    }
                    catch { }
                }
            }

            var fixedCandidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python312\python312.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Python312\python312.dll"),
                @"C:\Python312\python312.dll"
            };
            return fixedCandidates.FirstOrDefault(File.Exists);
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
