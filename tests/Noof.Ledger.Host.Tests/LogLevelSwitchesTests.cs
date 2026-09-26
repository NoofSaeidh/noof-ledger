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

    // Console is pinned to Fatal in these two theories so it never binds - they isolate file and
    // database exactly as before the console dimension was added; Root_is_the_minimum_of_file_console_
    // and_database below is what actually exercises console's own participation in the minimum.
    [Theory]
    [InlineData(LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Information, LogEventLevel.Debug, LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Warning, LogEventLevel.Warning, LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Verbose, LogEventLevel.Information, LogEventLevel.Verbose)]
    public void Root_is_the_minimum_of_file_and_database_after_SetFileLevel_then_SetDatabaseLevel(
        LogEventLevel file, LogEventLevel database, LogEventLevel expectedRoot)
    {
        var switches = new LogLevelSwitches();
        switches.SetConsoleLevel(LogEventLevel.Fatal);

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
        switches.SetConsoleLevel(LogEventLevel.Fatal);

        switches.SetDatabaseLevel(database);
        switches.SetFileLevel(file);

        switches.Root.MinimumLevel.Should().Be(expectedRoot);
    }

    [Theory]
    [InlineData(LogEventLevel.Information, LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Information, LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Verbose, LogEventLevel.Verbose)]
    [InlineData(LogEventLevel.Warning, LogEventLevel.Warning, LogEventLevel.Warning, LogEventLevel.Warning)]
    public void Root_is_the_minimum_of_file_console_and_database(
        LogEventLevel file, LogEventLevel console, LogEventLevel database, LogEventLevel expectedRoot)
    {
        var switches = new LogLevelSwitches();

        switches.SetFileLevel(file);
        switches.SetConsoleLevel(console);
        switches.SetDatabaseLevel(database);

        switches.Root.MinimumLevel.Should().Be(expectedRoot);
    }

    [Fact]
    public void SetConsoleLevel_can_be_called_before_or_after_the_other_setters_with_the_same_result()
    {
        var setConsoleFirst = new LogLevelSwitches();
        setConsoleFirst.SetConsoleLevel(LogEventLevel.Debug);
        setConsoleFirst.SetFileLevel(LogEventLevel.Warning);
        setConsoleFirst.SetDatabaseLevel(LogEventLevel.Warning);

        var setConsoleLast = new LogLevelSwitches();
        setConsoleLast.SetFileLevel(LogEventLevel.Warning);
        setConsoleLast.SetDatabaseLevel(LogEventLevel.Warning);
        setConsoleLast.SetConsoleLevel(LogEventLevel.Debug);

        setConsoleFirst.Root.MinimumLevel.Should().Be(LogEventLevel.Debug);
        setConsoleLast.Root.MinimumLevel.Should().Be(LogEventLevel.Debug);
    }
}
