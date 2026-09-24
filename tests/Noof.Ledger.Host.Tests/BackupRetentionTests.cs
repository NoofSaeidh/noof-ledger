using AwesomeAssertions;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class BackupRetentionTests
{
    [Fact]
    public void Keeps_nothing_to_delete_when_at_or_under_the_limit()
    {
        string[] files = ["noof_ledger-20260101-000000.dump", "noof_ledger-20260102-000000.dump"];

        BackupRetention.ToDelete(files, keep: 14).Should().BeEmpty();
    }

    [Fact]
    public void Deletes_every_file_past_the_newest_fourteen_by_name_order()
    {
        // File names embed a sortable UTC timestamp (yyyyMMdd-HHmmss), so the newest 14 are the
        // last 14 in ordinal order - no parsing needed, and no ambiguity from a file's mtime,
        // which a copy or a restore of the backups folder would not preserve.
        var files = Enumerable.Range(1, 16)
            .Select(day => $"noof_ledger-202601{day:D2}-000000.dump")
            .ToArray();

        var toDelete = BackupRetention.ToDelete(files, keep: 14);

        toDelete.Should().BeEquivalentTo(
        [
            "noof_ledger-20260101-000000.dump",
            "noof_ledger-20260102-000000.dump",
        ]);
    }

    [Fact]
    public void An_unordered_input_is_sorted_before_pruning()
    {
        string[] files =
        [
            "noof_ledger-20260103-000000.dump",
            "noof_ledger-20260101-000000.dump",
            "noof_ledger-20260102-000000.dump",
        ];

        BackupRetention.ToDelete(files, keep: 2).Should().BeEquivalentTo(["noof_ledger-20260101-000000.dump"]);
    }

    [Fact]
    public void A_hand_named_file_is_never_picked_as_newest_nor_pruned()
    {
        // M-3 (Phase 4 final review): "newest by name" trusted the glob. A hand-named file like
        // Task 10's "noof_ledger-manual-check.dump" sorts as newest forever ('m' > '2'), so
        // restore-check would check it instead of a real dump and retention would keep it and
        // prune a real one once there are 15. It must be excluded from consideration entirely.
        string[] files =
        [
            "noof_ledger-manual.dump",
            "noof_ledger-20260101-000000.dump",
            "noof_ledger-20260102-000000.dump",
        ];

        BackupRetention.ToDelete(files, keep: 1).Should().BeEquivalentTo(["noof_ledger-20260101-000000.dump"]);
    }
}
