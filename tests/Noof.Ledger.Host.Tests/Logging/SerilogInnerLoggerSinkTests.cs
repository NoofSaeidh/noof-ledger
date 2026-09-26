using AwesomeAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Noof.Ledger.Host.Tests.Logging;

// Final review, major: CLAUDE.md and LoggingSetup's comments claimed an inner LoggerConfiguration
// used as a WriteTo.Sink target "does enforce its own default Information floor", so
// `.MinimumLevel.Verbose()` on that inner logger was needed to stop it re-filtering. Serilog.Core.Logger
// implements ILogEventSink.Emit by dispatching straight to its sink pipeline with no level check -
// only ILogger.Write (what Log.Information/logger.Information(...) call) checks IsEnabled first. These
// two tests pin down which is true against the resolved Serilog version (4.4.0) so a future Serilog
// upgrade that changed this would be caught here, not discovered as a silent gap in production.
public class SerilogInnerLoggerSinkTests
{
    [Fact]
    public void An_inner_logger_used_as_an_ILogEventSink_Emit_target_bypasses_its_own_MinimumLevel()
    {
        var captured = new List<LogEvent>();
        var inner = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new DelegatingSink(captured.Add))
            .CreateLogger();

        ((ILogEventSink)inner).Emit(DebugEvent());

        captured.Should().ContainSingle(
            "Logger.Emit dispatches to the sink pipeline unconditionally - the inner logger's own " +
            "Information floor never sees this call, so a Debug event let through by an outer levelSwitch " +
            "reaches it regardless of the inner logger's MinimumLevel");
    }

    [Fact]
    public void An_inner_loggers_own_Write_method_does_honour_its_MinimumLevel()
    {
        var captured = new List<LogEvent>();
        var inner = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new DelegatingSink(captured.Add))
            .CreateLogger();

        inner.Write(DebugEvent());

        captured.Should().BeEmpty(
            "ILogger.Write (what Information()/Debug() call on the logger itself) applies IsEnabled " +
            "against the logger's own MinimumLevel before it ever reaches Emit - unlike calling Emit directly");
    }

    static LogEvent DebugEvent() => new(
        DateTimeOffset.UtcNow, LogEventLevel.Debug, null,
        new Serilog.Parsing.MessageTemplateParser().Parse("probe"), []);

    sealed class DelegatingSink(Action<LogEvent> onEmit) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => onEmit(logEvent);
    }
}
