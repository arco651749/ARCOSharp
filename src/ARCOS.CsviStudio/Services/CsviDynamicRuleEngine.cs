using System.Text.RegularExpressions;
using ARCOS.Core.Csvi;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ARCOS.CsviStudio.Services;

/// <summary>
/// CSVI Studio 動態規則執行引擎（完全由 RULES/CsviLintRules.json 驅動）。
/// 利用 Roslyn 語法樹與呼叫圖分析評估規則，結果僅回報為 Warning，絕不阻斷使用者編譯產出 .csvi。
/// </summary>
public static class CsviDynamicRuleEngine
{
    public static CsviTargetContext DetectContext(CSharpCompilation compilation, CsviManifest manifest, Dictionary<string, string> sources)
    {
        string category = "*";
        string driverName = string.IsNullOrWhiteSpace(manifest.CommandKey) ? compilation.AssemblyName ?? "CSVI" : manifest.CommandKey;
        bool hasDriver = false;
        bool hasGuidance = false;
        bool hasModuleHandler = false;

        if (!string.IsNullOrWhiteSpace(manifest.CommandKey) && manifest.CommandKey.Contains('@'))
        {
            category = manifest.CommandKey.Split('@', 2)[0].Trim();
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (cls.BaseList != null)
                {
                    foreach (var bt in cls.BaseList.Types)
                    {
                        string tName = bt.Type.ToString();
                        if (tName.Contains("IDriver", StringComparison.Ordinal)) hasDriver = true;
                        if (tName.Contains("IParameterGuidance", StringComparison.Ordinal)) hasGuidance = true;
                        if (tName.Contains("IModuleHandler", StringComparison.Ordinal)) hasModuleHandler = true;
                    }
                }

                // Extract `public string Category => "IO";` or `ModuleName => "Capture"` if present
                foreach (var prop in cls.Members.OfType<PropertyDeclarationSyntax>())
                {
                    string pName = prop.Identifier.ValueText;
                    if (pName is "Category" or "ModuleName")
                    {
                        var lit = prop.ExpressionBody?.Expression as LiteralExpressionSyntax
                                  ?? prop.Initializer?.Value as LiteralExpressionSyntax;
                        if (lit != null && lit.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            string val = lit.Token.ValueText.Trim();
                            if (!string.IsNullOrEmpty(val))
                                category = val;
                        }
                    }
                    else if (pName == "DriverType")
                    {
                        var lit = prop.ExpressionBody?.Expression as LiteralExpressionSyntax;
                        if (lit != null && lit.IsKind(SyntaxKind.StringLiteralExpression))
                            driverName = lit.Token.ValueText.Trim();
                    }
                }
            }
        }

