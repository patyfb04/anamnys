namespace Anamnys.Server.Storage;

public static class StorageSetup
{
    public static void AddNoteStorage(this IHostApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        if (PublicUrlProblem(options.PublicUrl, builder.Environment.IsDevelopment()) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<S3NoteFileStore>();
        builder.Services.AddSingleton<INoteFileStore>(sp => sp.GetRequiredService<S3NoteFileStore>());
        builder.Services.AddHostedService<StorageInitializer>();
    }

    // Presigned URLs are signed for this host and opened by the browser from an https page, so
    // outside Development it must be the public TLS host of the store — never the internal one.
    internal static string? PublicUrlProblem(string publicUrl, bool isDevelopment)
    {
        if (isDevelopment)
        {
            return null;
        }
        return Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? null
            : "Storage:PublicUrl must be the browser-reachable https URL of the object store outside Development " +
              "(AppHost parameter storage-public-url). Presigned PDF links are signed for this host.";
    }
}
