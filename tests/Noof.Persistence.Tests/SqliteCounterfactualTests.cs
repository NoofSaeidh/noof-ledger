using System.Globalization;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;

namespace Noof.Persistence.Tests;

public class SqliteCounterfactualTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public void Record_what_sqlite_does_with_decimal_as_text(string culture)
    {
        CultureInfo.CurrentCulture = new CultureInfo(culture);

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE probe (amount TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        foreach (var amount in new[] { "-45.25", "0.10", "2.05", "9.55", "10.00", "100.00", "999.99", "1234.50" })
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = $"INSERT INTO probe VALUES ('{amount}')";
            insert.ExecuteNonQuery();
        }

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT MAX(amount) FROM probe";
        var max = query.ExecuteScalar()!.ToString();

        max.Should().NotBe("1234.50",
            "this test documents the defect that made PostgreSQL the choice; if it ever passes, re-open the decision");
    }
}
