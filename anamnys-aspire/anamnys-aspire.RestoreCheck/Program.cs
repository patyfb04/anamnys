using System.Text.Json;
using Anamnys.RestoreCheck;
using Anamnys.Server.Integrity;
using Anamnys.Server.Storage;

// Usage: RestoreCheck --connection <anamnysdb> --keycloak-connection <keycloakdb>
//   --s3-endpoint <url> --s3-bucket <name> --s3-access-key <key> --s3-secret-key <secret>
//   [--baseline-connection <live anamnysdb, read-only; needs --source-s3-*>] [--counts <snapshot.json>] [--sample 200] [--tsa-root <root.pem>]
//   [--source-s3-endpoint <url> --source-s3-bucket <name> --source-s3-access-key <key> --source-s3-secret-key <secret>]
// Every option can also come from the environment (RESTORECHECK_CONNECTION, RESTORECHECK_S3_SECRET_KEY, ...).
// Prints {"passed","checks":[{"name","passed","detail"}],"durationSeconds"} to stdout; exit code 0 when passed, else 1.
var options = CliOptions.Parse(args);
RestoreCheckReport report;
try
{
    var snapshot = await SnapshotFile.LoadAsync(options);
    var tsaRoot = options.Get("tsa-root") is { Length: > 0 } rootPath ? await File.ReadAllTextAsync(rootPath) : "";
    using var http = new HttpClient();
    var authority = new HttpTimestampAuthority(http, new IntegrityOptions { TsaRootCertificate = tsaRoot });
    using var store = RestoreOutput.Store(options, "s3");
    using var source = options.Get("source-s3-endpoint") is { Length: > 0 } ? RestoreOutput.Store(options, "source-s3") : null;
    var baseline = options.Get("baseline-connection") is { Length: > 0 } b ? b : null;
    if (baseline is not null && source is null)
    {
        throw new ArgumentException("--baseline-connection needs the --source-s3-* settings.");
    }
    var checker = new RestoreChecker(
        options.Required("connection"), options.Required("keycloak-connection"), store, authority, source, baseline);
    int.TryParse(options.Get("sample"), out var sample);
    report = await checker.RunAsync(snapshot, sample > 0 ? sample : RestoreChecker.DefaultSample, CancellationToken.None);
}
catch (Exception e)
{
    // Arguments, snapshot or root unreadable: the type only, never a message that could carry a secret.
    report = new RestoreCheckReport(false, [new CheckResult("setup", false, $"could not start: {e.GetType().Name}")], 0);
}
Console.Out.WriteLine(RestoreOutput.Render(report));
return report.Passed ? 0 : 1;

