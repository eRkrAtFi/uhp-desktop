using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QemuManager.Services;

/// <summary>
/// Splits/joins a free-form command-line-style string the same way
/// VmProcessManager's own ExtraArgs field always has - shared with
/// EnvironmentMigration, which needs the identical quoting rules to find
/// and remove specific flags (-kernel/-initrd/-append) from an old
/// ExtraArgs string during its own v1-to-v2 migration step (see its own
/// remarks) without mangling whatever quoted, space-containing paths are
/// left in the remainder.
/// </summary>
public static class ArgumentTokenizer
{
    /// <summary>Splits on whitespace, honoring double quotes - enough for
    /// the common case of a quoted path containing spaces, without
    /// pulling in a full shell parser.</summary>
    public static List<string> Split(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in input)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>The inverse of Split - re-quotes any token containing
    /// whitespace so the result round-trips back through Split correctly.</summary>
    public static string Join(IEnumerable<string> tokens) =>
        string.Join(" ", tokens.Select(t => t.Any(char.IsWhiteSpace) ? $"\"{t}\"" : t));
}
