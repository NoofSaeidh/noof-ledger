using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Noof.Ledger.Persistence.Tests;

public class SchemaSnapshotTests
{
    [Fact]
    public void The_generated_schema_matches_the_committed_snapshot()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none")
            .Options;

        using var db = new LedgerDbContext(options);

        var actual = Normalise(db.Database.GenerateCreateScript());
        var expected = Normalise(File.ReadAllText(SnapshotPath));

        actual.Should().Be(expected,
            "the schema changed; review the diff and update schema.expected.sql deliberately");
    }

    static string SnapshotPath =>
        Path.Combine(AppContext.BaseDirectory, "schema.expected.sql");

    static string Normalise(string sql) =>
        string.Join('\n', sql.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())).Trim();
}