        return new CsviTargetContext(driverName, category, hasDriver, hasGuidance, hasModuleHandler);
    }

    public static List<CsviLintRuleDefinition> FilterApplicableRules(CsviLintConfig config, CsviTargetContext ctx)
    {
        var applicable = new List<CsviLintRuleDefinition>();
        foreach (var rule in config.Rules)
        {
            if (!rule.Enabled) continue;

            if (!string.Equals(rule.Category, "*", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rule.Category, ctx.Category, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool ifaceOk = rule.RequiresInterface?.Trim() switch
            {
                null or "" or "*" => true,
                "IDriver" => ctx.ImplementsIDriver,
                "IParameterGuidance" => ctx.ImplementsGuidance,
                "IModuleHandler" => ctx.ImplementsModuleHandler,
                _ => true
            };

            if (ifaceOk) applicable.Add(rule);
        }
        return applicable;
    }

    public static List<string> EvaluateLocalStaticWarnings(
        CSharpCompilation compilation,
        CsviManifest manifest,
        Dictionary<string, string> sources,
        CsviLintConfig config)
    {
        var warnings = new List<string>();
        if (!config.LintBehavior.EmitLocalStaticWarningsOnCompile)
            return warnings;

        var ctx = DetectContext(compilation, manifest, sources);
        var rules = FilterApplicableRules(config, ctx);
        if (rules.Count == 0) return warnings;

        var methodsByName = IndexAllMethods(compilation);
        string combinedSource = string.Join("\n", sources.Where(k => k.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Select(k => k.Value));

        foreach (var rule in rules)
        {
            var chk = rule.LocalStaticCheck;
            if (chk == null || string.Equals(chk.Kind, "None", StringComparison.OrdinalIgnoreCase))
                continue;

            string? violation = chk.Kind switch
            {
                "InitMustCall" => CheckInitMustCall(chk, methodsByName),
                "LoopMustNotCall" => CheckLoopMustNotCall(chk, methodsByName),
                "MethodMustCatch" => CheckMethodMustCatch(chk, methodsByName),
                "MustSubscribeEvent" => CheckMustSubscribeEvent(chk, combinedSource),
                "DisposalSafety" => CheckDisposalSafety(chk, methodsByName, combinedSource),
                "RegexMustMatch" => CheckRegex(chk.Pattern, combinedSource, expectMatch: true, rule.Title),
                "RegexMustNotMatch" => CheckRegex(chk.Pattern, combinedSource, expectMatch: false, rule.Title),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(violation))
            {
                warnings.Add($"[LINT-WARN][{rule.Id}] {rule.Title} — {violation}");
            }
        }

        return warnings;
    }

    private static Dictionary<string, List<MethodDeclarationSyntax>> IndexAllMethods(CSharpCompilation compilation)
    {
        var map = new Dictionary<string, List<MethodDeclarationSyntax>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in compilation.SyntaxTrees)
        {
            foreach (var m in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                string name = m.Identifier.ValueText;
                if (!map.TryGetValue(name, out var list))
                {
                    list = new List<MethodDeclarationSyntax>();
                    map[name] = list;
                }
                list.Add(m);
            }
        }
        return map;
    }

    private static List<MethodDeclarationSyntax> CollectReachableMethods(
        IEnumerable<string> entryNames,
        Dictionary<string, List<MethodDeclarationSyntax>> allMethods,
        bool followPrivateCalls,
        bool excludeAsyncBackgroundLoops = false)
    {
        var visited = new HashSet<MethodDeclarationSyntax>();
        var queue = new Queue<(MethodDeclarationSyntax Method, int Depth)>();

        foreach (var entry in entryNames)
        {
            if (allMethods.TryGetValue(entry, out var found))
            {
                foreach (var m in found)
                {
                    if (visited.Add(m)) queue.Enqueue((m, 0));
                }
            }
        }

        while (queue.Count > 0)
        {
            var (cur, depth) = queue.Dequeue();
            if (!followPrivateCalls || depth >= 4) continue;

            foreach (var inv in cur.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (excludeAsyncBackgroundLoops && IsInsideBackgroundLaunchLambda(inv))
                    continue;

                string? calleeName = ExtractInvokedMethodName(inv);
                if (calleeName == null) continue;
                if (excludeAsyncBackgroundLoops && calleeName.EndsWith("Loop", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (allMethods.TryGetValue(calleeName, out var targets))
                {
                    foreach (var t in targets)
                    {
                        if (visited.Add(t)) queue.Enqueue((t, depth + 1));
                    }
                }
            }
        }

        return visited.ToList();
    }

    private static bool IsInsideBackgroundLaunchLambda(SyntaxNode node)
    {
        var lambda = node.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault();
        if (lambda == null) return false;

        var outerCall = lambda.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (outerCall != null)
        {
            string callText = outerCall.Expression.ToString();
            if (callText.Contains("Task.Run", StringComparison.Ordinal) ||
                callText.Contains("StartNew", StringComparison.Ordinal))
            {
                return true;
            }
        }

        var threadCreation = lambda.Ancestors().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
        return threadCreation != null && threadCreation.Type.ToString().Contains("Thread", StringComparison.Ordinal);
    }

    private static string? CheckInitMustCall(LocalStaticCheckConfig chk, Dictionary<string, List<MethodDeclarationSyntax>> allMethods)
    {
        var reachable = CollectReachableMethods(
            chk.EntryMethods,
            allMethods,
            chk.FollowPrivateCalls,
            excludeAsyncBackgroundLoops: true);
        if (reachable.Count == 0)
            return $"找不到進入方法 ({string.Join(", ", chk.EntryMethods)})。";

        foreach (var m in reachable)
        {
            foreach (var inv in m.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (IsInsideBackgroundLaunchLambda(inv))
                    continue;
                string? mName = ExtractInvokedMethodName(inv);
                if (mName == null) continue;
                bool methodMatched = chk.RequiredMethodTokens.Count == 0 ||
                                     chk.RequiredMethodTokens.Any(tok => mName.Contains(tok, StringComparison.OrdinalIgnoreCase));
                if (!methodMatched) continue;

                string argsText = inv.ArgumentList?.ToString() ?? "";
                bool argMatched = chk.RequiredArgumentTokens.Count == 0 ||
                                  chk.RequiredArgumentTokens.Any(tok => argsText.Contains(tok, StringComparison.OrdinalIgnoreCase));
                if (argMatched)
                    return null;
            }
        }

        return $"從 {string.Join("/", chk.EntryMethods)} 出發未偵測到預期初始化呼叫 ({string.Join("/", chk.RequiredMethodTokens)} + {string.Join("/", chk.RequiredArgumentTokens)})。";
    }

    private static string? CheckLoopMustNotCall(LocalStaticCheckConfig chk, Dictionary<string, List<MethodDeclarationSyntax>> allMethods)
    {
        var reachable = CollectReachableMethods(chk.EntryMethods, allMethods, followPrivateCalls: true);
        foreach (var m in reachable)
        {
            foreach (var inv in m.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? mName = ExtractInvokedMethodName(inv);
                if (mName == null) continue;

                bool forbiddenMethod = chk.ForbiddenMethodTokens.Any(tok => mName.Contains(tok, StringComparison.OrdinalIgnoreCase));
                if (!forbiddenMethod) continue;

                string argsText = inv.ArgumentList?.ToString() ?? "";
                bool forbiddenArg = chk.ForbiddenArgumentTokens.Count == 0 ||
                                    chk.ForbiddenArgumentTokens.Any(tok => argsText.Contains(tok, StringComparison.OrdinalIgnoreCase));
                if (forbiddenArg)
                {
                    int line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    return $"{m.Identifier.ValueText} (L{line}) 疑似執行了禁止的寫入/覆寫呼叫 `{inv}`，請確認未覆寫 OutStart 狀態。";
                }
            }
        }
        return null;
    }

    private static string? CheckMethodMustCatch(LocalStaticCheckConfig chk, Dictionary<string, List<MethodDeclarationSyntax>> allMethods)
    {
        var entries = CollectReachableMethods(chk.EntryMethods, allMethods, followPrivateCalls: false);
        if (entries.Count == 0) return null;

        foreach (var m in entries)
        {
            bool hasCatch = m.DescendantNodes().OfType<CatchClauseSyntax>().Any();
            if (!hasCatch)
            {
                int line = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                return $"{m.Identifier.ValueText} (L{line}) 缺少 try/catch 例外容錯（OperationCanceledException / 硬體斷線）。";
            }
        }
        return null;
    }

    private static string? CheckMustSubscribeEvent(LocalStaticCheckConfig chk, string combinedSource)
    {
        if (chk.RequiredTokens.Count == 0) return null;
        foreach (var tok in chk.RequiredTokens)
        {
            if (!Regex.IsMatch(combinedSource, $@"\b{Regex.Escape(tok)}\s*\+=", RegexOptions.CultureInvariant))
            {
                return $"程式碼中未見 `{tok} +=` 事件訂閱，請確認外部輸入覆寫時機。";
            }
        }
        return null;
    }

    private static string? CheckDisposalSafety(
        LocalStaticCheckConfig chk,
        Dictionary<string, List<MethodDeclarationSyntax>> allMethods,
        string combinedSource)
    {
        bool triggered = chk.TriggerIfSourceContains.Count == 0 ||
                         chk.TriggerIfSourceContains.Any(t => combinedSource.Contains(t, StringComparison.Ordinal));
        if (!triggered) return null;

        if (!allMethods.TryGetValue("Shutdown", out var shutdowns) || shutdowns.Count == 0)
            return "使用 CancellationTokenSource 但缺少 Shutdown() 方法。";

        bool hasCancel = shutdowns.Any(m => m.ToString().Contains(".Cancel(", StringComparison.Ordinal));
        if (!hasCancel)
            return "Shutdown() 內未呼叫 CancellationTokenSource.Cancel()。";

        if (!allMethods.TryGetValue("Dispose", out var disposes) || disposes.Count == 0)
            return "缺少 Dispose() 釋放 CancellationTokenSource。";

        bool disposeCallsShutdown = disposes.Any(m => m.ToString().Contains("Shutdown(", StringComparison.Ordinal));
        if (!disposeCallsShutdown)
            return "Dispose() 未呼叫 Shutdown()。";

        return null;
    }

    private static string? CheckRegex(string pattern, string combinedSource, bool expectMatch, string title)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        bool matched = Regex.IsMatch(combinedSource, pattern, RegexOptions.Multiline);
        if (expectMatch && !matched) return $"未符合自訂規則樣式 `{pattern}` ({title})。";
        if (!expectMatch && matched) return $"命中禁用規則樣式 `{pattern}` ({title})。";
        return null;
    }

    private static string? ExtractInvokedMethodName(InvocationExpressionSyntax inv)
    {
        return inv.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
            GenericNameSyntax gn => gn.Identifier.ValueText,
            _ => null
        };
    }
}
