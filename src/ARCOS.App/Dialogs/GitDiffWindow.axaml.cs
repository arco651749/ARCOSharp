using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ARCOS.Core.Common;
using ARCOS.Core.Execution;
using ARCOS.Core.Serialization;

namespace ARCOS.App.Dialogs;

public partial class GitDiffWindow : Window
{
    private readonly string _targetFilePath;
    private readonly ArcosThreadVM? _vm;
    private readonly int _initialProgNo;

    private Dictionary<int, ProgramTable> _headPrograms = new();
    private Dictionary<int, ProgramTable> _workPrograms = new();
    private HashSet<int> _changedSections = new();
    private bool _isWholeFileDiff = false;

    public bool ReloadRequested { get; private set; } = false;
    public int? ReloadProgNo { get; private set; } = null;

    public GitDiffWindow() : this(System.IO.Path.Combine(ArcosPaths.RootDirectory, "Master.pro"), null, 0)
    {
    }

    public GitDiffWindow(string targetFilePath, ArcosThreadVM? vm, int initialProgNo = 0)
    {
        InitializeComponent();
        _targetFilePath = targetFilePath;
        _vm = vm;
        _initialProgNo = initialProgNo;

        var txtTarget = this.FindControl<TextBlock>("TxtTargetFile");
        if (txtTarget != null)
        {
            txtTarget.Text = $"Target: [ {Path.GetFileName(_targetFilePath)} ]";
        }

        var btnClose = this.FindControl<Button>("BtnClose");
        if (btnClose != null) btnClose.Click += (_, _) => Close();

        var btnLaunchMeld = this.FindControl<Button>("BtnLaunchMeld");
        if (btnLaunchMeld != null) btnLaunchMeld.Click += OnLaunchMeldClick;

        var btnReloadSection = this.FindControl<Button>("BtnReloadSection");
        if (btnReloadSection != null) btnReloadSection.Click += OnReloadSectionClick;

        var btnReloadAll = this.FindControl<Button>("BtnReloadToArcos");
        if (btnReloadAll != null) btnReloadAll.Click += OnReloadAllClick;

        var btnCompareSlot = this.FindControl<Button>("BtnLoadToCompareSlot");
        if (btnCompareSlot != null) btnCompareSlot.Click += OnLoadToCompareSlotClick;

        var cmb = this.FindControl<ComboBox>("CmbProgramSection");
        if (cmb != null)
        {
            cmb.SelectionChanged += OnSectionSelectionChanged;
        }

        AnalyzeAndLoadSections();
    }

    private void AnalyzeAndLoadSections()
    {
        try
        {
            string repoRoot = ArcosPaths.RootDirectory;
            string relPath = Path.GetRelativePath(repoRoot, _targetFilePath).Replace('\\', '/');

            // 1. 取得 HEAD 版本內容並解析
            string headProText = RunGitCommand($"show HEAD:\"{relPath}\"");
            _headPrograms = ScriptSerializer.DeserializePro(headProText);

            // 2. 取得 WorkTree 版本內容並解析
            var workList = File.Exists(_targetFilePath) ? ScriptSerializer.LoadProFile(_targetFilePath) : new List<ProgramTable>();
            _workPrograms = workList.ToDictionary(p => p.ProgramNumber, p => p);

            // 3. 找出所有程式槽位編號 (0 ~ 99)
            var allKeys = _headPrograms.Keys.Union(_workPrograms.Keys).OrderBy(k => k).ToList();
            _changedSections.Clear();

            var cmbItems = new List<string> { "[全部專案檔 (All Sections)]" };
            int selectIndex = 0;

            for (int i = 0; i < allKeys.Count; i++)
            {
                int pNo = allKeys[i];
                var hProg = _headPrograms.TryGetValue(pNo, out var hp) ? hp : null;
                var wProg = _workPrograms.TryGetValue(pNo, out var wp) ? wp : null;

                string hScript = hProg != null ? ScriptSerializer.ProgramToCleanScript(hProg) : "";
                string wScript = wProg != null ? ScriptSerializer.ProgramToCleanScript(wProg) : "";

                bool isDiff = !string.Equals(hScript, wScript, StringComparison.Ordinal);
                if (isDiff) _changedSections.Add(pNo);

                string name = wProg?.Name ?? hProg?.Name ?? $"Program_{pNo}";
                string mark = isDiff ? "● [有變更]" : "[無變更]";
                cmbItems.Add($"[{pNo:D2}] {name}  {mark}");

                if (pNo == _initialProgNo)
                {
                    selectIndex = i + 1; // 偏移 1 因第 0 項為全部專案
                }
            }

            var cmb = this.FindControl<ComboBox>("CmbProgramSection");
            if (cmb != null)
            {
                cmb.ItemsSource = cmbItems;
                cmb.SelectedIndex = (selectIndex < cmbItems.Count) ? selectIndex : 0;
            }
        }
        catch (Exception ex)
        {
            var txtDiff = this.FindControl<SelectableTextBlock>("TxtDiffContent");
            if (txtDiff != null) txtDiff.Text = $"分析程式區段失敗: {ex.Message}";
        }
    }

