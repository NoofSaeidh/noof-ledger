using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// The script runs only against a real server, so its one comparison rule is pinned here as text.
public class RestoreCheckScriptTests
{
    [Fact]
    public void Leaves_backup_runs_and_fx_rates_out_of_the_row_count_comparison()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot.Find().FullName, "ops", "restore-check.ps1"));

        script.Should().Contain("$ledgerTables = $tables | Where-Object { $_ -notin 'backup_runs', 'fx_rates' }",
            "a worker can write either table after a dump is taken, so their counts differ by construction (ops/RUNBOOK.md)");
    }
}
