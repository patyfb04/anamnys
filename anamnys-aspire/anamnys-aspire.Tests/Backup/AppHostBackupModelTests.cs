using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using FluentAssertions;

namespace Anamnys.Tests.Backup;

// design/specs/2026-11-05-backup-restore-design.md §2.1–2.3, §6 (AppHost). Model inspection only: the AppHost runs up to
// builder.Build(), nothing is built or started, so no Docker and no shared fixture are needed.
public class AppHostBackupModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The resource graph of a dev run before F-46 (AppHost.cs at 7c7d3ff), backup-enabled unset, Mailpit as the mail
    // catcher (an empty Resend key, as SharedAppHostFixture passes).
    private static readonly string[] GraphBeforeBackup =
    [
        "admin", "admin-installer", "anamnysdb", "cache", "clamav", "keycloak", "keycloak-admin-password",
        "keycloak-admin-username", "keycloakdb", "mailpit", "owner-client-secret", "patient", "patient-client-secret",
        "patient-installer", "postgres", "provider", "provider-client-secret", "provider-installer", "server",
        "server-rebuilder", "storage", "storage-access-key", "storage-secret-key", "web", "web-installer",
    ];

    private static readonly string[] WalGVariables =
    [
        "WALG_S3_PREFIX", "AWS_ENDPOINT", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_S3_FORCE_PATH_STYLE",
        "WALG_LIBSODIUM_KEY", "WALG_LIBSODIUM_KEY_TRANSFORM", "WALG_COMPRESSION_METHOD",
    ];

    private static readonly string[] PublishParameters =
    [
        "backup-s3-endpoint", "backup-s3-bucket", "backup-s3-access-key", "backup-s3-secret-key",
        "backup-encryption-key", "backup-alert-email", "backup-agent-db-password",
    ];

    // An empty Resend key keeps the graph independent of a developer's own user secrets (see SharedAppHostFixture).
    private static Task<IDistributedApplicationTestingBuilder> AppHostAsync(params string[] args) =>
        DistributedApplicationTestingBuilder.CreateAsync<Projects.anamnys_aspire_AppHost>(
            ["--Parameters:resend-api-key=", .. args], Ct);

    private static async Task<Dictionary<string, string>> EnvironmentAsync(IResource resource) =>
        await ((IResourceWithEnvironment)resource).GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Publish);

    private static async Task<string[]> ArgumentsAsync(IResource resource) =>
        await ((IResourceWithArgs)resource).GetArgumentValuesAsync(DistributedApplicationOperation.Publish);

    [Fact]
    public async Task Backup_unset_leaves_the_dev_graph_unchanged()
    {
        // Pinned off: a `backup-enabled=true` user secret left over from a drill must not change what this test sees.
        await using var appHost = await AppHostAsync("--Parameters:backup-enabled=false");

        appHost.ExecutionContext.IsRunMode.Should().BeTrue();
        appHost.Resources.Select(r => r.Name).Order(StringComparer.Ordinal)
            .Should().Equal(GraphBeforeBackup.Order(StringComparer.Ordinal));

        var postgres = appHost.Resources.Single(r => r.Name == "postgres");
        postgres.Annotations.OfType<DockerfileBuildAnnotation>().Should().BeEmpty();
        var image = postgres.Annotations.OfType<ContainerImageAnnotation>().Single();
        image.Registry.Should().Be("docker.io");
        image.Image.Should().Be("library/postgres");
        image.Tag.Should().Be("18.3");
        (await ArgumentsAsync(postgres)).Should().BeEmpty();
        (await EnvironmentAsync(postgres)).Keys.Should().NotContain(WalGVariables).And.NotContain("AWS_REGION").And.NotContain("PGCTLTIMEOUT");

        (await EnvironmentAsync(appHost.Resources.Single(r => r.Name == "server"))).Keys
            .Should().NotContain(k => k.StartsWith("Backup__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backup_enabled_in_a_run_adds_archiving_the_agent_and_the_local_store()
    {
        await using var appHost = await AppHostAsync(
            "--Parameters:backup-enabled=true",
            "--Parameters:backup-store-access-key=testaccesskey0123456789abcdefABCD",
            "--Parameters:backup-store-secret-key=testsecretkey0123456789abcdefABCD",
            "--Parameters:backup-agent-db-password=testagentpassword0123456789abcdef",
            "--Parameters:backup-encryption-key=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            "--Parameters:backup-alert-email=admin@test.local");

        var names = appHost.Resources.Select(r => r.Name).ToList();
        names.Should().Contain(["backup-agent", "backup-store"]);
        names.Should().Contain(GraphBeforeBackup);

        var postgres = appHost.Resources.Single(r => r.Name == "postgres");
        postgres.Annotations.OfType<DockerfileBuildAnnotation>().Should().ContainSingle();
        (await ArgumentsAsync(postgres)).Should().Equal(
            "-c", "archive_mode=on", "-c", "archive_command=timeout 60 wal-g wal-push %p", "-c", "archive_timeout=60",
            "-c", "wal_level=replica", "-c", "hba_file=/etc/postgresql/anamnys/pg_hba.conf");
        var postgresEnv = await EnvironmentAsync(postgres);
        postgresEnv.Keys.Should().Contain(WalGVariables);
        postgresEnv["WALG_LIBSODIUM_KEY_TRANSFORM"].Should().Be("base64");
        postgresEnv["WALG_COMPRESSION_METHOD"].Should().Be("brotli");
        postgresEnv["AWS_S3_FORCE_PATH_STYLE"].Should().Be("true");
        postgresEnv["WALG_S3_PREFIX"].Should().Be("s3://anamnys-backup/pg");
        postgresEnv["PGCTLTIMEOUT"].Should().Be("300");

        var agentEnv = await EnvironmentAsync(appHost.Resources.Single(r => r.Name == "backup-agent"));
        agentEnv.Keys.Should().Contain(WalGVariables);
        agentEnv.Keys.Should().Contain(["PGHOST", "PGPORT", "PGUSER", "PGPASSWORD", "BACKUP_AGENT_DB_PASSWORD",
            "SOURCE_S3_ENDPOINT", "BACKUP_S3_BUCKET", "BACKUP_ENCRYPTION_KEY"]);

        var serverEnv = await EnvironmentAsync(appHost.Resources.Single(r => r.Name == "server"));
        serverEnv["Backup__Enabled"].Should().Be("true");
        serverEnv.Keys.Should().Contain("Backup__AlertEmail");
    }

    [Fact]
    public async Task Publish_requires_the_backup_parameters_and_has_no_local_store()
    {
        var output = Path.Combine(Path.GetTempPath(), "anamnys-apphost-publish-" + Guid.NewGuid().ToString("N"));
        await using var appHost = await AppHostAsync("--operation", "publish", "--publisher", "manifest", "--output-path", output);

        appHost.ExecutionContext.IsPublishMode.Should().BeTrue();
        var parameters = appHost.Resources.OfType<ParameterResource>().ToDictionary(p => p.Name);
        parameters.Keys.Should().Contain(PublishParameters);
        foreach (var name in PublishParameters)
        {
            parameters[name].Default.Should().BeNull($"{name} must be supplied by the deployment");
        }
        parameters["backup-s3-secret-key"].Secret.Should().BeTrue();
        parameters["backup-encryption-key"].Secret.Should().BeTrue();
        parameters["backup-agent-db-password"].Secret.Should().BeTrue();

        var names = appHost.Resources.Select(r => r.Name).ToList();
        names.Should().Contain("backup-agent").And.NotContain("backup-store");
    }
}
