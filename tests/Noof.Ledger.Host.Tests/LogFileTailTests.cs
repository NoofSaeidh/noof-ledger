using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests;

public sealed class LogFileTailTests : IDisposable
{
    readonly DirectoryInfo directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"noof-log-tail-{Guid.NewGuid():N}"));

    [Fact]
    public async Task Reads_the_last_N_lines_of_the_newest_log_file()
    {
        var older = Path.Combine(directory.FullName, "noof-ledger-20260101.log");
        var newer = Path.Combine(directory.FullName, "noof-ledger-20260102.log");
        await File.WriteAllLinesAsync(older, ["old line 1", "old line 2"], TestContext.Current.CancellationToken);
        await File.WriteAllLinesAsync(newer, ["line 1", "line 2", "line 3", "line 4"], TestContext.Current.CancellationToken);
        // A distinct write time (rather than relying on file-name ordering, which the real
        // implementation does not use) - the newest file is chosen by LastWriteTimeUtc.
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var tail = new LogFileTail(Configuration(directory.FullName));

        var lines = await tail.ReadLastLinesAsync(2, TestContext.Current.CancellationToken);

        lines.Should().Equal("line 3", "line 4");
    }

    [Fact]
    public async Task Returns_an_empty_list_when_no_log_file_exists_yet()
    {
        var tail = new LogFileTail(Configuration(directory.FullName));

        var lines = await tail.ReadLastLinesAsync(500, TestContext.Current.CancellationToken);

        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Reads_a_file_that_is_open_for_writing_elsewhere()
    {
        // FileShare.ReadWrite is the whole point: Serilog's file sink holds the log file open for
        // writing for as long as the host runs, so a tail that cannot share the handle can never
        // read anything.
        var path = Path.Combine(directory.FullName, "noof-ledger-20260103.log");
        await using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        await using (var streamWriter = new StreamWriter(writer))
        {
            await streamWriter.WriteLineAsync("still being written");
            await streamWriter.FlushAsync(TestContext.Current.CancellationToken);

            var tail = new LogFileTail(Configuration(directory.FullName));
            var lines = await tail.ReadLastLinesAsync(10, TestContext.Current.CancellationToken);

            lines.Should().Equal("still being written");
        }
    }

    static IConfiguration Configuration(string directory) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:Directory"] = directory })
            .Build();

    public void Dispose() => directory.Delete(recursive: true);
}
