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

    public static TheoryData<string> CommandNameTheoryData()
    {
        var data = new TheoryData<string>();
        foreach (var name in CommandNames)
            data.Add(name);
        return data;
    }
}