    private void OnSectionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var cmb = sender as ComboBox;
        if (cmb == null || cmb.SelectedIndex < 0) return;

        int idx = cmb.SelectedIndex;
        if (idx == 0)
        {
            // 全專案比對
            _isWholeFileDiff = true;
            RenderWholeFileDiff();
            UpdateButtonsForWhole();
        }
        else
        {
            _isWholeFileDiff = false;
            // 取得對應的 ProgramNumber
            var allKeys = _headPrograms.Keys.Union(_workPrograms.Keys).OrderBy(k => k).ToList();
            int progIndex = idx - 1;
            if (progIndex >= 0 && progIndex < allKeys.Count)
            {
                int pNo = allKeys[progIndex];
                RenderSectionDiff(pNo);
                UpdateButtonsForSection(pNo);
            }
        }
    }

    private void RenderWholeFileDiff()
    {
        var txtDiff = this.FindControl<SelectableTextBlock>("TxtDiffContent");
        var txtSummary = this.FindControl<TextBlock>("TxtDiffSummary");
        var txtHeader = this.FindControl<TextBlock>("TxtDiffViewHeader");
        var badgeStatus = this.FindControl<Border>("BadgeStatus");
        var txtStatus = this.FindControl<TextBlock>("TxtStatus");

        if (txtHeader != null) txtHeader.Text = "全專案檔 INI 差異比對 (Git Working Tree vs. HEAD)";

        try
        {
            string repoRoot = ArcosPaths.RootDirectory;
            string relPath = Path.GetRelativePath(repoRoot, _targetFilePath).Replace('\\', '/');
            string rawDiff = RunGitCommand($"diff HEAD -- \"{relPath}\"");

            if (string.IsNullOrWhiteSpace(rawDiff))
            {
                if (txtDiff != null) txtDiff.Text = "=== [全專案檔與 Git HEAD 完全同步，無任何未提交變更] ===";
                if (txtSummary != null) txtSummary.Text = "0 modified sections";
                if (badgeStatus != null) { badgeStatus.Background = new SolidColorBrush(Color.Parse("#F0FDF4")); badgeStatus.BorderBrush = new SolidColorBrush(Color.Parse("#BBF7D0")); }
                if (txtStatus != null) { txtStatus.Text = "Clean / Synced"; txtStatus.Foreground = new SolidColorBrush(Color.Parse("#166534")); }
            }
            else
            {
                if (txtDiff != null) txtDiff.Text = rawDiff;
                if (txtSummary != null) txtSummary.Text = $"{_changedSections.Count} sections changed";
                if (badgeStatus != null) { badgeStatus.Background = new SolidColorBrush(Color.Parse("#FEF2F2")); badgeStatus.BorderBrush = new SolidColorBrush(Color.Parse("#FECACA")); }
                if (txtStatus != null) { txtStatus.Text = "Modified"; txtStatus.Foreground = new SolidColorBrush(Color.Parse("#DC2626")); }
            }
        }
        catch (Exception ex)
        {
            if (txtDiff != null) txtDiff.Text = $"無法執行 Git 比對: {ex.Message}";
        }
    }

    private void RenderSectionDiff(int progNo)
    {
        var txtDiff = this.FindControl<SelectableTextBlock>("TxtDiffContent");
        var txtSummary = this.FindControl<TextBlock>("TxtDiffSummary");
        var txtHeader = this.FindControl<TextBlock>("TxtDiffViewHeader");
        var badgeStatus = this.FindControl<Border>("BadgeStatus");
        var txtStatus = this.FindControl<TextBlock>("TxtStatus");

        var hProg = _headPrograms.TryGetValue(progNo, out var hp) ? hp : null;
        var wProg = _workPrograms.TryGetValue(progNo, out var wp) ? wp : null;
        string name = wProg?.Name ?? hProg?.Name ?? $"Program_{progNo}";

        if (txtHeader != null) txtHeader.Text = $"[Program {progNo:D2}] {name} - 純指令解包比對視圖";

        string hScript = hProg != null ? ScriptSerializer.ProgramToCleanScript(hProg) : "";
        string wScript = wProg != null ? ScriptSerializer.ProgramToCleanScript(wProg) : "";

        if (string.Equals(hScript, wScript, StringComparison.Ordinal))
        {
            if (txtDiff != null) txtDiff.Text = $"// ========================================================\n// [Program {progNo}] {name}\n// 此區段與 Git HEAD 版本內容完全一致 (無任何變更)\n// ========================================================\n\n" + wScript;
            if (txtSummary != null) txtSummary.Text = "Section Clean";
            if (badgeStatus != null) { badgeStatus.Background = new SolidColorBrush(Color.Parse("#F0FDF4")); badgeStatus.BorderBrush = new SolidColorBrush(Color.Parse("#BBF7D0")); }
            if (txtStatus != null) { txtStatus.Text = $"Prog {progNo} Clean"; txtStatus.Foreground = new SolidColorBrush(Color.Parse("#166534")); }
        }
        else
        {
            // 建立簡易 Unified Diff 標記
            string diffText = GenerateSimpleUnifiedDiff(hScript, wScript, $"HEAD:Prog{progNo}", $"WORK:Prog{progNo}");
            if (txtDiff != null) txtDiff.Text = diffText;
            if (txtSummary != null) txtSummary.Text = $"Program {progNo} Modified";
            if (badgeStatus != null) { badgeStatus.Background = new SolidColorBrush(Color.Parse("#FEF2F2")); badgeStatus.BorderBrush = new SolidColorBrush(Color.Parse("#FECACA")); }
            if (txtStatus != null) { txtStatus.Text = $"Prog {progNo} Modified"; txtStatus.Foreground = new SolidColorBrush(Color.Parse("#DC2626")); }
        }
    }

    private void UpdateButtonsForWhole()
    {
        var txtMeld = this.FindControl<TextBlock>("TxtLaunchMeldLabel");
        if (txtMeld != null) txtMeld.Text = "呼叫 Meld 比對全專案";

        var btnSection = this.FindControl<Button>("BtnReloadSection");
        if (btnSection != null) btnSection.IsEnabled = false;

        var btnCompare = this.FindControl<Button>("BtnLoadToCompareSlot");
        if (btnCompare != null) btnCompare.IsEnabled = false;
    }

    private void UpdateButtonsForSection(int progNo)
    {
        var txtMeld = this.FindControl<TextBlock>("TxtLaunchMeldLabel");
        if (txtMeld != null) txtMeld.Text = $"呼叫 Meld 編輯 Program {progNo}";

        var txtSection = this.FindControl<TextBlock>("TxtReloadSectionLabel");
        if (txtSection != null) txtSection.Text = $"僅載入 Program {progNo}";

        var btnSection = this.FindControl<Button>("BtnReloadSection");
        if (btnSection != null) btnSection.IsEnabled = true;

        var btnCompare = this.FindControl<Button>("BtnLoadToCompareSlot");
        if (btnCompare != null) btnCompare.IsEnabled = true;
    }

    private async void OnLaunchMeldClick(object? sender, RoutedEventArgs e)
    {
        var cmb = this.FindControl<ComboBox>("CmbProgramSection");
        if (cmb == null || cmb.SelectedIndex <= 0)
        {
            // 全專案比對
            await LaunchMeldWholeProAsync();
        }
        else
        {
            var allKeys = _headPrograms.Keys.Union(_workPrograms.Keys).OrderBy(k => k).ToList();
            int progIndex = cmb.SelectedIndex - 1;
            if (progIndex >= 0 && progIndex < allKeys.Count)
            {
                int pNo = allKeys[progIndex];
                await LaunchMeldForSectionAsync(pNo);
            }
        }
    }

    private async Task LaunchMeldForSectionAsync(int progNo)
    {
        try
        {
            var hProg = _headPrograms.TryGetValue(progNo, out var hp) ? hp : new ProgramTable { ProgramNumber = progNo };
            var wProg = _workPrograms.TryGetValue(progNo, out var wp) ? wp : new ProgramTable { ProgramNumber = progNo };
            string name = wProg.Name ?? hProg.Name ?? $"Program_{progNo}";

            // 1. 前處理 (Preprocessing)：將 INI 區段解包為乾淨的腳本文字
            string hScript = ScriptSerializer.ProgramToCleanScript(hProg);
            string wScript = ScriptSerializer.ProgramToCleanScript(wProg);

            string tempDir = Path.Combine(Path.GetTempPath(), "ARCOSharp_Meld");
            Directory.CreateDirectory(tempDir);

            string headTempFile = Path.Combine(tempDir, $"HEAD_Program_{progNo:D2}_{name}.arcos");
            string workTempFile = Path.Combine(tempDir, $"WORK_Program_{progNo:D2}_{name}.arcos");

            await File.WriteAllTextAsync(headTempFile, hScript, Encoding.UTF8);
            await File.WriteAllTextAsync(workTempFile, wScript, Encoding.UTF8);

            // 2. 呼叫 Meld (優先使用 tools/MeldPortable/Meld.exe)
            string meldExe = ArcosPaths.MeldExecutable;
            var psi = new ProcessStartInfo
            {
                FileName = meldExe,
                Arguments = $"\"{headTempFile}\" \"{workTempFile}\"",
                WorkingDirectory = ArcosPaths.RootDirectory,
                UseShellExecute = true
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                _vm?.MainThreads[0].Log($"[Git] Launched Meld for Program {progNo} ({name}). Awaiting user edit...");
                await proc.WaitForExitAsync();

                // 3. 後處理 (Postprocessing)：讀取修改後純文字，自動重新編號與封裝
                if (File.Exists(workTempFile))
                {
                    string editedScript = await File.ReadAllTextAsync(workTempFile, Encoding.UTF8);
                    var updatedProg = ScriptSerializer.CleanScriptToProgram(editedScript, progNo, name);

                    // 局部寫回專案檔 (保留其他 0~99 區段完全不動)
                    var allProgs = File.Exists(_targetFilePath) ? ScriptSerializer.LoadProFile(_targetFilePath) : new List<ProgramTable>();
                    int existingIdx = allProgs.FindIndex(p => p.ProgramNumber == progNo);
                    if (existingIdx >= 0) allProgs[existingIdx] = updatedProg;
                    else allProgs.Add(updatedProg);

                    ScriptSerializer.SaveProFile(_targetFilePath, allProgs);

                    // 局部更新 VM
                    _vm?.LoadProgram(progNo, updatedProg);
                    _vm?.MainThreads[0].Log($"[Git] Meld merge completed for Program {progNo}. Saved to '{_targetFilePath}' and reloaded to Slot {progNo}.");

                    ReloadRequested = true;
                    ReloadProgNo = progNo;

                    // 刷新視窗資料
                    AnalyzeAndLoadSections();
                }
            }
        }
        catch (Exception ex)
        {
            _vm?.MainThreads[0].Log($"[Git] Failed to run Meld: {ex.Message}", isError: true);
        }
    }

    private async Task LaunchMeldWholeProAsync()
    {
        try
        {
            string repoRoot = ArcosPaths.RootDirectory;
            string relPath = Path.GetRelativePath(repoRoot, _targetFilePath).Replace('\\', '/');

            // 匯出 HEAD 全檔案暫存
            string headProText = RunGitCommand($"show HEAD:\"{relPath}\"");
            string tempDir = Path.Combine(Path.GetTempPath(), "ARCOSharp_Meld");
            Directory.CreateDirectory(tempDir);
            string headTempFile = Path.Combine(tempDir, $"HEAD_{Path.GetFileName(_targetFilePath)}");
            await File.WriteAllTextAsync(headTempFile, headProText, Encoding.UTF8);

            string meldExe = ArcosPaths.MeldExecutable;
            var psi = new ProcessStartInfo
            {
                FileName = meldExe,
                Arguments = $"\"{headTempFile}\" \"{_targetFilePath}\"",
                WorkingDirectory = repoRoot,
                UseShellExecute = true
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                _vm?.MainThreads[0].Log($"[Git] Launched Meld for entire project. Awaiting edit...");
                await proc.WaitForExitAsync();

                ReloadRequested = true;
                ReloadProgNo = null; // 全專案重新載入
                AnalyzeAndLoadSections();
            }
        }
        catch (Exception ex)
        {
            _vm?.MainThreads[0].Log($"[Git] Failed to launch Meld for whole project: {ex.Message}", isError: true);
        }
    }

    private void OnReloadSectionClick(object? sender, RoutedEventArgs e)
    {
        var cmb = this.FindControl<ComboBox>("CmbProgramSection");
        if (cmb == null || cmb.SelectedIndex <= 0) return;

        var allKeys = _headPrograms.Keys.Union(_workPrograms.Keys).OrderBy(k => k).ToList();
        int progIndex = cmb.SelectedIndex - 1;
        if (progIndex >= 0 && progIndex < allKeys.Count)
        {
            int pNo = allKeys[progIndex];
            if (_workPrograms.TryGetValue(pNo, out var prog))
            {
                _vm?.LoadProgram(pNo, prog);
                _vm?.MainThreads[0].Log($"[Git] Reloaded Program {pNo} from disk into VM slot {pNo}.");
            }
            ReloadRequested = true;
            ReloadProgNo = pNo;
            Close();
        }
    }

    private void OnLoadToCompareSlotClick(object? sender, RoutedEventArgs e)
    {
        var cmb = this.FindControl<ComboBox>("CmbProgramSection");
        if (cmb == null || cmb.SelectedIndex <= 0) return;

        var allKeys = _headPrograms.Keys.Union(_workPrograms.Keys).OrderBy(k => k).ToList();
        int progIndex = cmb.SelectedIndex - 1;
        if (progIndex >= 0 && progIndex < allKeys.Count)
        {
            int pNo = allKeys[progIndex];
            if (_workPrograms.TryGetValue(pNo, out var prog))
            {
                var compareProg = new ProgramTable { ProgramNumber = 90, Name = $"Compare_P{pNo}" };
                for (int i = 1; i <= prog.Count; i++)
                {
                    compareProg.Add(prog[i]);
                }
                _vm?.LoadProgram(90, compareProg);
                _vm?.MainThreads[0].Log($"[Git] Loaded Program {pNo} copy into Staging Slot 90 for A/B testing.");
            }
        }
    }

    private void OnReloadAllClick(object? sender, RoutedEventArgs e)
    {
        ReloadRequested = true;
        ReloadProgNo = null;
        Close();
    }

    private string RunGitCommand(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = ArcosPaths.RootDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var proc = Process.Start(psi);
        if (proc == null) return string.Empty;
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return output;
    }

    private string GenerateSimpleUnifiedDiff(string textA, string textB, string labelA, string labelB)
    {
        var linesA = textA.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var linesB = textB.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var sb = new StringBuilder();
        sb.AppendLine($"--- {labelA}");
        sb.AppendLine($"+++ {labelB}");

        int max = Math.Max(linesA.Length, linesB.Length);
        for (int i = 0; i < max; i++)
        {
            string a = i < linesA.Length ? linesA[i] : null!;
            string b = i < linesB.Length ? linesB[i] : null!;

            if (a == b)
            {
                sb.AppendLine($"  {a}");
            }
            else
            {
                if (a != null) sb.AppendLine($"- {a}");
                if (b != null) sb.AppendLine($"+ {b}");
            }
        }
        return sb.ToString();
    }
}
