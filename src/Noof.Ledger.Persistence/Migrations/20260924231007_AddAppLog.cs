using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddAppLog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "app_log",
            schema: "public",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                logged_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                level = table.Column<short>(type: "smallint", nullable: false),
                source = table.Column<string>(type: "text", nullable: true),
                message = table.Column<string>(type: "text", nullable: false),
                template = table.Column<string>(type: "text", nullable: false),
                exception = table.Column<string>(type: "text", nullable: true),
                transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                properties = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_app_log", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_app_log_level_logged_at",
            schema: "public",
            table: "app_log",
            columns: new[] { "level", "logged_at" });

        migrationBuilder.CreateIndex(
            name: "IX_app_log_logged_at",
            schema: "public",
            table: "app_log",
            column: "logged_at");

        migrationBuilder.CreateIndex(
            name: "IX_app_log_transaction_id",
            schema: "public",
            table: "app_log",
            column: "transaction_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "app_log",
            schema: "public");
    }
}
