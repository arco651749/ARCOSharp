namespace MVCON.Core.Execution;

/// <summary>
/// 單行指令項目封裝
/// 支援解析標準腳本格式 (<Module>@<Action>::<Param>;) 與 INI 檔案格式 (<Module>@<Action>\t<Param>)
/// </summary>
public sealed class CommandItem
{
    public int LineNumber { get; set; }
    public string RawLine { get; }
    public string Command { get; }
    public string Module { get; }
    public string Action { get; }
    public string RawParameter { get; }
    public bool IsComment { get; }
    public bool IsEmpty { get; }

    public CommandItem(int lineNumber, string rawLine, string command, string module, string action, string rawParameter, bool isComment, bool isEmpty)
    {
        LineNumber = lineNumber;
        RawLine = rawLine;
        Command = command;
        Module = module;
        Action = action;
        RawParameter = rawParameter;
        IsComment = isComment;
        IsEmpty = isEmpty;
    }

    /// <summary>
    /// 解析單行文字為 CommandItem
    /// </summary>
    public static CommandItem Parse(string rawLine, int lineNumber = 0)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return new CommandItem(lineNumber, rawLine ?? "", "", "", "", "", false, true);
        }

        string trimmed = rawLine.Trim();

        // 移除 INI 鍵值前綴（如 1="IO@SetPort\tP0.0" 或 1=IO@SetPort...）
        int eqIdx = trimmed.IndexOf('=');
        if (eqIdx > 0 && int.TryParse(trimmed.Substring(0, eqIdx).Trim(), out _))
        {
            trimmed = trimmed.Substring(eqIdx + 1).Trim();
            if (trimmed.StartsWith('\"') && trimmed.EndsWith('\"') && trimmed.Length >= 2)
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            }
        }

        // 判斷註解
        if (trimmed.StartsWith("//") || trimmed.StartsWith(';'))
        {
            return new CommandItem(lineNumber, rawLine, "", "", "", trimmed, true, false);
        }

        // 移除尾端分號 ; (結尾分隔符)
        if (trimmed.EndsWith(';'))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
        }

        string cmdPart;
        string paramPart;

        // 優先檢查 :: 分隔符
        int doubleColonIdx = trimmed.IndexOf("::", StringComparison.Ordinal);
        if (doubleColonIdx >= 0)
        {
            cmdPart = trimmed.Substring(0, doubleColonIdx).Trim();
            paramPart = trimmed.Substring(doubleColonIdx + 2).Trim();
        }
        else
        {
            // 次之檢查 Tab 分隔符 (INI 格式)
            int tabIdx = trimmed.IndexOf('\t');
            if (tabIdx >= 0)
            {
                cmdPart = trimmed.Substring(0, tabIdx).Trim();
                paramPart = trimmed.Substring(tabIdx + 1).Trim();
            }
            else
            {
                cmdPart = trimmed.Trim();
                paramPart = "";
            }
        }

        string module = "";
        string action = cmdPart;
        int atIdx = cmdPart.IndexOf('@');
        if (atIdx >= 0)
        {
            module = cmdPart.Substring(0, atIdx).Trim();
            action = cmdPart.Substring(atIdx + 1).Trim();
        }

        bool isComment = false;
        if (module.Equals("System", StringComparison.OrdinalIgnoreCase) && 
            action.StartsWith("#Comment", StringComparison.OrdinalIgnoreCase))
        {
            isComment = true;
        }

        return new CommandItem(lineNumber, rawLine, cmdPart, module, action, paramPart, isComment, false);
    }

    public override string ToString() =>
        string.IsNullOrEmpty(RawParameter) ? $"{LineNumber}: {Command}" : $"{LineNumber}: {Command}::{RawParameter}";
}
