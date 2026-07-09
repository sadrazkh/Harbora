namespace Harbora.Web.ViewModels;

public sealed record DomainRow(Guid Id, string Host, Guid AppId, string AppName, bool Ssl, bool Primary);
