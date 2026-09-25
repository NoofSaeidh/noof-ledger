using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Receipts.FiscalQr;
using Noof.Ledger.Receipts.Qr;
using Noof.Ledger.Receipts.Suf;

namespace Noof.Ledger.Receipts;

[SuppressMessage("Maintainability", "CA1515",
    Justification = "The one public type in this assembly, and the only way the Host can register "
        + "IFiscalQrDecoder, IQrReader and IFiscalReceiptClient without naming an implementation.")]
public static class ReceiptsRegistration
{
    const string HttpClientName = "suf-purs";

    public static IServiceCollection AddNoofReceipts(this IServiceCollection services)
    {
        // No credential rides on this client - suf.purs.gov.rs is a public, unauthenticated
        // verification endpoint - but RemoveAllLoggers keeps the pattern consistent with every
        // other outbound HttpClient in this repo, and a request URL still carries the receipt's
        // own vl value, which is a personal fiscal record.
        services.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15)).RemoveAllLoggers();

        services.AddSingleton<IFiscalQrDecoder, FiscalQrDecoder>();
        services.AddSingleton<IQrReader, ZxingQrReader>();
        services.AddScoped<IFiscalReceiptClient>(sp =>
            new SufReceiptClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));

        return services;
    }
}
