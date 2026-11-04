using System.Text;
using ARCOS.Core.Execution;

namespace ARCOS.Core.Serialization;

/// <summary>
/// ARCOS ?³æœ¬?‡å?æ¡ˆæ?åºå??–å™¨
/// ?¯æ´ .pro (Windows INI æ¨™æ?å¤šå?æ®µå?æ¡ˆæ?) ??.init (?®ä?ç¨‹å??³æœ¬) è®€å¯«ï?
/// ä¸¦å??Ÿæ”¯?´å?è¡?Parameter (?¸å®¹ Motion@ExtraCMD ?‹è?å¤šè??³æœ¬??Python ?Ÿå?ç¢???
/// </summary>
public static class ScriptSerializer
{
    /// <summary>
    /// å°‡å???ProgramTable ?²å???.pro æª”æ? (INI ?¼å?)
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
                // è½‰ç¾©?›è?ç¬¦è?ä»¥ç›¸å®¹å–®è¡?INI
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
    /// å¾?.pro æª”æ?è¼‰å…¥?€?‰ç?å¼å?æ®?
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
                // ?„å??›è?ç¬¦è?
                string unescaped = val.Replace("\\n", "\n");
                currentProg.Add(CommandItem.Parse(unescaped, lineNum));
            }
        }

        return result;
    }

    /// <summary>
    /// è§??å­—ä¸²?§å®¹?¼å???.pro æª”æ???Dictionary(int, ProgramTable)
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
    /// å°?ProgramTable è½‰ç‚ºä¹¾æ·¨?ç„¡ INI è¡Œè??æ”¯?´å?è¡Œé??Ÿç?ç´”æ?ä»¤è…³??(ä¾?Meld æ¯”å??‡ç·¨è¼¯å??•ç?)
    /// </summary>
    public static string ProgramToCleanScript(ProgramTable prog)
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= prog.Count; i++)
        {
            var item = prog[i];
            if (item.IsEmpty) continue;
            if (item.IsComment)
            {
                sb.AppendLine($"// {item.RawParameter}");
            }
            else if (string.IsNullOrEmpty(item.RawParameter))
            {
                sb.AppendLine($"{item.Command}::;");
            }
            else
            {
                sb.AppendLine($"{item.Command}::{item.RawParameter};");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// å°?Meld ç·¨è¼¯å¾Œç?ç´”æ?ä»¤è…³?¬é??°è§£?ä¸¦?ªå?ç·¨è???ProgramTable (Meld ç·¨è¼¯å¾Œè??†é???
    /// </summary>
    public static ProgramTable CleanScriptToProgram(string scriptText, int progNumber, string progName = "")
    {
        var prog = new ProgramTable { ProgramNumber = progNumber, Name = progName };
        var lines = scriptText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int lineNum = 1;
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string trimmed = raw.Trim();
            if (trimmed.StartsWith("//") || trimmed.StartsWith(';'))
            {
                prog.Add(new CommandItem(lineNum, raw, "", "System", "#Comment", trimmed.TrimStart('/', ';', ' '), true, false));
                lineNum++;
            }
            else
            {
                var item = CommandItem.Parse(raw, lineNum);
                if (!item.IsEmpty)
                {
                    prog.Add(item);
                    lineNum++;
                }
            }
        }
        return prog;
    }

    /// <summary>
    /// å°‡å–®ä¸€ç¨‹å?æ®µå¯«?¥ä??²åˆ¶ .init æª”æ?
    /// äºŒé€²åˆ¶è¦ç?ï¼šRow Count (int32) + Col Count (int32 = 2) + ?„å?ä¸²é•·åº¦è??§å®¹
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
    /// å¾ä??²åˆ¶ .init æª”æ?è¼‰å…¥?®ä?ç¨‹å?æ®?
    /// </summary>
    public static ProgramTable LoadInitFile(string filePath, int progNo = 0, string name = "")
    {
        var prog = new ProgramTable { ProgramNumber = progNo, Name = name };
        if (!File.Exists(filePath)) return prog;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8);

        if (fs.Length < 8) return prog;

        int rowCount = br.ReadInt32();
        int colCount = br.ReadInt32(); // ?ºå???2

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
