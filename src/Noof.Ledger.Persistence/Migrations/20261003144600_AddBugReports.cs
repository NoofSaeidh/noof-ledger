using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddBugReports : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "bug_reports",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                number = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                source = table.Column<int>(type: "integer", nullable: false),
                text = table.Column<string>(type: "text", nullable: true),
                transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                telegram_chat_id = table.Column<long>(type: "bigint", nullable: true),
                telegram_message_id = table.Column<int>(type: "integer", nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                closed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                snapshot_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                record_summary = table.Column<string>(type: "text", nullable: true),
                findings = table.Column<string>(type: "jsonb", nullable: true),
                log_lines = table.Column<string>(type: "jsonb", nullable: true),
                collection_failures = table.Column<string>(type: "text", nullable: true),
                explanation_state = table.Column<int>(type: "integer", nullable: false),
                explanation_attempts = table.Column<int>(type: "integer", nullable: false),
                explanation_next_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                explanation = table.Column<string>(type: "text", nullable: true),
                looks_like_bug = table.Column<bool>(type: "boolean", nullable: true),
                reply_message_id = table.Column<int>(type: "integer", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_bug_reports", x => x.id);
                table.CheckConstraint("ck_bug_reports_closed_at_matches_status", "(status = 1) = (closed_at IS NOT NULL)");
                table.CheckConstraint("ck_bug_reports_explanation_matches_state", "(explanation_state = 1) = (explanation IS NOT NULL AND looks_like_bug IS NOT NULL)");
                table.CheckConstraint("ck_bug_reports_telegram_ids_match_source", "(source = 0 AND telegram_chat_id IS NOT NULL AND telegram_message_id IS NOT NULL) OR (source = 1 AND telegram_chat_id IS NULL AND telegram_message_id IS NULL AND reply_message_id IS NULL)");
                table.ForeignKey(
                    name: "FK_bug_reports_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_bug_reports_number",
            schema: "public",
            table: "bug_reports",
            column: "number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_bug_reports_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "bug_reports",
            columns: new[] { "telegram_chat_id", "telegram_message_id" },
            unique: true,
            filter: "telegram_chat_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_bug_reports_transaction_id",
            schema: "public",
            table: "bug_reports",
            column: "transaction_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "bug_reports",
            schema: "public");
    }
}
