namespace Anamnys.Server.Storage;

public sealed class StorageOptions
{
    public string ServiceUrl { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "notes";
}
