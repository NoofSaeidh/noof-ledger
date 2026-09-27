using AwesomeAssertions;

namespace Noof.Ledger.Host.Tests;

// Copilot finding, PR #3: TestHostLogging.DeleteBestEffortAsync did not match the best-effort
// behaviour of its E2E sibling (HostProcess.DeleteBestEffortAsync) - its final IOException escaped
// the filtered catch instead of being swallowed, and it never caught UnauthorizedAccessException at
// all, so a Windows-held log file could fail a test during cleanup instead of being retried and
// ignored. Both now delegate to the one shared implementation in TestKit.
public class TestHostLoggingDeleteTests
{
    [Fact]
    public async Task A_directory_with_a_file_held_open_is_not_deleted_immediately_but_the_call_never_throws()
    {
        var directory = Directory.CreateTempSubdirectory("noof-host-test-logs-delete-").FullName;
        var filePath = Path.Combine(directory, "held.log");
        await using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var act = () => TestHostLogging.DeleteBestEffortAsync(directory);

            await act.Should().NotThrowAsync("a file the OS still holds open must be tolerated, not fault the test's own cleanup");
        }

        Directory.Exists(directory).Should().BeTrue("the delete could not have succeeded while the file was held open");

        await TestHostLogging.DeleteBestEffortAsync(directory);

        Directory.Exists(directory).Should().BeFalse("once the handle is released, a later best-effort delete succeeds");
    }
}
