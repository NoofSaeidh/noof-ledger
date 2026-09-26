using AwesomeAssertions;
using Noof.Ledger.Host.Logging;
using Serilog.Events;

namespace Noof.Ledger.Host.Tests;

public class LogLevelSwitchesTests
{
    [Fact]
    public void All_three_levels_default_to_Information()
    {
        var switches = new LogLevelSwitches();

        switches.Root.MinimumLevel.Should().Be(LogEventLevel.Information);
        switches.Database.MinimumLevel.Should().Be(LogEventLevel.Information);
    }

    [Theory]
    [InlineData(LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Information, LogEventLevel.Debug, LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Warning, LogEventLevel.Warning, LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Verbose, LogEventLevel.Information, LogEventLevel.Verbose)]
    public void Root_is_the_minimum_of_file_and_database_after_SetFileLevel_then_SetDatabaseLevel(
        LogEventLevel file, LogEventLevel database, LogEventLevel expectedRoot)
    {
        var switches = new LogLevelSwitches();

        switches.SetFileLevel(file);
        switches.SetDatabaseLevel(database);

        switches.Root.MinimumLevel.Should().Be(expectedRoot);
    }

    [Theory]
    [InlineData(LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Information, LogEventLevel.Debug, LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Warning, LogEventLevel.Warning, LogEventLevel.Warning)]
    public void Root_is_the_minimum_of_file_and_database_after_SetDatabaseLevel_then_SetFileLevel(
        LogEventLevel file, LogEventLevel database, LogEventLevel expectedRoot)
    {
        var switches = new LogLevelSwitches();

        switches.SetDatabaseLevel(database);
        switches.SetFileLevel(file);

        switches.Root.MinimumLevel.Should().Be(expectedRoot);
    }
}
