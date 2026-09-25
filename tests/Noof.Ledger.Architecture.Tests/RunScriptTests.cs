using System.Diagnostics;
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Task 11: run.ps1 in the repo root is the one way to launch and operate the app - this is its
// only automated coverage, since the rest of what it does (starting a real host, touching a real
// database) is out of bounds for this fast, no-database project.
public class RunScriptTests
{
    static readonly string[] CommandNames =
    [
        "start", "publish", "start-published", "set-password", "test", "update-test-template",
        "clean-test-dbs", "restore-check", "db-auth-reset", "pg", "status", "logs", "backups", "inspect",
    ];

    static (int ExitCode, string Output) RunPwsh(params string[] arguments)
    {
        var repoRoot = RepoRoot.Find().FullName;
        var start = new ProcessStartInfo("pwsh")
        {
            ArgumentList = { "-NoProfile", "-File", Path.Combine(repoRoot, "run.ps1") },
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    [Fact]
    public void Help_lists_every_command()
    {
        var (exitCode, output) = RunPwsh("help");

        exitCode.Should().Be(0);
        foreach (var name in CommandNames)
            output.Should().Contain(name, $"the command table must list '{name}'");
    }

    [Theory]
    [MemberData(nameof(CommandNameTheoryData))]
    public void Help_for_each_command_prints_non_empty_detail(string commandName)
    {
        var (exitCode, output) = RunPwsh("help", commandName);

        exitCode.Should().Be(0);
        output.Trim().Should().NotBeEmpty($"'run.ps1 help {commandName}' must print something");
        output.Should().Contain(commandName);
    }

    [Fact]
    public void An_unknown_command_prints_the_table_and_exits_1()
    {
        var (exitCode, output) = RunPwsh("bogus");

        exitCode.Should().Be(1);
        output.Should().Contain("Unknown command");
        foreach (var name in CommandNames)
            output.Should().Contain(name);
    }

    // M-7 (Phase 5 final review): Invoke-Checked used to swallow the child's own exit code and
    // always exit run.ps1 with 1. A filter matching zero tests makes the MTP test host exit 8 (not
    // 1) - safe to run for real since Domain.Tests needs no database and this fails on the very
    // first fast project, never reaching the others.
    [Fact]
    public void A_failing_child_command_propagates_its_own_exit_code()
    {
        var (exitCode, _) = RunPwsh("test", "fast", "-Filter", "ThisClassDoesNotExist12345");

        exitCode.Should().Be(8, "dotnet test's own exit code for 'zero tests ran' must propagate, not a generic 1");
    }

    // M-6 (Phase 5 final review): Get-ArgValue returns $null when -Filter is the last token, and
    // every -Filter caller used to treat that the same as "no -Filter given" and run the whole
    // project unfiltered - the one thing the operator's testing rule says db/e2e must not do outside
    // the phase-end pass. Safe to run for real: the fix throws before Enter-SuiteLock, so this never
    // touches PostgreSQL even for `test db`.
    [Theory]
    [InlineData("fast")]
    [InlineData("db")]
    [InlineData("e2e")]
    [InlineData("all")]
    public void Filter_with_no_value_errors_instead_of_running_the_suite_unfiltered(string suite)
    {
        var (exitCode, output) = RunPwsh("test", suite, "-Filter");

        exitCode.Should().NotBe(0, "a -Filter with no value must fail loudly, not run the suite unfiltered");
        output.Should().Contain("-Filter");
    }

    public static TheoryData<string> CommandNameTheoryData()
    {
        var data = new TheoryData<string>();
        foreach (var name in CommandNames)
            data.Add(name);
        return data;
    }
}
