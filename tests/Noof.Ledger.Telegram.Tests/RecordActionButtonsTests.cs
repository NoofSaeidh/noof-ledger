using AwesomeAssertions;
using Noof.Ledger.Application.Chat;

namespace Noof.Ledger.Telegram.Tests;

public class RecordActionButtonsTests
{
    [Theory]
    [InlineData(RecordAction.Cancel)]
    [InlineData(RecordAction.Edit)]
    [InlineData(RecordAction.Restore)]
    public void Every_action_survives_the_round_trip_through_callback_data(RecordAction action)
    {
        var data = RecordActionButtons.ToButton(action).CallbackData;

        RecordActionButtons.TryParse(data, out var parsed).Should().BeTrue();
        parsed.Should().Be(action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("delete")]
    public void Unknown_data_is_not_an_action(string? data)
    {
        RecordActionButtons.TryParse(data, out _).Should().BeFalse();
    }
}
