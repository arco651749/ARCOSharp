using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ARCOS.Core.Common;
using ARCOS.Core.Csvi;
using ARCOS.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

/// <summary>
/// Layer 2 Contract Verification:
/// Audits ALL 14 modules' CSVI Guide UIs &amp; StandardCommandSchemas against the v2.0 Parser / Validator / Formatter / SyntaxBridge.
/// Writes full findings to verification/2026-11-22/reports/15_CSVI_GUIDE_UI_CONTRACT_AUDIT.md.
/// </summary>
public sealed class CsviGuideUiContractTests
{
    private static readonly string[] OfficialModules =
    {
        "SYSTEM", "THREAD", "VARIABLE", "IO", "MOTION", "CAPTURE", "DAQ",
        "POINT&PATH", "USER", "PYTHON", "REDIS", "MULTISTATION", "AIAGENT", "CALL"
    };

    private static readonly Regex FindControlRegex = new(
        @"\bFind(?:Control)?\s*<\s*[^>]+>\s*\(\s*""([^""]+)""\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex ParamKeyAccessRegex = new(
        @"(?:\.(?:Get|TryGet|GetBool|GetInt|GetDouble|GetList)\s*\(\s*""([A-Za-z_][A-Za-z0-9_]*)""|\.(?:AllArgs|RequiredArgs|OptionalArgs|OutputArgs)\s*\[\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*\])",
        RegexOptions.Compiled);

    private readonly ITestOutputHelper _output;

    public CsviGuideUiContractTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record CsviContractRow(
        string Module,
        string FileName,
        string ExpectedKey,
        string ManifestKey,
        string GuidanceKey,
        bool LoadedOk,
        bool HasGuidanceType,
        bool HasStdSchema,
        bool HasManifestSchema,
        bool HasRuntimeGetSchema,
        string SchemaDrift,
        bool HasAxaml,
        bool AxamlValidXml,
        string? AxamlError,
        List<string> DanglingFindControls,
        List<string> UnknownParamKeysInCode,
        string ReopenParseMode,
        string EmitMode,
        string EmitSnippet,
        bool RegisteredInModuleRegistry,
        string? LoadError);

    private sealed record BridgeRoundTripRow(
        string CommandKey,
        string Module,
        string SyntheticV2,
        bool ParseOk,
        bool ValidateOk,
        bool FormatIdempotent,
        bool NormalizeOk,
        bool BridgeRoundTripOk,
        string NormalizedLegacy,
        string RoundTripV2,
        string Details);

    [Fact]
    public void Contract_All256Csvis_And_AllStandardSchemas_FullAudit()
    {
        var harness = new ModuleVerificationHarness();
        Assert.True(Directory.Exists(harness.DistModulesDir), $"DistModulesDir missing: {harness.DistModulesDir}");

        var allRegisteredCommands = new HashSet<string>(
            harness.Registry.GetAllCommands("*"),
            StringComparer.OrdinalIgnoreCase);

        var csviRows = new List<CsviContractRow>();
        var modulePanels = new List<(string Module, string FileName, bool LoadedOk, bool HasFrontPanel, string? Error)>();

        foreach (string mod in OfficialModules)
        {
            string modDir = Path.Combine(harness.DistModulesDir, mod);
            if (!Directory.Exists(modDir)) continue;

            foreach (string csviPath in Directory.GetFiles(modDir, "*.csvi").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(csviPath);
                string stem = Path.GetFileNameWithoutExtension(csviPath);

                // Module-level panel (e.g., System.csvi, IO.csvi, Motion.csvi, Redis.csvi)
                if (stem.Equals(mod, StringComparison.OrdinalIgnoreCase) ||
                    (mod == "POINT&PATH" && stem.Equals("PointPath", StringComparison.OrdinalIgnoreCase)) ||
                    (mod == "POINT&PATH" && stem.Equals("Point&Path", StringComparison.OrdinalIgnoreCase)) ||
                    (mod == "AIAGENT" && stem.Equals("AIAgent", StringComparison.OrdinalIgnoreCase)))
                {
                    CsviPackage? panelPkg = null;
                    try
                    {
                        panelPkg = CsviPackage.Load(csviPath);
                        bool hasPanel = panelPkg.EmbeddedRuntimeXaml.Count > 0 ||
                                        panelPkg.EmbeddedSourceFiles.Keys.Any(k => k.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));
                        modulePanels.Add((mod, fileName, true, hasPanel, null));
                    }
                    catch (Exception ex)
                    {
                        modulePanels.Add((mod, fileName, false, false, ex.GetBaseException().Message));
                    }
                    finally
                    {
                        panelPkg?.Unload();
                    }
                    continue;
                }

                csviRows.Add(AuditSingleCommandCsvi(mod, csviPath, allRegisteredCommands));
            }
        }

