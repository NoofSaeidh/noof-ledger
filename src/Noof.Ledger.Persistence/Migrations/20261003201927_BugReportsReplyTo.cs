using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

// Spec R-1 and R-2. AddBugReports is applied to the shared test template, so this is a migration of its own. The old
// rules come off first: they name the old numbers, and the rows are renumbered before the new ones go on. A Telegram
// report's message ids become the reply address the Telegram layer writes, "<chat id>:<message id>".
/// <inheritdoc />
public partial class BugReportsReplyTo : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_bug_reports_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.DropCheckConstraint(
            name: "ck_bug_reports_closed_at_matches_status",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.DropCheckConstraint(
            name: "ck_bug_reports_explanation_matches_state",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.DropCheckConstraint(
            name: "ck_bug_reports_telegram_ids_match_source",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.AddColumn<string>(
            name: "delivered_as",
            schema: "public",
            table: "bug_reports",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "reply_to",
            schema: "public",
            table: "bug_reports",
            type: "text",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE public.bug_reports SET
                source = source + 1,
                status = status + 1,
                explanation_state = explanation_state + 1,
                reply_to = telegram_chat_id::text || ':' || telegram_message_id::text,
                delivered_as = reply_message_id::text;
            """);

        migrationBuilder.DropColumn(
            name: "reply_message_id",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.DropColumn(
            name: "telegram_chat_id",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.DropColumn(
            name: "telegram_message_id",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.CreateIndex(
            name: "IX_bug_reports_source_reply_to",
            schema: "public",
            table: "bug_reports",
            columns: new[] { "source", "reply_to" },
            unique: true,
            filter: "reply_to IS NOT NULL");

        migrationBuilder.AddCheckConstraint(
            name: "ck_bug_reports_closed_at_matches_status",
            schema: "public",
            table: "bug_reports",
            sql: "status <> 0 AND (status = 2) = (closed_at IS NOT NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "ck_bug_reports_explanation_matches_state",
            schema: "public",
            table: "bug_reports",
            sql: "explanation_state <> 0 AND (explanation_state = 2) = (explanation IS NOT NULL AND looks_like_bug IS NOT NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "ck_bug_reports_source_and_reply_to",
            schema: "public",
            table: "bug_reports",
            sql: "source <> 0 AND (delivered_as IS NULL OR reply_to IS NOT NULL)");
    }

    // A reply address only Telegram reads back; any other source's is lost.
    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_bug_reports_source_reply_to",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.DropCheckConstraint(
            name: "ck_bug_reports_closed_at_matches_status",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.DropCheckConstraint(
            name: "ck_bug_reports_explanation_matches_state",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.DropCheckConstraint(
            name: "ck_bug_reports_source_and_reply_to",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.AddColumn<int>(
            name: "reply_message_id",
            schema: "public",
            table: "bug_reports",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "telegram_chat_id",
            schema: "public",
            table: "bug_reports",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "telegram_message_id",
            schema: "public",
            table: "bug_reports",
            type: "integer",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE public.bug_reports SET
                source = source - 1,
                status = status - 1,
                explanation_state = explanation_state - 1,
                telegram_chat_id = CASE WHEN source = 1 THEN split_part(reply_to, ':', 1)::bigint END,
                telegram_message_id = CASE WHEN source = 1 THEN split_part(reply_to, ':', 2)::integer END,
                reply_message_id = CASE WHEN source = 1 THEN delivered_as::integer END;
            """);

        migrationBuilder.DropColumn(
            name: "delivered_as",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.DropColumn(
            name: "reply_to",
            schema: "public",
            table: "bug_reports");

        migrationBuilder.CreateIndex(
            name: "IX_bug_reports_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "bug_reports",
            columns: new[] { "telegram_chat_id", "telegram_message_id" },
            unique: true,
            filter: "telegram_chat_id IS NOT NULL");

        migrationBuilder.AddCheckConstraint(
            name: "ck_bug_reports_closed_at_matches_status",
            schema: "public",
            table: "bug_reports",
            sql: "(status = 1) = (closed_at IS NOT NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "ck_bug_reports_explanation_matches_state",
            schema: "public",
            table: "bug_reports",
            sql: "(explanation_state = 1) = (explanation IS NOT NULL AND looks_like_bug IS NOT NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "ck_bug_reports_telegram_ids_match_source",
            schema: "public",
            table: "bug_reports",
            sql: "(source = 0 AND telegram_chat_id IS NOT NULL AND telegram_message_id IS NOT NULL) OR (source = 1 AND telegram_chat_id IS NULL AND telegram_message_id IS NULL AND reply_message_id IS NULL)");
    }
}
