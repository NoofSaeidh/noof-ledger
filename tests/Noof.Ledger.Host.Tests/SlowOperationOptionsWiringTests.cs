using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Tests;

// Task V2: proves appsettings.json's Logging:SlowOperationMs section binds through the real host
// pipeline.
public class SlowOperationOptionsWiringTests
{
    static WebApplicationFactory<Program> Factory(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseTempKeyRingDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            configure?.Invoke(builder);
        });

    [Fact]
    public void IOperationTimer_resolves_as_a_singleton()
    {
        using var factory = Factory();

        var first = factory.Services.GetRequiredService<IOperationTimer>();
        var second = factory.Services.GetRequiredService<IOperationTimer>();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void A_configured_override_reaches_SlowOperationOptions()
    {
        using var factory = Factory(builder => builder.UseSetting("Logging:SlowOperationMs:MODEL", "45000"));

        var options = factory.Services.GetRequiredService<SlowOperationOptions>();

        options.ThresholdMs["model"].Should().Be(45000);
        options.ThresholdMs.Count(pair => string.Equals(pair.Key, "model", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "case-insensitive lookup must not leave a duplicate key behind");
    }

    [Fact]
    public void The_configured_defaults_bind_from_the_real_appsettings_json()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<SlowOperationOptions>();

        options.ThresholdMs["job.queueWait"].Should().Be(30000);
    }

    // Phase 6: receipt.fiscalFetch is a network call to an outside site with no group threshold of
    // its own before this - it fell back to "default" (1000 ms) and would log Slow on nearly every
    // real call to suf.purs.gov.rs.
    [Fact]
    public void The_receipt_group_threshold_binds_from_the_real_appsettings_json()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<SlowOperationOptions>();

        options.ThresholdMs["receipt"].Should().Be(5000);
    }

    // Item E: Receipts:VerificationUrlPrefix is the one source ReceiptLinkDetector and
    // FiscalQrDecoder both read - proving it binds through the real host pipeline is what stands in
    // for a second copy of the constant being left behind.
    [Fact]
    public void The_receipts_verification_url_prefix_binds_from_the_real_appsettings_json()
    {
        using var factory = Factory();

        var verificationUrl = factory.Services.GetRequiredService<Noof.Ledger.Application.Receipts.FiscalVerificationUrl>();

        verificationUrl.Prefix.Should().Be("https://suf.purs.gov.rs/v/?vl=");
        verificationUrl.Host.Should().Be("suf.purs.gov.rs");
        verificationUrl.PathPrefix.Should().Be("/v/");
    }

    // A bad Receipts:VerificationUrlPrefix throws out of AddNoofApplication during Program.cs's own
    // top-level startup - the same InvalidOperationException FiscalVerificationUrlTests proves at the
    // unit level. WebApplicationFactory cannot observe it directly: Program.cs's top-level try/catch
    // (M-2/the two-stage Serilog initialization) deliberately swallows every startup exception into
    // Log.Fatal so a failure is never silent, which means the entry point returns normally and
    // WebApplicationFactory reports "the entry point exited without ever building an IHost" instead of
    // the original exception - FatalStartupExitCodeTests is what proves that path end to end, out of
    // process, for a different bad setting (Capture:TimeZone).
    [Fact]
    public void A_bad_configured_verification_url_prefix_never_reaches_a_built_host()
    {
        var act = () =>
        {
            using var factory = Factory(builder => builder.UseSetting("Receipts:VerificationUrlPrefix", "not a url"));
            _ = factory.Services;
        };

        act.Should().Throw<InvalidOperationException>().WithMessage("*entry point*",
            "this must be WebApplicationFactory's own wrapper for the swallowed startup failure, not some " +
            "unrelated InvalidOperationException the bad setting happened not to cause");
    }
}
