using System.Text;
using System.Text.RegularExpressions;

namespace Anamnys.Server.NoteStructuring;

// GBNF that only admits a JSON object with exactly the given keys, in order, each a JSON string.
public static partial class NoteGrammar
{
    public static string For(IReadOnlyList<string> keys)
    {
        var root = new StringBuilder("root ::= \"{\" ws ");
        for (var i = 0; i < keys.Count; i++)
        {
            if (!SafeKey().IsMatch(keys[i]))
            {
                throw new ArgumentException($"Section key '{keys[i]}' is not a plain identifier.", nameof(keys));
            }
            if (i > 0)
            {
                root.Append("\",\" ws ");
            }
            root.Append($"\"\\\"{keys[i]}\\\"\" ws \":\" ws string ws ");
        }
        root.Append("\"}\"");
        return string.Join('\n',
            root.ToString(),
            "string ::= \"\\\"\" char* \"\\\"\"",
            "char ::= [^\"\\\\\\x00-\\x1f] | \"\\\\\" ([\"\\\\/bfnrt] | \"u\" hex hex hex hex)",
            "hex ::= [0-9a-fA-F]",
            "ws ::= [ \\t\\n]?");
    }

    [GeneratedRegex("^[a-z]+$")]
    private static partial Regex SafeKey();
}
