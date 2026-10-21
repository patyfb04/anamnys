namespace Anamnys.Server.Data.Entities;

// A co-author of a relatório multiprofissional (Res. CFP 006/2019 art. 12). Not an Anamnys user.
public sealed record TeamMember(string Name, string Category, string Registry, string Analysis);
