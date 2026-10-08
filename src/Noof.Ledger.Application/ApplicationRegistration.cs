using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Application;

[SuppressMessage("Maintainability", "CA1515",
    Justification = "The one public way into this assembly's own services - ProposalMapper, "
        + "MerchantScan and RecordEcho are internal, and this is the only way the Host and "
        + "Telegram can register them without naming an implementation type.")]
public static class ApplicationRegistration
{
    public static IServiceCollection AddNoofApplication(
        this IServiceCollection services,
        SlowOperationOptions slowOperations,
        FiscalVerificationUrlOptions fiscalVerificationUrlOptions)
    {
        services.AddSingleton<IProposalMapper, ProposalMapper>();
        services.AddSingleton<IMerchantScan, MerchantScan>();
        services.AddSingleton<IRecordEcho, RecordEcho>();
        services.AddSingleton<IFindingText, FindingText>();
        services.AddSingleton<IBugReportMarkdown, BugReportMarkdown>();

        services.AddSingleton(slowOperations);
        services.AddSingleton<IOperationTimer, OperationTimer>();

        // Constructed here, not lazily behind DI, so a bad Receipts:VerificationUrlPrefix fails
        // startup immediately instead of on the first message with a fiscal link.
        services.AddSingleton<IFiscalVerificationUrl>(new FiscalVerificationUrl(fiscalVerificationUrlOptions));

        return services;
    }
}
