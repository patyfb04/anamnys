using System.Text.Json;
using Anamnys.Server.Storage;

namespace Anamnys.RestoreCheck;

internal sealed class CliOptions(Dictionary<string, string> values)
{
    public static CliOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                values[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
            }
        }
        return new CliOptions(values);
    }

    public string? Get(string name) =>
        values.TryGetValue(name, out var value)
            ? value
            : Environment.GetEnvironmentVariable("RESTORECHECK_" + name.Replace('-', '_').ToUpperInvariant());

    public string Required(string name) =>
        Get(name) is { Length: > 0 } value ? value : throw new ArgumentException($"--{name} is required.");
}

internal static class RestoreOutput
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Render(RestoreCheckReport report) => JsonSerializer.Serialize(report, Json);

    public static S3NoteFileStore Store(CliOptions options, string prefix) =>
        new(new StorageOptions
        {
            ServiceUrl = options.Required(prefix + "-endpoint"),
            PublicUrl = options.Required(prefix + "-endpoint"),
            Bucket = options.Required(prefix + "-bucket"),
            AccessKey = options.Required(prefix + "-access-key"),
            SecretKey = options.Required(prefix + "-secret-key"),
        });
}

internal static class SnapshotFile
{
    // Null when --counts was not given at all (the check is then reported as skipped). When it was given, a missing value,
    // an unreadable file or an empty snapshot throws: a restore gate must not pass because its input was lost.
    public static async Task<CountSnapshot?> LoadAsync(CliOptions options)
    {
        var path = options.Get("counts");
        if (path is null)
        {
            return null;
        }
        if (path.Length == 0)
        {
            throw new ArgumentException("--counts needs a path.");
        }
        var snapshot = JsonSerializer.Deserialize<CountSnapshot>(await File.ReadAllTextAsync(path), RestoreOutput.Json);
        return snapshot?.Tables is { Count: > 0 } ? snapshot : throw new InvalidDataException("The snapshot has no tables.");
    }
}