        // Part 2: StandardCommandSchemas v2 -> Parse -> Validate -> Format -> NormalizeForExecution -> FormatToV2
        var bridgeRows = new List<BridgeRoundTripRow>();
        var seenSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in StandardCommandSchemas.All.Values)
        {
            if (!seenSchemas.Add(schema.CommandKey)) continue;
            bridgeRows.Add(RunBridgeRoundTrip(schema));
        }

        // Write Markdown Audit Report
        string reportPath = Path.GetFullPath(Path.Combine(
            harness.DistModulesDir, "..", "..", "..", "verification", "2026-11-22", "reports",
            "15_CSVI_GUIDE_UI_CONTRACT_AUDIT.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, BuildMarkdownReport(csviRows, modulePanels, bridgeRows), new UTF8Encoding(false));

        // Emit concise summary to test output
        int totalCsvi = csviRows.Count;
        int loadFail = csviRows.Count(r => !r.LoadedOk);
        int missingGuidance = csviRows.Count(r => r.LoadedOk && !r.HasGuidanceType);
        int invalidXml = csviRows.Count(r => r.HasAxaml && !r.AxamlValidXml);
        int danglingControls = csviRows.Count(r => r.DanglingFindControls.Count > 0);
        int schemaDriftCount = csviRows.Count(r => !string.IsNullOrEmpty(r.SchemaDrift));
        int missingStdSchema = csviRows.Count(r => !r.HasStdSchema);
        int missingManifestSchema = csviRows.Count(r => !r.HasManifestSchema);
        int legacyOnlyReopen = csviRows.Count(r => r.ReopenParseMode == "LEGACY_ONLY");
        int legacyOnlyEmit = csviRows.Count(r => r.EmitMode == "LEGACY_RAW");
        int unregistered = csviRows.Count(r => !r.RegisteredInModuleRegistry);

        int totalBridge = bridgeRows.Count;
        int bridgeParseFail = bridgeRows.Count(b => !b.ParseOk);
        int bridgeValFail = bridgeRows.Count(b => !b.ValidateOk);
        int bridgeFmtFail = bridgeRows.Count(b => !b.FormatIdempotent);
        int bridgeNormFail = bridgeRows.Count(b => !b.NormalizeOk);
        int bridgeRtFail = bridgeRows.Count(b => !b.BridgeRoundTripOk);

        _output.WriteLine("=== LAYER 2 CSVI GUIDE UI CONTRACT AUDIT SUMMARY ===");
        _output.WriteLine($"Report: {reportPath}");
        _output.WriteLine($"Command CSVIs audited: {totalCsvi} (Module panels: {modulePanels.Count})");
        _output.WriteLine($"  LoadFail={loadFail}, MissingGuidance={missingGuidance}, InvalidAxamlXml={invalidXml}, DanglingFindControl={danglingControls}");
        _output.WriteLine($"  MissingStdSchema={missingStdSchema}, MissingManifestSchema={missingManifestSchema}, SchemaDrift={schemaDriftCount}");
        _output.WriteLine($"  ReopenParse: V2={csviRows.Count(r => r.ReopenParseMode == "V2_AWARE")}, LEGACY_ONLY={legacyOnlyReopen}, NO_PARAM={csviRows.Count(r => r.ReopenParseMode == "NO_PARAM")}");
        _output.WriteLine($"  EmitMode: V2={csviRows.Count(r => r.EmitMode == "V2_FORMATTED")}, LEGACY_RAW={legacyOnlyEmit}, NO_PARAM={csviRows.Count(r => r.EmitMode == "NO_PARAM")}");
        _output.WriteLine($"  UnregisteredInRegistry={unregistered}");
        _output.WriteLine($"StandardCommandSchemas Bridge Round-Trip audited: {totalBridge}");
        _output.WriteLine($"  ParseFail={bridgeParseFail}, ValidateFail={bridgeValFail}, FormatNonIdempotent={bridgeFmtFail}, NormalizeFail={bridgeNormFail}, BridgeRoundTripLoss={bridgeRtFail}");

        // Hard structural & contract invariants (0 regressions allowed)
        Assert.Equal(0, loadFail);
        Assert.Equal(0, missingGuidance);
        Assert.Equal(0, invalidXml);
        Assert.Equal(0, danglingControls);
        Assert.Equal(0, schemaDriftCount);
        Assert.Equal(0, unregistered);
        Assert.Equal(0, bridgeParseFail);
        Assert.Equal(0, bridgeValFail);
        Assert.Equal(0, bridgeFmtFail);
        Assert.Equal(0, bridgeNormFail);
        Assert.Equal(0, bridgeRtFail);
    }

    private static CsviContractRow AuditSingleCommandCsvi(string mod, string csviPath, HashSet<string> allRegistered)
    {
        string fileName = Path.GetFileName(csviPath);
        string stem = Path.GetFileNameWithoutExtension(csviPath);
        string expectedKey = $"{mod}@{stem}";

        CsviPackage? pkg = null;
        try
        {
            pkg = CsviPackage.Load(csviPath);
            string manifestKey = pkg.Manifest?.CommandKey ?? string.Empty;
            string lookupKey = !string.IsNullOrWhiteSpace(manifestKey) ? manifestKey : expectedKey;

            // Find IParameterGuidance implementation
            IParameterGuidance? guidance = null;
            string guidanceKey = string.Empty;
            if (pkg.LoadedAssembly != null)
            {
                foreach (var t in pkg.LoadedAssembly.GetTypes())
                {
                    if (typeof(IParameterGuidance).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                    {
                        if (Activator.CreateInstance(t) is IParameterGuidance g)
                        {
                            guidance = g;
                            guidanceKey = g.CommandKey ?? string.Empty;
                            break;
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(guidanceKey))
            {
                lookupKey = guidanceKey;
            }

            var stdSchema = StandardCommandSchemas.Get(lookupKey) ?? StandardCommandSchemas.Get(expectedKey);
            var mfSchema = pkg.Manifest?.Schema;
            var rtSchema = guidance?.GetSchema();

            string drift = CompareSchemas(stdSchema, mfSchema, rtSchema);

            // AXAML analysis + FindControl <-> x:Name wiring check
            var axamlSources = new List<(string Key, string Content)>();
            foreach (var kv in pkg.EmbeddedRuntimeXaml)
                axamlSources.Add((kv.Key, kv.Value));
            foreach (var kv in pkg.EmbeddedSourceFiles)
            {
                if (kv.Key.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    axamlSources.Add((kv.Key, kv.Value));
                }
            }

            bool hasAxaml = axamlSources.Count > 0;
            bool axamlValid = true;
            string? axamlError = null;
            var axamlNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (xKey, xaml) in axamlSources)
            {
                try
                {
                    var doc = XDocument.Parse(xaml.TrimStart('\uFEFF'));
                    foreach (var el in doc.Descendants())
                    {
                        foreach (var attr in el.Attributes())
                        {
                            if (attr.Name.LocalName == "Name" && !string.IsNullOrWhiteSpace(attr.Value))
                            {
                                axamlNames.Add(attr.Value.Trim());
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    axamlValid = false;
                    axamlError ??= $"{xKey}: {ex.Message}";
                }
            }

            var csFiles = pkg.EmbeddedSourceFiles
                .Where(kv => kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .ToList();
            string allCs = string.Join("\n", csFiles);

            var dangling = new List<string>();
            if (hasAxaml && axamlValid)
            {
                foreach (Match m in FindControlRegex.Matches(allCs))
                {
                    string ctrlName = m.Groups[1].Value;
                    if (!axamlNames.Contains(ctrlName) && !dangling.Contains(ctrlName))
                    {
                        dangling.Add(ctrlName);
                    }
                }
            }

            // C6: Check every parsed parameter key referenced in embedded C# against CommandSchema
            var unknownKeys = new List<string>();
            if (stdSchema != null)
            {
                foreach (Match m in ParamKeyAccessRegex.Matches(allCs))
                {
                    string k = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
                    if (string.IsNullOrEmpty(k)) continue;
                    // Ignore VariableEngine/Engine calls like Variable.Get("...") by checking preceding receiver in match context
                    int start = Math.Max(0, m.Index - 24);
                    string prefix = allCs.Substring(start, m.Index - start);
                    if (prefix.Contains("Variable", StringComparison.OrdinalIgnoreCase) ||
                        prefix.Contains("Io", StringComparison.OrdinalIgnoreCase) ||
                        prefix.Contains("Timer", StringComparison.OrdinalIgnoreCase) ||
                        prefix.Contains("Point", StringComparison.OrdinalIgnoreCase) ||
                        prefix.Contains("Image", StringComparison.OrdinalIgnoreCase) ||
                        prefix.Contains("Daq", StringComparison.OrdinalIgnoreCase) ||
                        prefix.Contains("Config", StringComparison.OrdinalIgnoreCase) ||
                        prefix.Contains("Properties", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (stdSchema.FindParam(k) == null && !unknownKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
                    {
                        unknownKeys.Add(k);
                    }
                }
            }

            // Re-open parsing capability & output emit mode classification
            bool isNoParam = (stdSchema != null && stdSchema.Parameters.Count == 0);
            string reopenMode;
            if (isNoParam)
            {
                reopenMode = "NO_PARAM";
            }
            else if (allCs.Contains("CommandParamParser.Parse", StringComparison.Ordinal) ||
                     allCs.Contains("NormalizeForExecution", StringComparison.Ordinal) ||
                     allCs.Contains("CommandSyntaxBridge", StringComparison.Ordinal))
            {
                reopenMode = "V2_AWARE";
            }
            else
            {
                reopenMode = "LEGACY_ONLY";
            }

            string emitMode;
            if (isNoParam)
            {
                emitMode = "NO_PARAM";
            }
            else if (allCs.Contains("FormatToV2", StringComparison.Ordinal) ||
                     allCs.Contains("CommandFormatter.", StringComparison.Ordinal) ||
                     Regex.IsMatch(allCs, @"FormatParameter\s*\("))
            {
                emitMode = "V2_FORMATTED";
            }
            else
            {
                emitMode = "LEGACY_RAW";
            }

            string emitSnippet = "";
            var emitMatch = Regex.Match(allCs, @"(ResultParam|ResultParameter|BuildParam|BuildCommand|FormatToV2|TrySetResult|station\s*=|Close\s*\()[^\r\n]{0,160}");
            if (emitMatch.Success) emitSnippet = emitMatch.Value.Trim();

            bool registered = allRegistered.Contains(lookupKey) || allRegistered.Contains(expectedKey);

            return new CsviContractRow(
                mod, fileName, expectedKey, manifestKey, guidanceKey,
                true, guidance != null,
                stdSchema != null, mfSchema != null, rtSchema != null,
                drift,
                hasAxaml, axamlValid, axamlError, dangling, unknownKeys,
                reopenMode, emitMode, emitSnippet, registered, null);
        }
        catch (Exception ex)
        {
            return new CsviContractRow(
                mod, fileName, expectedKey, "", "",
                false, false, false, false, false,
                "", false, false, null, new List<string>(), new List<string>(),
                "ERR", "ERR", "", false, ex.GetBaseException().Message);
        }
        finally
        {
            pkg?.Unload();
        }
    }

    private static string CompareSchemas(CommandSchema? std, CommandSchema? mf, CommandSchema? rt)
    {
        var issues = new List<string>();
        if (std != null && mf != null)
        {
            var stdNames = std.Parameters.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var mfNames = mf.Parameters.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (!stdNames.SequenceEqual(mfNames, StringComparer.OrdinalIgnoreCase))
            {
                var onlyStd = stdNames.Except(mfNames, StringComparer.OrdinalIgnoreCase).ToList();
                var onlyMf = mfNames.Except(stdNames, StringComparer.OrdinalIgnoreCase).ToList();
                issues.Add($"Std-vs-Manifest params mismatch (onlyStd=[{string.Join(",", onlyStd)}], onlyManifest=[{string.Join(",", onlyMf)}])");
            }
        }
        if (std != null && rt != null)
        {
            var stdNames = std.Parameters.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var rtNames = rt.Parameters.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (!stdNames.SequenceEqual(rtNames, StringComparer.OrdinalIgnoreCase))
            {
                var onlyStd = stdNames.Except(rtNames, StringComparer.OrdinalIgnoreCase).ToList();
                var onlyRt = rtNames.Except(stdNames, StringComparer.OrdinalIgnoreCase).ToList();
                issues.Add($"Std-vs-GetSchema params mismatch (onlyStd=[{string.Join(",", onlyStd)}], onlyGetSchema=[{string.Join(",", onlyRt)}])");
            }
        }
        return string.Join("; ", issues);
    }

    private static BridgeRoundTripRow RunBridgeRoundTrip(CommandSchema schema)
    {
        string mod = schema.CommandKey.Contains('@') ? schema.CommandKey.Split('@')[0] : "UNKNOWN";
        string syntheticV2 = BuildSyntheticV2Param(schema, out var expectedRequired);

        var parsed = CommandParamParser.Parse(syntheticV2, schema);
        bool parseOk = !parsed.HasSyntaxError;

        var validation = CommandValidator.Validate(syntheticV2, schema);
        bool validateOk = validation.IsValid;

        string formatted1 = CommandFormatter.FormatParameter(parsed, schema);
        var parsed2 = CommandParamParser.Parse(formatted1, schema);
        string formatted2 = CommandFormatter.FormatParameter(parsed2, schema);
        bool fmtIdempotent = !parsed2.HasSyntaxError && string.Equals(formatted1, formatted2, StringComparison.Ordinal);

        string normalized = "";
        bool normOk = true;
        string normErr = "";
        try
        {
            normalized = CommandSyntaxBridge.NormalizeForExecution(schema.CommandKey, syntheticV2);
        }
        catch (Exception ex)
        {
            normOk = false;
            normErr = ex.GetBaseException().Message;
        }

        string rtV2 = "";
        bool rtOk = true;
        var details = new List<string>();
        if (!parseOk) details.Add($"ParseError: {parsed.SyntaxErrorMessage}");
        if (!validateOk) details.Add($"ValidateError: {string.Join("; ", validation.Diagnostics.Select(d => $"{d.Code}:{d.Message}"))}");
        if (!fmtIdempotent) details.Add($"FormatNotIdempotent: '{formatted1}' != '{formatted2}'");
        if (!normOk) details.Add($"NormalizeEx: {normErr}");

        if (normOk)
        {
            try
            {
                rtV2 = CommandSyntaxBridge.FormatToV2(schema.CommandKey, normalized);
                var rtParsed = CommandParamParser.Parse(rtV2, schema);
                if (rtParsed.HasSyntaxError)
                {
                    rtOk = false;
                    details.Add($"RoundTripParseError: {rtParsed.SyntaxErrorMessage}");
                }
                else
                {
                    foreach (var reqKey in expectedRequired)
                    {
                        if (!rtParsed.TryGet(reqKey, out var rtVal) || string.IsNullOrWhiteSpace(rtVal))
                        {
                            rtOk = false;
                            details.Add($"LostRequiredAfterBridge: '{reqKey}'");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                rtOk = false;
                details.Add($"RoundTripEx: {ex.GetBaseException().Message}");
            }
        }
        else
        {
            rtOk = false;
        }

        return new BridgeRoundTripRow(
            schema.CommandKey, mod, syntheticV2,
            parseOk, validateOk, fmtIdempotent, normOk, rtOk,
            normalized, rtV2, string.Join(" | ", details));
    }

    private static string BuildSyntheticV2Param(CommandSchema schema, out List<string> requiredKeys)
    {
        requiredKeys = new List<string>();
        var reqParts = new List<string>();
        var optParts = new List<string>();
        var outParts = new List<string>();

        foreach (var p in schema.Parameters)
        {
            if (p.Direction == ParamDirection.ImplicitEffect) continue;

            string sampleVal = SampleValueFor(p);
            if (p.Direction == ParamDirection.Input && p.Required)
            {
                requiredKeys.Add(p.Name);
                reqParts.Add($"{p.Name}={CommandFormatter.FormatValue(sampleVal, p)}");
            }
            else if (p.Direction == ParamDirection.Output && p.Required)
            {
                requiredKeys.Add(p.Name);
                outParts.Add($"{p.Name}={CommandFormatter.FormatValue(sampleVal, p)}");
            }
            else if (p.Direction == ParamDirection.Input && !p.Required && optParts.Count == 0)
            {
                // Pick 1 non-default optional param if possible
                string nonDef = NonDefaultSampleValueFor(p);
                if (!p.IsDefaultValue(nonDef))
                {
                    optParts.Add($"{p.Name}={CommandFormatter.FormatValue(nonDef, p)}");
                }
            }
            else if (p.Direction == ParamDirection.Output && !p.Required && outParts.Count == 0)
            {
                outParts.Add($"{p.Name}={CommandFormatter.FormatValue(sampleVal, p)}");
            }
        }

        var sb = new StringBuilder();
        if (reqParts.Count > 0) sb.Append(string.Join(", ", reqParts));
        if (optParts.Count > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('{').Append(string.Join(", ", optParts)).Append('}');
        }
        if (outParts.Count > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("-> ").Append(string.Join(", ", outParts));
        }
        if (schema.SupportsErrorTrap)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("[[ErrLbl]]Timeout");
        }
        return sb.ToString();
    }

    private static string SampleValueFor(ParamSpec p)
    {
        if (p.AllowedValues is { Length: > 0 }) return p.AllowedValues[0];
        return p.ValueType switch
        {
            ParamValueType.Integer => "2",
            ParamValueType.Number => "1.5",
            ParamValueType.Boolean => "true",
            ParamValueType.LogicExpr => "1==1",
            ParamValueType.PortName => "DI0",
            ParamValueType.PortList => "[P0.0;P0.1]",
            ParamValueType.DriverName => "MockDriver",
            ParamValueType.ThreadTarget => "Thread1",
            ParamValueType.ProgramTarget => "SubProg",
            ParamValueType.LabelName => "L1",
            ParamValueType.VariableName => "$V1",
            ParamValueType.FilePath => "test.csv",
            ParamValueType.MultiLineText => "x = 1",
            _ => p.Name.Equals("coords", StringComparison.OrdinalIgnoreCase) ? "10,20,0"
               : p.Name.Equals("roi", StringComparison.OrdinalIgnoreCase) ? "0,0,100,100"
               : p.Name.Equals("delta", StringComparison.OrdinalIgnoreCase) ? "(1,2,3)"
               : p.Name.Equals("expr", StringComparison.OrdinalIgnoreCase) ? "1+2"
               : "SampleVal"
        };
    }

    private static string NonDefaultSampleValueFor(ParamSpec p)
    {
        if (p.AllowedValues is { Length: > 0 })
        {
            var alt = p.AllowedValues.FirstOrDefault(v => !p.IsDefaultValue(v));
            if (alt != null) return alt;
        }
        if (p.ValueType == ParamValueType.Boolean)
        {
            return p.IsDefaultValue("true") ? "false" : "true";
        }
        if (p.ValueType == ParamValueType.Integer)
        {
            return p.IsDefaultValue("3") ? "5" : "3";
        }
        if (p.ValueType == ParamValueType.Number)
        {
            return p.IsDefaultValue("2.5") ? "3.5" : "2.5";
        }
        return SampleValueFor(p);
    }

    private static string BuildMarkdownReport(
        List<CsviContractRow> csvis,
        List<(string Module, string FileName, bool LoadedOk, bool HasFrontPanel, string? Error)> panels,
        List<BridgeRoundTripRow> bridges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ARCOSharp Layer-2 Contract Audit Report — 256 CSVI Guide UIs & Engine Syntax Bridge");
        sb.AppendLine();
        sb.AppendLine($"- **Date**: `2026-11-22`");
        sb.AppendLine($"- **Target**: `dist/ARCOS_Runtime/MODULES/` (14 Official Modules)");
        sb.AppendLine($"- **Command CSVIs Audited**: **{csvis.Count}** (+ **{panels.Count}** Module Panels)");
        sb.AppendLine($"- **StandardCommandSchemas Round-Tripped**: **{bridges.Count}**");
        sb.AppendLine();

        sb.AppendLine("## 1. Executive Scorecard");
        sb.AppendLine();
        sb.AppendLine("| Check Category | Total | Pass | Issue / Gap | Severity |");
        sb.AppendLine("| :--- | ---: | ---: | ---: | :--- |");
        sb.AppendLine($"| C1.1 CSVI Assembly Load | {csvis.Count} | {csvis.Count(r => r.LoadedOk)} | {csvis.Count(r => !r.LoadedOk)} | P0 |");
        sb.AppendLine($"| C1.2 `IParameterGuidance` Instantiation | {csvis.Count} | {csvis.Count(r => r.HasGuidanceType)} | {csvis.Count(r => !r.HasGuidanceType)} | P0 |");
        sb.AppendLine($"| C2.1 `StandardCommandSchemas` Coverage | {csvis.Count} | {csvis.Count(r => r.HasStdSchema)} | {csvis.Count(r => !r.HasStdSchema)} | P1 |");
        sb.AppendLine($"| C2.2 `Manifest.Schema` Embedded | {csvis.Count} | {csvis.Count(r => r.HasManifestSchema)} | {csvis.Count(r => !r.HasManifestSchema)} | P1 |");
        sb.AppendLine($"| C2.3 Schema Triangulation (0 Drift) | {csvis.Count} | {csvis.Count(r => string.IsNullOrEmpty(r.SchemaDrift))} | {csvis.Count(r => !string.IsNullOrEmpty(r.SchemaDrift))} | P0 |");
        sb.AppendLine($"| C3.1 AXAML Valid XML | {csvis.Count} | {csvis.Count(r => r.HasAxaml && r.AxamlValidXml)} | {csvis.Count(r => !r.HasAxaml || !r.AxamlValidXml)} | P0 |");
        sb.AppendLine($"| C3.2 `FindControl` ↔ `x:Name` Wiring | {csvis.Count} | {csvis.Count(r => r.DanglingFindControls.Count == 0)} | {csvis.Count(r => r.DanglingFindControls.Count > 0)} | P0 |");
        sb.AppendLine($"| C3.3 Guide UI Param Keys ↔ Schema | {csvis.Count} | {csvis.Count(r => r.UnknownParamKeysInCode.Count == 0)} | {csvis.Count(r => r.UnknownParamKeysInCode.Count > 0)} | P0 |");
        sb.AppendLine($"| C4.1 Guide UI Re-open v2.0 Parser | {csvis.Count} | {csvis.Count(r => r.ReopenParseMode != "LEGACY_ONLY")} | {csvis.Count(r => r.ReopenParseMode == "LEGACY_ONLY")} | P1 (UX Risk) |");
        sb.AppendLine($"| C4.2 Guide UI v2.0 Output Formatting | {csvis.Count} | {csvis.Count(r => r.EmitMode != "LEGACY_RAW")} | {csvis.Count(r => r.EmitMode == "LEGACY_RAW")} | P2 (Legacy Emit) |");
        sb.AppendLine($"| C5.1 `ModuleRegistry` Registration | {csvis.Count} | {csvis.Count(r => r.RegisteredInModuleRegistry)} | {csvis.Count(r => !r.RegisteredInModuleRegistry)} | P1 |");
        sb.AppendLine($"| C5.2 Schema Synthetic v2 Parse + Validate | {bridges.Count} | {bridges.Count(b => b.ParseOk && b.ValidateOk)} | {bridges.Count(b => !b.ParseOk || !b.ValidateOk)} | P0 |");
        sb.AppendLine($"| C5.3 `CommandFormatter` Idempotency | {bridges.Count} | {bridges.Count(b => b.FormatIdempotent)} | {bridges.Count(b => !b.FormatIdempotent)} | P1 |");
        sb.AppendLine($"| C5.4 `NormalizeForExecution` ↔ `FormatToV2` Round-Trip | {bridges.Count} | {bridges.Count(b => b.NormalizeOk && b.BridgeRoundTripOk)} | {bridges.Count(b => !b.NormalizeOk || !b.BridgeRoundTripOk)} | P0 |");
        sb.AppendLine();

        sb.AppendLine("## 2. Per-Module Breakdown");
        sb.AppendLine();
        sb.AppendLine("| Module | Cmd CSVIs | Load OK | Dangling `FindControl` | Schema Drift | Reopen `V2_AWARE` / `LEGACY_ONLY` | Emit `V2` / `LEGACY` | Unregistered |");
        sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | :---: | :---: | ---: |");
        foreach (var mod in OfficialModules)
        {
            var rows = csvis.Where(r => r.Module == mod).ToList();
            if (rows.Count == 0) continue;
            sb.AppendLine(
                $"| `{mod}` | {rows.Count} | {rows.Count(r => r.LoadedOk)}/{rows.Count} | {rows.Count(r => r.DanglingFindControls.Count > 0)} | " +
                $"{rows.Count(r => !string.IsNullOrEmpty(r.SchemaDrift))} | " +
                $"{rows.Count(r => r.ReopenParseMode == "V2_AWARE")} / {rows.Count(r => r.ReopenParseMode == "LEGACY_ONLY")} | " +
                $"{rows.Count(r => r.EmitMode == "V2_FORMATTED")} / {rows.Count(r => r.EmitMode == "LEGACY_RAW")} | " +
                $"{rows.Count(r => !r.RegisteredInModuleRegistry)} |");
        }
        sb.AppendLine();

        // Detailed sections for every non-zero defect category
        var danglingList = csvis.Where(r => r.DanglingFindControls.Count > 0).ToList();
        sb.AppendLine($"## 3. Dangling `FindControl` ↔ AXAML `x:Name` Mismatches ({danglingList.Count})");
        sb.AppendLine();
        if (danglingList.Count == 0)
        {
            sb.AppendLine("_None — all `FindControl` calls match AXAML `x:Name` definitions._");
        }
        else
        {
            sb.AppendLine("| Module | CSVI | Missing Control Names in AXAML |");
            sb.AppendLine("| :--- | :--- | :--- |");
            foreach (var r in danglingList)
                sb.AppendLine($"| `{r.Module}` | `{r.FileName}` | `{string.Join("`, `", r.DanglingFindControls)}` |");
        }
        sb.AppendLine();

        var driftList = csvis.Where(r => !string.IsNullOrEmpty(r.SchemaDrift) || !r.HasStdSchema || !r.HasManifestSchema).ToList();
        sb.AppendLine($"## 4. Schema Coverage & Drift Issues ({driftList.Count})");
        sb.AppendLine();
        if (driftList.Count == 0)
        {
            sb.AppendLine("_None — 100% aligned._");
        }
        else
        {
            sb.AppendLine("| Module | CSVI | GuidanceKey | StdSchema | ManifestSchema | GetSchema() | Drift Details |");
            sb.AppendLine("| :--- | :--- | :--- | :---: | :---: | :---: | :--- |");
            foreach (var r in driftList)
                sb.AppendLine($"| `{r.Module}` | `{r.FileName}` | `{r.GuidanceKey}` | {(r.HasStdSchema ? "✅" : "❌")} | {(r.HasManifestSchema ? "✅" : "❌")} | {(r.HasRuntimeGetSchema ? "✅" : "—")} | {r.SchemaDrift} |");
        }
        sb.AppendLine();

        var bridgeIssues = bridges.Where(b => !b.ParseOk || !b.ValidateOk || !b.FormatIdempotent || !b.NormalizeOk || !b.BridgeRoundTripOk).ToList();
        sb.AppendLine($"## 5. `CommandSyntaxBridge` v2 ↔ Legacy Round-Trip Issues ({bridgeIssues.Count})");
        sb.AppendLine();
        if (bridgeIssues.Count == 0)
        {
            sb.AppendLine("_None — all StandardCommandSchemas passed full parse/validate/format/bridge round-trip._");
        }
        else
        {
            sb.AppendLine("| CommandKey | Synthetic v2.0 | Normalized Legacy | RoundTrip v2.0 | Details |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");
            foreach (var b in bridgeIssues)
                sb.AppendLine($"| `{b.CommandKey}` | `{Esc(b.SyntheticV2)}` | `{Esc(b.NormalizedLegacy)}` | `{Esc(b.RoundTripV2)}` | {Esc(b.Details)} |");
        }
        sb.AppendLine();

        var legacyReopen = csvis.Where(r => r.ReopenParseMode == "LEGACY_ONLY").ToList();
        sb.AppendLine($"## 6. Guide UIs with Legacy-Only Re-open Parsing ({legacyReopen.Count})");
        sb.AppendLine();
        sb.AppendLine("> When a user double-clicks a v2.0 script line (`key=val {opt=val} -> out=$x`), Guide UIs without `CommandParamParser.Parse` or `NormalizeForExecution` may fail to pre-fill fields cleanly unless the host pre-normalizes `currentParam`.");
        sb.AppendLine();
        if (legacyReopen.Count == 0)
        {
            sb.AppendLine("_None — all parameterized Guide UIs are v2.0 aware._");
        }
        else
        {
            foreach (var grp in legacyReopen.GroupBy(r => r.Module))
            {
                sb.AppendLine($"- **`{grp.Key}` ({grp.Count()})**: {string.Join(", ", grp.Select(x => $"`{Path.GetFileNameWithoutExtension(x.FileName)}`"))}");
            }
        }
        sb.AppendLine();

        var unregList = csvis.Where(r => !r.RegisteredInModuleRegistry).ToList();
        sb.AppendLine($"## 7. CSVIs Not Found in `ModuleRegistry.GetAllCommands(\"*\")` ({unregList.Count})");
        sb.AppendLine();
        if (unregList.Count == 0)
        {
            sb.AppendLine("_None — all CSVIs are registered._");
        }
        else
        {
            foreach (var r in unregList)
                sb.AppendLine($"- `{r.Module}` / `{r.FileName}` (Expected `{r.ExpectedKey}`, GuidanceKey=`{r.GuidanceKey}`)");
        }
        sb.AppendLine();

        var badAxaml = csvis.Where(r => !r.HasAxaml || !r.AxamlValidXml).ToList();
        sb.AppendLine($"## 8. Missing or Invalid AXAML XML ({badAxaml.Count})");
        sb.AppendLine();
        if (badAxaml.Count == 0)
        {
            sb.AppendLine("_None — all AXAML files are valid XML._");
        }
        else
        {
            sb.AppendLine("| Module | CSVI | HasAxaml | XML Parse Error |");
            sb.AppendLine("| :--- | :--- | :---: | :--- |");
            foreach (var r in badAxaml)
                sb.AppendLine($"| `{r.Module}` | `{r.FileName}` | {(r.HasAxaml ? "Yes" : "No")} | `{Esc(r.AxamlError ?? "No AXAML embedded")}` |");
        }
        sb.AppendLine();

        var legacyEmit = csvis.Where(r => r.EmitMode == "LEGACY_RAW").ToList();
        sb.AppendLine($"## 9. Guide UIs Emitting Legacy Raw Syntax ({legacyEmit.Count})");
        sb.AppendLine();
        if (legacyEmit.Count == 0)
        {
            sb.AppendLine("_None — all parameterized Guide UIs format v2.0 output._");
        }
        else
        {
            foreach (var r in legacyEmit)
                sb.AppendLine($"- `{r.Module}` / `{r.FileName}` (`{r.GuidanceKey}`) — `{Esc(r.EmitSnippet)}`");
        }
        sb.AppendLine();

        var unknownKeyList = csvis.Where(r => r.UnknownParamKeysInCode.Count > 0).ToList();
        sb.AppendLine($"## 10. Guide UI Code Referencing Unknown Schema Param Keys ({unknownKeyList.Count})");
        sb.AppendLine();
        if (unknownKeyList.Count == 0)
        {
            sb.AppendLine("_None — every parsed parameter key in Guide UI code exists in CommandSchema._");
        }
        else
        {
            sb.AppendLine("| Module | CSVI | Unknown Keys Referenced in C# |");
            sb.AppendLine("| :--- | :--- | :--- |");
            foreach (var r in unknownKeyList)
                sb.AppendLine($"| `{r.Module}` | `{r.FileName}` | `{string.Join("`, `", r.UnknownParamKeysInCode)}` |");
        }

        return sb.ToString();
    }

    private static string Esc(string s) => (s ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
