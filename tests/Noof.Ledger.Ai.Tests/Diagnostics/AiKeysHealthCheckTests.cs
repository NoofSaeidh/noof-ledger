using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Ai.Diagnostics;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Transcription;

namespace Noof.Ledger.Ai.Tests.Diagnostics;

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
    public void Reports_its_name_order_and_log_category()
    {
        var check = new AiKeysHealthCheck(ReadyGate(), ModelProvider(true), SpeechProvider(true));

        check.Name.Should().Be("AI keys");
        check.Order.Should().Be(40);
        check.LogCategory.Should().Be("Noof.Ledger.Ai");
    }

    [Fact]
    public async Task Reports_a_warning_when_the_gate_is_not_ready()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(DatabaseState.Waiting);
        var check = new AiKeysHealthCheck(gate, ModelProvider(true), SpeechProvider(true));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
    }

    [Fact]
    public async Task Both_keys_present_is_ok()
    {
        var check = new AiKeysHealthCheck(ReadyGate(), ModelProvider(true), SpeechProvider(true));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Ok);
    }

    [Fact]
    public async Task A_missing_model_key_is_failing_and_names_it()
    {
        var check = new AiKeysHealthCheck(ReadyGate(), ModelProvider(false), SpeechProvider(true));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Failing);
        result.Summary.Should().Contain("Anthropic API key");
    }

    [Fact]
    public async Task A_missing_speech_key_is_failing_and_names_it()
    {
        var check = new AiKeysHealthCheck(ReadyGate(), ModelProvider(true), SpeechProvider(false));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Failing);
        result.Summary.Should().Contain("Groq API key");
    }
}
