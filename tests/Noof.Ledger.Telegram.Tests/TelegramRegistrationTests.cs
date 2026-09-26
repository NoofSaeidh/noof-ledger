using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noof.Ledger.Application;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Transcription;
using NSubstitute;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramRegistrationTests
{
    [Fact]
    public void AddNoofTelegram_registers_the_notifier_the_router_and_the_poller()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        // TelegramPollingService takes IConfiguration (it reads Capture:TimeZone per update). The
        // Host always has one; a bare ServiceCollection does not, and GetServices<IHostedService>()
        // constructs the service, so without this the test fails on a missing dependency rather
        // than on the behaviour under test.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(_ =>
        {
            var gate = Substitute.For<IDatabaseGate>();
            gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            return gate;
        });
        services.AddSingleton(_ => Substitute.For<IPollingHeartbeat>());
        services.AddNoofApplication(new SlowOperationOptions());
        services.AddScoped(_ => Substitute.For<ISecretStore>());
        services.AddScoped(_ => Substitute.For<Application.Capture.ICaptureStore>());
        services.AddScoped(_ => Substitute.For<Application.Editing.IRecordEditor>());
        services.AddScoped(_ => Substitute.For<Application.Categorization.ICategorizationStore>());
        services.AddScoped(_ => Substitute.For<IRecordEcho>());
        services.AddScoped(_ => Substitute.For<ISystemHealth>());
        services.AddNoofTelegram();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IChatNotifier>().Should().BeOfType<TelegramChatNotifier>();
        scope.ServiceProvider.GetRequiredService<ITelegramUpdateRouter>().Should().BeOfType<TelegramUpdateRouter>();
        scope.ServiceProvider.GetRequiredService<IVoiceFileSource>().Should().BeOfType<TelegramVoiceFileSource>();
        provider.GetServices<IHostedService>().OfType<TelegramPollingService>().Should().ContainSingle();
    }
}
