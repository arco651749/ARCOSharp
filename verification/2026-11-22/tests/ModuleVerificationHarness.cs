using System.Text.Json;
using System.Text.RegularExpressions;
using ARCOS.Core.Csvi;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Core.Tests.ModuleVerification;

public sealed class ModuleVerificationHarness
{
    public IoEngine Io { get; }
    public VariableEngine Variable { get; }
    public PointEngine Point { get; }
    public ImageEngine Image { get; }
    public DaqEngine Daq { get; }
    public TimerEngine Timer { get; }
    public BasicEngines Engines { get; }
    public DriverManager Drivers { get; }
    public ModuleRegistry Registry { get; }
    public CommandExecutor Executor { get; }
    public string DistModulesDir { get; }

    public ModuleVerificationHarness()
    {
        Io = new IoEngine();
        Variable = new VariableEngine();
        Point = new PointEngine(Io);
        Image = new ImageEngine(Io);
        Daq = new DaqEngine(Io);
        Timer = new TimerEngine(Io);
        Engines = new BasicEngines(Io, Variable, Point, Image, Daq, Timer);
        Drivers = new DriverManager(Engines);

        DistModulesDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES"));

        Registry = ModuleRegistry.CreateDefault();
        if (Directory.Exists(DistModulesDir))
        {
            Registry.LoadExternalModules(DistModulesDir);
        }
        Executor = new CommandExecutor(Registry);
    }

    public (ExecutionContext Ctx, List<CommandResult> Results) RunScript(string script, int maxSteps = 50)
    {
        var program = ProgramTable.FromText(script, 0, "VerificationProg");
        var ctx = new ExecutionContext(0, "VerifyThread", program, Engines, Drivers);
        var results = new List<CommandResult>();
        for (int i = 0; i < maxSteps && ctx.ProgIndex <= program.Count; i++)
        {
            var res = Executor.ExecuteStep(ctx);
            results.Add(res);
            if (res.Status == CommandStatus.Error)
                break;
        }
        return (ctx, results);
    }

    public CsviGuideAuditResult AuditCsviGuideUi(string moduleFolder, string csviFileName)
    {
        string path = Path.Combine(DistModulesDir, moduleFolder, csviFileName);
        if (!File.Exists(path))
        {
            return new CsviGuideAuditResult(csviFileName, false, false, false, "CSVI file not found in dist/ARCOS_Runtime", "");
        }

        var pkg = CsviPackage.Load(path);
        bool hasManifestSchema = pkg.Manifest?.Schema != null;
        string allCode = string.Join("\n", pkg.EmbeddedSourceFiles.Values);
        bool hasAxaml = pkg.EmbeddedSourceFiles.Keys.Any(k => k.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                     || pkg.EmbeddedRuntimeXaml.Count > 0;

        // Check whether Guide UI formats v2.0 syntax (uses CommandFormatter / FormatToV2 / "::" / "=")
        bool usesV2Formatter = allCode.Contains("CommandFormatter", StringComparison.Ordinal)
                            || allCode.Contains("FormatToV2", StringComparison.Ordinal)
                            || allCode.Contains(" :: ", StringComparison.Ordinal);

        // Extract sample output assembly line from Guide UI code if present
        string sampleLine = "";
        foreach (var kv in pkg.EmbeddedSourceFiles)
        {
            if (!kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(kv.Value, @"(CommandFormatter\.|FormatToV2|ResultCommand|ParameterString|BuildCommand)[^\r\n]{0,120}");
            if (m.Success)
            {
                sampleLine = m.Value.Trim();
                break;
            }
        }

        return new CsviGuideAuditResult(
            csviFileName,
            true,
            hasManifestSchema,
            hasAxaml,
            usesV2Formatter ? "V2_READY" : "CHECK_LEGACY_FORMAT",
            sampleLine);
    }

    public List<CsviGuideAuditResult> AuditAllCsvisInModule(string moduleFolder)
    {
        string dir = Path.Combine(DistModulesDir, moduleFolder);
        var list = new List<CsviGuideAuditResult>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.GetFiles(dir, "*.csvi").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(file);
            string stem = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(stem, moduleFolder, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(AuditCsviGuideUi(moduleFolder, name));
        }
        return list;
    }
}

public sealed record CsviGuideAuditResult(
    string FileName,
    bool Exists,
    bool HasManifestSchema,
    bool HasAxamlGuideUi,
    string GuideOutputStatus,
    string SampleCodeSnippet);
