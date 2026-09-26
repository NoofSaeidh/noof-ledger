using AwesomeAssertions;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

// M-5 (Phase 5 final review): a read-only health check must not have the side effect of creating
// directories - and on an unwritable path it silently turned "plenty of space" into a Failing
// "Could not read free disk space".
public class DriveFreeSpaceProviderTests
{
    [Fact]
    public void Reading_free_space_for_a_path_that_does_not_exist_never_creates_it()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"noof-disk-check-missing-{Guid.NewGuid():N}", "nested", "deeper");

        var bytes = new DriveFreeSpaceProvider().GetAvailableFreeBytes(missingPath);

        Directory.Exists(missingPath).Should().BeFalse("a health read must never create the directory it measures");
        bytes.Should().BeGreaterThan(0);
    }
}
