using Harbora.Domain.Mail;
using Harbora.Domain.Servers;

namespace Harbora.Web.ViewModels;

public sealed class MailPageViewModel
{
    public MailServer? Server { get; init; }
    public IReadOnlyList<MailDomain> Domains { get; init; } = [];
    public IReadOnlyList<Server> AvailableServers { get; init; } = [];
    public bool CanManagePlatform { get; init; }
    public string Currency { get; init; } = "IRR";
}

