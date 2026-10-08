using AwesomeAssertions;
using Noof.Ledger.Web.Components.Shared;

namespace Noof.Ledger.Host.Tests;

// An exception escaping a Blazor event handler ends the whole circuit - and by the time an abandoned operation fails,
// the operator may already be on another page of that same circuit.
public sealed class OneAtATimeTests
{
    [Fact]
    public async Task An_operation_that_fails_after_the_page_has_gone_lets_nothing_escape()
    {
        var operations = new OneAtATime();
        var clipboard = new TaskCompletionSource();
        using var leaving = new CancellationTokenSource();

        var copying = operations.RunAsync(_ => clipboard.Task, leaving.Token);
        await leaving.CancelAsync();
        clipboard.SetException(new InvalidOperationException("The browser went away with the page."));

        (await copying).Should().BeFalse();
        operations.Busy.Should().BeFalse();
        (await operations.RunAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken))
            .Should().BeTrue("the gate is free for the next page action");
    }

    [Fact]
    public async Task On_a_page_still_open_a_failure_reaches_the_page()
    {
        var operations = new OneAtATime();

        var running = () => operations.RunAsync(
            _ => throw new InvalidOperationException("The database went away."), TestContext.Current.CancellationToken);

        await running.Should().ThrowAsync<InvalidOperationException>();
        operations.Busy.Should().BeFalse();
    }
}
