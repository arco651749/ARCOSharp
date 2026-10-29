using Avalonia;
using Avalonia.Media;
using System.IO;

using Avalonia.Controls;

using Avalonia.Interactivity;

using Avalonia.Platform.Storage;

using ARCOS.Core.Csvi;

using ARCOS.CsviStudio.Services;



namespace ARCOS.CsviStudio.Views;



public partial class MainWindow : Window

{

    private string? _currentFilePath;

    private CsviManifest _currentManifest = new();

    private string? _password;

    private readonly CsviCompilerService _compiler = new();

    private readonly SandboxExecutor _sandbox = new();



    public MainWindow() : this(null) { }



    public MainWindow(string? initialFilePath)

    {

        InitializeComponent();

        Position = new Avalonia.PixelPoint(1930, -670);

        SourceTabs.SelectionChanged += (s, e) =>
        {
            if (SourceTabs.SelectedItem is TabItem ti)
            {
                string h = ti.Header?.ToString() ?? "";
                if (h.Contains("Wizard")) UpdateXamlPreview(WizardEditor.Text);
                else if (h.Contains("Panel")) UpdateXamlPreview(PanelEditor.Text);
            }
        };

        WizardEditor.TextChanged += (s, e) => UpdateXamlPreview(WizardEditor.Text);
        PanelEditor.TextChanged += (s, e) => UpdateXamlPreview(PanelEditor.Text);

        if (string.IsNullOrEmpty(initialFilePath))
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            initialFilePath = cmdArgs
                .Select(a => a.Trim('"').Trim())
                .FirstOrDefault(a => !a.StartsWith("-") && a.EndsWith(".csvi", StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrEmpty(initialFilePath))
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "MockIO.csvi"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MODULES", "IO", "DRIVER", "MockIO", "MockIO.csvi"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "MODULES", "IO", "DRIVER", "MockIO", "MockIO.csvi"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MODULES", "IO", "DRIVER", "MockIO", "MockIO.csvi"),
                @"D:\AntiGravity\ARCOSharp\src\MODULES\IO\DRIVER\MockIO\MockIO.csvi"
            };
            initialFilePath = candidates.FirstOrDefault(File.Exists);
        }

        if (!string.IsNullOrEmpty(initialFilePath) && File.Exists(initialFilePath))
        {
            LoadFile(Path.GetFullPath(initialFilePath));
            return;
        }

        LoadDefaultTemplate();

    }



    private void LoadDefaultTemplate()

    {

        _currentFilePath = null;

        _currentManifest = new CsviManifest

        {

            Module = "IO",

            CommandKey = "IO@SetPort",

            ComponentType = CsviComponentType.Command,

            PackagingMode = CsviPackagingMode.PortableOpen

        };



        CurrentFileBlock.Text = "New CSVI Component (Unsaved)";

        ModeCombo.SelectedIndex = 0;



        LogicEditor.Text = @"using System;

using ARCOS.Core.Execution;



namespace ARCOS.Module.IO;



public class IoSetPortHandler : IModuleHandler

{

    public string ModuleName => ""IO"";



    public bool Execute(string commandKey, string parameter, ExecutionContext context, CommandState state)

    {

        // 微秒級高速執行

        if (string.IsNullOrWhiteSpace(parameter)) return true;



        var parts = parameter.Split(',');

        string port = parts[0].Trim();

        bool val = parts.Length > 1 && (parts[1].Trim() == ""1"" || parts[1].Trim().Equals(""True"", StringComparison.OrdinalIgnoreCase));



        context.IO.SetPort(port, val);

        return true;

    }

}

";



        WizardEditor.Text = @"<UserControl xmlns=""https://github.com/avaloniaui""

             xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">

    <StackPanel Margin=""10"" Spacing=""8"">

        <TextBlock Text=""Port Name:"" FontWeight=""Bold""/>

        <TextBox Text=""P0.0""/>

        <TextBlock Text=""Value:"" FontWeight=""Bold""/>

        <CheckBox Content=""Output ON (True)"" IsChecked=""True""/>

    </StackPanel>

</UserControl>

";



        PanelEditor.Text = @"<UserControl xmlns=""https://github.com/avaloniaui""

             xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">

    <Border Background=""#252526"" CornerRadius=""4"" Padding=""10"">

        <TextBlock Text=""IO Front Panel Active"" Foreground=""#4EC9B0"" HorizontalAlignment=""Center""/>

    </Border>

</UserControl>

";



        HistoryViewer.Text = "Rev 1: Initial Created template";

        AppendLog("Loaded standard IO@SetPort CSVI template.");

    }



    private void LoadFile(string path)

    {

        try

        {

            _currentFilePath = path;

            byte[] bytes = File.ReadAllBytes(path);

            var pkg = CsviPackage.LoadFromBytes(bytes, _password);



            _currentManifest = pkg.Manifest;

            CurrentFileBlock.Text = Path.GetFileName(path) + " (" + path + ")";

            ModeCombo.SelectedIndex = (int)pkg.Manifest.PackagingMode;



            if (pkg.EmbeddedSourceFiles.TryGetValue("Logic.cs", out var logic))

            {

                LogicEditor.Text = logic;

            }

            if (pkg.EmbeddedSourceFiles.TryGetValue("Wizard.axaml", out var wiz))

            {

                WizardEditor.Text = wiz;
            UpdateXamlPreview(wiz);

            }

            if (pkg.EmbeddedSourceFiles.TryGetValue("Panel.axaml", out var pan))

            {

                PanelEditor.Text = pan;

            }



            // History

            var lines = pkg.Manifest.RevisionHistory.Select(r => $"[Rev {r.Revision}] {r.TimestampUtc} by {r.Author}: {r.Summary}");

            HistoryViewer.Text = string.Join(Environment.NewLine, lines);



            AppendLog($"Successfully opened {Path.GetFileName(path)} (Mode: {pkg.Manifest.PackagingMode}, Size: {bytes.Length} bytes)");

        }

        catch (Exception ex)

        {

            AppendLog("Failed to open CSVI: " + ex.Message);

        }

    }



    private async void OnOpenClick(object? sender, RoutedEventArgs e)

    {

        var topLevel = TopLevel.GetTopLevel(this);

        if (topLevel == null) return;



        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions

        {

            Title = "Open CSVI Instrument",

            AllowMultiple = false,

            FileTypeFilter = new[]

            {

                new FilePickerFileType("CSVI Virtual Instrument") { Patterns = new[] { "*.csvi", "*.dll" } }

            }

        });



        if (files.Count > 0)

        {

            string path = files[0].Path.LocalPath;

            LoadFile(path);

        }

    }



    private void OnNewClick(object? sender, RoutedEventArgs e)

    {

        LoadDefaultTemplate();

    }



    private async void OnBuildClick(object? sender, RoutedEventArgs e)

    {

        if (string.IsNullOrEmpty(_currentFilePath))

        {

            var topLevel = TopLevel.GetTopLevel(this);

            if (topLevel == null) return;



            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions

            {

                Title = "Save CSVI Component",

                DefaultExtension = "csvi",

                SuggestedFileName = "SetPort.csvi"

            });



            if (file == null) return;

            _currentFilePath = file.Path.LocalPath;

        }



        var sources = new Dictionary<string, string>

        {

            ["Logic.cs"] = LogicEditor.Text ?? string.Empty,

            ["Wizard.axaml"] = WizardEditor.Text ?? string.Empty,

            ["Panel.axaml"] = PanelEditor.Text ?? string.Empty

        };



        var mode = (CsviPackagingMode)ModeCombo.SelectedIndex;

        _currentManifest.PackagingMode = mode;



        var compRes = _compiler.Compile(Path.GetFileNameWithoutExtension(_currentFilePath), sources, _currentManifest, _password);

        if (compRes.Success)

        {

            File.WriteAllBytes(_currentFilePath, compRes.AssemblyBytes);

            AppendLog($"✔ Roslyn Compilation Succeeded in {compRes.ElapsedMilliseconds} ms -> {_currentFilePath} ({compRes.AssemblyBytes.Length} bytes)");

            CurrentFileBlock.Text = Path.GetFileName(_currentFilePath) + " (" + _currentFilePath + ")";

        }

        else

        {

            AppendLog("❌ Compilation Failed:\n" + string.Join("\n", compRes.Errors));

        }

    }



    private void OnSetPasswordClick(object? sender, RoutedEventArgs e)

    {

        AppendLog("Password protection mode configured.");

    }



    private void OnExportSourceClick(object? sender, RoutedEventArgs e)

    {

        if (!string.IsNullOrEmpty(_currentFilePath))

        {

            string outDir = Path.Combine(Path.GetDirectoryName(_currentFilePath)!, Path.GetFileNameWithoutExtension(_currentFilePath) + "_Source");

            Directory.CreateDirectory(outDir);

            File.WriteAllText(Path.Combine(outDir, "Logic.cs"), LogicEditor.Text ?? "");

            File.WriteAllText(Path.Combine(outDir, "Wizard.axaml"), WizardEditor.Text ?? "");

            File.WriteAllText(Path.Combine(outDir, "Panel.axaml"), PanelEditor.Text ?? "");

            AppendLog($"Exported source files to: {outDir}");

        }

    }



    private void OnRunSandboxClick(object? sender, RoutedEventArgs e)

    {

        var sources = new Dictionary<string, string>

        {

            ["Logic.cs"] = LogicEditor.Text ?? string.Empty,

            ["Wizard.axaml"] = WizardEditor.Text ?? string.Empty

        };



        var comp = _compiler.Compile("SandboxTestAssembly", sources, _currentManifest, _password);

        if (!comp.Success)

        {

            SandboxOutputBlock.Text = "Compilation failed:\n" + string.Join("\n", comp.Errors);

            return;

        }



        string param = TestParamBox.Text ?? "";

        string cmdKey = !string.IsNullOrEmpty(_currentManifest.CommandKey) ? _currentManifest.CommandKey : "IO@SetPort";



        var run = _sandbox.Execute(comp.AssemblyBytes, cmdKey, param);

        if (run.Success)

        {

            SandboxOutputBlock.Text = $"Status: SUCCESS\nOutput: {run.Output}\nElapsed: {run.ElapsedMilliseconds:F4} ms";

        }

        else

        {

            SandboxOutputBlock.Text = $"Status: FAILED\nError: {run.Error}\nElapsed: {run.ElapsedMilliseconds:F4} ms";

        }

    }



    private void AppendLog(string message)

    {

        string timestamp = DateTime.Now.ToString("HH:mm:ss");

        DiagnosticsBox.Text += $"[{timestamp}] {message}\n";

    }


    private void UpdateXamlPreview(string? xamlText)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(xamlText)) return;
            var control = Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Parse<Control>(xamlText);
            XamlPreviewHost.Child = control;
        }
        catch (Exception ex)
        {
            XamlPreviewHost.Child = new TextBlock
            {
                Text = "XAML Preview Error:\n" + ex.Message,
                Foreground = Brushes.Crimson,
                Margin = new Thickness(10),
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

}
