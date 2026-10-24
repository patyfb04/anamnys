namespace Anamnys.Server.Exports;

public interface IAccountExportBuilder
{
    Task BuildAsync(Guid providerId, Stream destination, CancellationToken ct);
}
