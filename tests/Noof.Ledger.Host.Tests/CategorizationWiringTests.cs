using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Transcription;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.Persistence.Jobs;

namespace Noof.Ledger.Host.Tests;

public class CategorizationWiringTests
{
    static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public void Every_scoped_categorization_port_resolves_without_touching_the_database()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IJobQueue>().Should().BeOfType<EfJobQueue>();
        scope.ServiceProvider.GetRequiredService<ICategorizationStore>();
        scope.ServiceProvider.GetRequiredService<ICategoryCatalog>();
        scope.ServiceProvider.GetRequiredService<IMerchantDirectory>();
        scope.ServiceProvider.GetRequiredService<ICategorizer>();
        scope.ServiceProvider.GetRequiredService<IModelProvider>();
        scope.ServiceProvider.GetRequiredService<Noof.Ledger.Application.Editing.IRecordEditor>();
        scope.ServiceProvider.GetRequiredService<Noof.Ledger.Application.Wallets.IWalletDirectory>();
    }

    [Fact]
    public void CategorizationWorker_is_registered_as_a_hosted_service()
    {
        using var factory = Factory();

        factory.Services.GetServices<IHostedService>().Should().Contain(service => service is CategorizationWorker);
    }

    [Fact]
    public void CategorizationWorkerOptions_is_a_singleton_with_its_documented_defaults()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<CategorizationWorkerOptions>();

        options.MaxAttempts.Should().Be(8);
        options.MerchantHintLimit.Should().Be(10);
        options.MaxCanonicalizationsPerJob.Should().Be(3);
        options.DefaultCurrency.Should().Be("RSD");
    }

    [Fact]
    public void Every_scoped_transcription_port_resolves_without_touching_the_database()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITranscriptionStore>();
        scope.ServiceProvider.GetRequiredService<ITranscriber>();
        scope.ServiceProvider.GetRequiredService<ISpeechProvider>();
        scope.ServiceProvider.GetRequiredService<IVoiceFileSource>();
    }

    [Fact]
    public void TranscriptionWorker_is_registered_as_a_hosted_service()
    {
        using var factory = Factory();

        factory.Services.GetServices<IHostedService>().Should().Contain(service => service is TranscriptionWorker);
    }
}
