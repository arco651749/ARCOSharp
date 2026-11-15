using ARCOS.Core.Engines;
using Xunit;

namespace ARCOS.Core.Tests;

/// <summary>
/// 驗證 Python 模組使用「系統安裝的 Python 3.12」(與使用者終端機同一環境)，且工作目錄位於 ARCOS 的 MODULES/PYTHON
/// </summary>
public class PythonSystemEnvironmentTests
{
    [Fact]
    public void Python_UsesSystemPython312_And_WorkingDirIsModulesPython()
    {
        PythonEngine.EnsureInitialized();
        if (!PythonEngine.IsAvailable) return; // 未安裝 Python 3.12 的機器略過

        Assert.True(PythonEngine.RunScript("EnvTest",
            "import sys, os\nver = sys.version.split()[0]\nprefix = sys.prefix\nexe = sys.executable\ncwd = os.getcwd()\np0 = sys.path[0]", out var err), err);

        Assert.StartsWith("3.12", PythonEngine.GetVariable("EnvTest", "ver"));
        string exe = PythonEngine.GetVariable("EnvTest", "exe");
        Assert.EndsWith("python.exe", exe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetDirectoryName(exe), PythonEngine.GetVariable("EnvTest", "prefix"), ignoreCase: true);

        string cwd = PythonEngine.GetVariable("EnvTest", "cwd").TrimEnd('\\', '/');
        Assert.EndsWith(Path.Combine("MODULES", "PYTHON"), cwd, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(cwd, PythonEngine.GetVariable("EnvTest", "p0").TrimEnd('\\', '/'), ignoreCase: true);
        PythonEngine.ResetSession("EnvTest");
    }
}
