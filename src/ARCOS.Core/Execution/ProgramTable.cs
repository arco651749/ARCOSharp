namespace ARCOS.Core.Execution;

/// <summary>
/// 程式腳本區段表格 (對應 Program 0~99 或獨立 .init 腳本)
/// 維護 1-based 行號指令清單與 Label 快速定位索引
/// </summary>
public sealed class ProgramTable
{
    private readonly List<CommandItem> _items = new();
    private readonly Dictionary<string, int> _labelIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _errorHandlerIndex = new(StringComparer.OrdinalIgnoreCase);
    private int _defaultErrorHandlerLine = -1;

    public int ProgramNumber { get; set; }
    public string Name { get; set; } = "";
    public IReadOnlyList<CommandItem> Items => _items;
    public int Count => _items.Count;

    public CommandItem this[int lineNumber1Based]
    {
        get
        {
            if (lineNumber1Based < 1 || lineNumber1Based > _items.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(lineNumber1Based), $"Line {lineNumber1Based} is out of bounds (1..{_items.Count}).");
            }
            return _items[lineNumber1Based - 1];
        }
    }

    public void Add(CommandItem item)
    {
        item.LineNumber = _items.Count + 1;
        _items.Add(item);
        IndexItem(item);
    }

    public void Clear()
    {
        _items.Clear();
        _labelIndex.Clear();
        _errorHandlerIndex.Clear();
        _defaultErrorHandlerLine = -1;
    }

    private void IndexItem(CommandItem item)
    {
        if (item.IsComment || item.IsEmpty) return;

        if (item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase))
        {
            if (item.Action.Equals("Label", StringComparison.OrdinalIgnoreCase) || 
                item.Action.Equals("Thread Label", StringComparison.OrdinalIgnoreCase))
            {
                string lbl = item.RawParameter.Trim();
                if (!string.IsNullOrEmpty(lbl))
                {
                    _labelIndex[lbl] = item.LineNumber;
                }
            }
            else if (item.Action.Equals("ErrorHandler", StringComparison.OrdinalIgnoreCase))
            {
                string param = item.RawParameter.Trim();
                if (param.StartsWith("[[") && param.EndsWith("]]") && param.Length > 4)
                {
                    string errTag = param.Substring(2, param.Length - 4).Trim();
                    _errorHandlerIndex[errTag] = item.LineNumber;
                }
                else if (string.IsNullOrEmpty(param))
                {
                    if (_defaultErrorHandlerLine == -1)
                    {
                        _defaultErrorHandlerLine = item.LineNumber;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 搜尋標籤行號 (1-based)，找不到回傳 -1
    /// </summary>
    public int FindLabel(string labelName)
    {
        if (string.IsNullOrWhiteSpace(labelName)) return -1;

        if (_labelIndex.TryGetValue(labelName.Trim(), out int line))
        {
            return line;
        }

        // 若無直接對應的 Label，嘗試查詢同名 ErrorHandler
        if (_errorHandlerIndex.TryGetValue(labelName.Trim(), out int errLine))
        {
            return errLine;
        }

        return -1;
    }

    /// <summary>
    /// 尋找錯誤處理攔截點 (優先匹配特定 [[Label]]，次之匹配預設 ErrorHandler，再者尋找一般 Label)
    /// </summary>
    public int FindErrorHandler(string? errorLabel = null)
    {
        if (!string.IsNullOrWhiteSpace(errorLabel))
        {
            string clean = errorLabel.Trim().Trim('[', ']');
            if (_errorHandlerIndex.TryGetValue(clean, out int errLine))
            {
                return errLine;
            }
            if (_labelIndex.TryGetValue(clean, out int lblLine))
            {
                return lblLine;
            }

            // 容錯支援標籤前綴帶 $ (例如 [[]] 自動匹配 [[ErrCatch]])
            if (clean.StartsWith('$'))
            {
                string stripped = clean.TrimStart('$');
                if (_errorHandlerIndex.TryGetValue(stripped, out int sLine)) return sLine;
                if (_labelIndex.TryGetValue(stripped, out int slLine)) return slLine;
            }
        }

        if (_defaultErrorHandlerLine > 0)
        {
            return _defaultErrorHandlerLine;
        }

        return -1;
    }

    /// <summary>
    /// 從文字行清單解析建構 ProgramTable
    /// </summary>
    public static ProgramTable FromLines(IEnumerable<string> lines, int progNo = 0, string name = "")
    {
        var table = new ProgramTable { ProgramNumber = progNo, Name = name };
        string pending = "";

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine) && string.IsNullOrEmpty(pending))
            {
                table.Add(CommandItem.Parse(rawLine));
                continue;
            }

            string trimmed = rawLine.Trim();
            if ((trimmed.StartsWith("//") || trimmed.StartsWith(';')) && string.IsNullOrEmpty(pending))
            {
                table.Add(CommandItem.Parse(rawLine));
                continue;
            }

            // 若已有累積語句，且新行自身為獨立新指令起點（包含 Module@Action:: 或標籤 :），先結算前一指令
            if (!string.IsNullOrEmpty(pending) && IsCommandHeader(rawLine))
            {
                table.Add(CommandItem.Parse(pending));
                pending = "";
            }

            if (!string.IsNullOrEmpty(pending))
            {
                pending += "\n" + rawLine;
            }
            else
            {
                pending = rawLine;
            }

            string checkTrimmed = pending.Trim();
            if (checkTrimmed.EndsWith(';') || checkTrimmed.StartsWith(':'))
            {
                table.Add(CommandItem.Parse(pending));
                pending = "";
            }
        }

        if (!string.IsNullOrEmpty(pending))
        {
            table.Add(CommandItem.Parse(pending));
        }

        return table;
    }

    private static bool IsCommandHeader(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        string t = line.Trim();
        if (t.StartsWith(':')) return true;
        if (t.StartsWith("//") || t.StartsWith(';')) return false;
        int atIdx = t.IndexOf('@');
        if (atIdx > 0 && atIdx < t.Length - 1)
        {
            int colonIdx = t.IndexOf("::", atIdx, StringComparison.Ordinal);
            if (colonIdx > atIdx) return true;
            int tabIdx = t.IndexOf('\t', atIdx);
            if (tabIdx > atIdx) return true;
        }
        return false;
    }

    /// <summary>
    /// 從完整多行字串解析建構 ProgramTable
    /// </summary>
    public static ProgramTable FromText(string text, int progNo = 0, string name = "")
    {
        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        return FromLines(lines, progNo, name);
    }

    /// <summary>
    /// 從檔案路徑讀取並解析建構 ProgramTable
    /// </summary>
    public static ProgramTable FromIniFile(string filePath, int progNo = 0, string name = "")
    {
        if (!File.Exists(filePath)) return new ProgramTable { ProgramNumber = progNo, Name = name };
        var lines = File.ReadAllLines(filePath);
        return FromLines(lines, progNo, string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(filePath) : name);
    }
}
