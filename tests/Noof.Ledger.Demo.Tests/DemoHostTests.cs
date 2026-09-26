using AwesomeAssertions;

namespace Noof.Ledger.Demo.Tests;

public sealed class DemoHostTests
{
    const string ConnectionString = "Host=127.0.0.1;Port=5432;Database=noof_ledger_demo;Username=postgres;Password=s3cret-value";
    static readonly DemoPaths Paths = new(@"C:\demo-root");

    [Fact]
    public void Connection_string_travels_only_in_the_environment()
    {
        var start = DemoHost.StartInfo(ConnectionString, Paths, redirect: true);

        start.Environment["ConnectionStrings__Ledger"].Should().Be(ConnectionString);
        start.ArgumentList.Should().NotContain(argument => argument.Contains("s3cret-value", StringComparison.Ordinal));
        start.Arguments.Should().NotContain("s3cret-value");
    }

    [Fact]
    public void The_host_listens_on_loopback_5264_and_writes_only_under_the_demo_root()
    {
        var start = DemoHost.StartInfo(ConnectionString, Paths, redirect: true);

        start.Environment["Urls"].Should().Be("http://127.0.0.1:5264");
        start.Environment["Logging__File__Directory"].Should().Be(@"C:\demo-root\logs");
        start.Environment["DataProtection__KeyRingDirectory"].Should().Be(@"C:\demo-root\dp-keys");
        start.Environment["Backup__Enabled"].Should().Be("false");
    }

    [Fact]
    public void The_host_runs_like_run_ps1_start()
    {
        var start = DemoHost.StartInfo(ConnectionString, Paths, redirect: true);

        start.FileName.Should().Be("dotnet");
        start.ArgumentList.Should().ContainInOrder("run", "--project");
        start.ArgumentList.Should().Contain(["-c", "Release", "--no-launch-profile"]);
        start.UseShellExecute.Should().BeFalse();
    }
}
