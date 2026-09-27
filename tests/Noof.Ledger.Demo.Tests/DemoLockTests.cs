using AwesomeAssertions;

namespace Noof.Ledger.Demo.Tests;

public sealed class DemoLockTests
{
    readonly DemoPaths paths = new(Directory.CreateTempSubdirectory("noof-demo-lock-").FullName);

    [Fact]
    public void A_second_demo_command_is_refused_while_the_first_holds_the_lock()
    {
        using var first = DemoLock.Acquire(paths);

        var second = () => DemoLock.Acquire(paths);

        second.Should().Throw<InvalidOperationException>().WithMessage("*Another demo command*");
    }

    [Fact]
    public void The_lock_is_free_again_once_its_holder_is_done()
    {
        DemoLock.Acquire(paths).Dispose();

        using var again = DemoLock.Acquire(paths);
    }
}
