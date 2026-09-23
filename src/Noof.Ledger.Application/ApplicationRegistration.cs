using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;

namespace Noof.Ledger.Application;

[SuppressMessage("Maintainability", "CA1515",
    Justification = "The one public way into this assembly's own services - ProposalMapper, "
        + "MerchantScan and RecordEcho are internal, and this is the only way the Host and "
        + "Telegram can register them without naming an implementation type.")]
public static class ApplicationRegistration
{
    public static IServiceCollection AddNoofApplication(this IServiceCollection services)
    {
        services.AddSingleton<IProposalMapper, ProposalMapper>();
        services.AddSingleton<IMerchantScan, MerchantScan>();
        services.AddSingleton<IRecordEcho, RecordEcho>();

        return services;
    }
}
