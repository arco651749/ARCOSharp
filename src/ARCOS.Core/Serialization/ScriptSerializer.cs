using System.Text;
using ARCOS.Core.Execution;

namespace ARCOS.Core.Serialization;

/// <summary>
/// ARCOS 腳本與專案檔序列化器
/// 支援 .pro (Windows INI 標準多區段專案檔) 與 .init (單一程式腳本) 讀寫，
/// 並原生支援多行 Parameter (相容 Motion@ExtraCMD 手臂多行腳本與 Python 原始碼)。
/// </summary>
public static class ScriptSerializer
{
    /// <summary>
    /// 將多個 ProgramTable 儲存為 .pro 檔案 (INI 格式)
    /// </summary>
    public static void SaveProFile(string filePath, IEnumerable<ProgramTable> programs)
    {
        var sb = new StringBuilder();
        foreach (var prog in programs)
        {
            sb.AppendLine($"[Program {prog.ProgramNumber}]");
            sb.AppendLine($"Name=\"{prog.Name}\"");
            sb.AppendLine("Type=\"0\"");
            sb.AppendLine($"Size=\"{prog.Count}\"");

            for (int i = 1; i <= prog.Count; i++)
            {
                var item = prog[i];
                // 轉義換行符號以相容單行 INI
                string escapedParam = item.RawParameter
                    .Replace("\r\n", "\\n")
                    .Replace("\n", "\\n");

                sb.AppendLine($"{i}=\"{item.Command}\t{escapedParam}\"");
            }
            sb.AppendLine();
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// 從 .pro 檔案載入所有程式區段
    /// </summary>
    public static List<ProgramTable> LoadProFile(string filePath)
    {
        var result = new List<ProgramTable>();
        if (!File.Exists(filePath)) return result;

        var lines = File.ReadAllLines(filePath, Encoding.UTF8);
        ProgramTable? currentProg = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(';')) continue;

            if (line.StartsWith("[Program ") && line.EndsWith(']'))
            {
                string numStr = line.Substring(9, line.Length - 10).Trim();
                if (int.TryParse(numStr, out int pNo))
                {
                    currentProg = new ProgramTable { ProgramNumber = pNo };
                    result.Add(currentProg);
                }
                continue;
            }

            if (currentProg == null) continue;

            int eqIdx = line.IndexOf('=');
            if (eqIdx <= 0) continue;

            string key = line.Substring(0, eqIdx).Trim();
            string val = line.Substring(eqIdx + 1).Trim().Trim('\"');

            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                currentProg.Name = val;
            }
            else if (int.TryParse(key, out int lineNum))
            {
                // 還原換行符號
                string unescaped = val.Replace("\\n", "\n");
                currentProg.Add(CommandItem.Parse(unescaped, lineNum));
            }
        }

        return result;
    }

    /// <summary>
    /// 解析字串內容格式的 .pro 檔案為 Dictionary(int, ProgramTable)
    /// </summary>
    public static Dictionary<int, ProgramTable> DeserializePro(string content)
    {
        var result = new Dictionary<int, ProgramTable>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        ProgramTable? currentProg = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(';')) continue;

            if (line.StartsWith("[Program ", StringComparison.OrdinalIgnoreCase) && line.EndsWith(']'))
            {
                string numStr = line.Substring(9, line.Length - 10).Trim();
                if (int.TryParse(numStr, out int pNo))
                {
                    currentProg = new ProgramTable { ProgramNumber = pNo };
                    result[pNo] = currentProg;
                }
                continue;
            }

            if (currentProg == null) continue;

            int eqIdx = line.IndexOf('=');
            if (eqIdx <= 0) continue;

            string key = line.Substring(0, eqIdx).Trim();
            string val = line.Substring(eqIdx + 1).Trim().Trim('\"');

            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                currentProg.Name = val;
            }
            else if (int.TryParse(key, out int lineNum))
            {
                string unescaped = val.Replace("\\n", "\n");
                currentProg.Add(CommandItem.Parse(unescaped, lineNum));
            }
        }

        return result;
    }

    /// <summary>
    /// 將單一程式段寫入二進制 .init 檔案
    /// 二進制規範：Row Count (int32) + Col Count (int32 = 2) + 各字串長度與內容
    /// </summary>
    public static void SaveInitFile(string filePath, ProgramTable program)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs, Encoding.UTF8);

        // 1. Header: Row Count + Col Count (2)
        bw.Write(program.Count);
        bw.Write(2);

        // 2. Data Blocks
        for (int i = 1; i <= program.Count; i++)
        {
            var item = program[i];
            byte[] cmdBytes = Encoding.UTF8.GetBytes(item.Command);
            bw.Write(cmdBytes.Length);
            bw.Write(cmdBytes);

            byte[] paramBytes = Encoding.UTF8.GetBytes(item.RawParameter);
            bw.Write(paramBytes.Length);
            bw.Write(paramBytes);
        }
    }

    /// <summary>
    /// 從二進制 .init 檔案載入單一程式段
    /// </summary>
    public static ProgramTable LoadInitFile(string filePath, int progNo = 0, string name = "")
    {
        var prog = new ProgramTable { ProgramNumber = progNo, Name = name };
        if (!File.Exists(filePath)) return prog;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8);

        if (fs.Length < 8) return prog;

        int rowCount = br.ReadInt32();
        int colCount = br.ReadInt32(); // 固定為 2

        for (int i = 0; i < rowCount; i++)
        {
            int cmdLen = br.ReadInt32();
            string cmd = cmdLen > 0 ? Encoding.UTF8.GetString(br.ReadBytes(cmdLen)) : "";

            int paramLen = br.ReadInt32();
            string param = paramLen > 0 ? Encoding.UTF8.GetString(br.ReadBytes(paramLen)) : "";

            prog.Add(new CommandItem(i + 1, $"{cmd}\t{param}", cmd, "", "", param, false, false));
        }

        return prog;
    }
}
