namespace MVCON.Core.Model;

public record IoPortSnapshot(string Port, string Alias, bool Value, bool IsVirtual);
