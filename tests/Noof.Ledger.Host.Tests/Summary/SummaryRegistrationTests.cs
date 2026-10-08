using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Reporting.Summary;

namespace Noof.Ledger.Host.Tests.Summary;

public class SummaryRegistrationTests
{
    [Fact]
    public void AddNoofApplication_registers_one_summary_text_for_the_whole_process()
    {
        var services = new ServiceCollection();
        services.AddNoofApplication(
            new SlowOperationOptions(),
            new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMonthlySummaryText>().Should().BeOfType<MonthlySummaryText>()
            .And.BeSameAs(provider.GetRequiredService<IMonthlySummaryText>());
    }
}
