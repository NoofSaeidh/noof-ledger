using AwesomeAssertions;

namespace Noof.Ledger.Ai.Tests.Groq;

public class LiveTranscriptionGateTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("gsk-x", null)]
    [InlineData(null, @"C:\voice\note.ogg")]
    [InlineData("  ", @"C:\voice\note.ogg")]
    [InlineData("gsk-x", "  ")]
    public void Stays_closed_unless_both_the_key_and_the_file_are_given(string? key, string? file)
    {
        LiveTranscriptionGate.TryParse(key, file, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Opens_when_both_are_given()
    {
        LiveTranscriptionGate.TryParse("gsk-x", @"C:\voice\note.ogg", out var key, out var file).Should().BeTrue();
        key.Should().Be("gsk-x");
        file.Should().Be(@"C:\voice\note.ogg");
    }
}
