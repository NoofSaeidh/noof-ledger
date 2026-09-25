using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Transcription;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class AiKeysHealthCheckTests
{
    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Ready);
        return gate;
    }

    static IModelProvider ModelProvider(bool configured)
    {
        var provider = Substitute.For<IModelProvider>();
        provider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(configured);
        provider.SecretLabel.Returns("Anthropic API key");
        return provider;
    }

    static ISpeechProvider SpeechProvider(bool configured)
    {
        var provider = Substitute.For<ISpeechProvider>();
        provider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(configured);
        provider.SecretLabel.Returns("Groq API key");
        return provider;
    }

    [Fact]
    public async Task Reports_degraded_when_the_gate_is_not_ready()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Waiting);
        var check = new AiKeysHealthCheck(gate, ModelProvider(true), SpeechProvider(true));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task Both_keys_present_is_healthy()
    {
        var check = new AiKeysHealthCheck(ReadyGate(), ModelProvider(true), SpeechProvider(true));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task A_missing_model_key_is_unhealthy_and_names_it()
    {
        var check = new AiKeysHealthCheck(ReadyGate(), ModelProvider(false), SpeechProvider(true));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Anthropic API key");
    }

    [Fact]
    public async Task A_missing_speech_key_is_unhealthy_and_names_it()
    {
        var check = new AiKeysHealthCheck(ReadyGate(), ModelProvider(true), SpeechProvider(false));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Groq API key");
    }
}
