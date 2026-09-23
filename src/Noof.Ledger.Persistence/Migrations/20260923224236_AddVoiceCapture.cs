using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddVoiceCapture : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_categorization_jobs_transaction_id_source_message_id",
            schema: "public",
            table: "categorization_jobs");

        migrationBuilder.AlterColumn<string>(
            name: "raw_text",
            schema: "public",
            table: "transactions",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "text");

        migrationBuilder.AddColumn<int>(
            name: "capture_kind",
            schema: "public",
            table: "transactions",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "voice_duration_seconds",
            schema: "public",
            table: "transactions",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "voice_file_id",
            schema: "public",
            table: "transactions",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "voice_file_id",
            schema: "public",
            table: "categorization_jobs",
            type: "text",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind = 0 AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)");

        migrationBuilder.CreateIndex(
            name: "IX_categorization_jobs_transaction_id_source_message_id_kind",
            schema: "public",
            table: "categorization_jobs",
            columns: new[] { "transaction_id", "source_message_id", "kind" },
            unique: true,
            filter: "source_message_id IS NOT NULL");

        migrationBuilder.AddCheckConstraint(
            name: "ck_categorization_jobs_transcription_has_voice_file",
            schema: "public",
            table: "categorization_jobs",
            sql: "kind <> 3 OR voice_file_id IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropIndex(
            name: "IX_categorization_jobs_transaction_id_source_message_id_kind",
            schema: "public",
            table: "categorization_jobs");

        migrationBuilder.DropCheckConstraint(
            name: "ck_categorization_jobs_transcription_has_voice_file",
            schema: "public",
            table: "categorization_jobs");

        migrationBuilder.DropColumn(
            name: "capture_kind",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "voice_duration_seconds",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "voice_file_id",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "voice_file_id",
            schema: "public",
            table: "categorization_jobs");

        migrationBuilder.AlterColumn<string>(
            name: "raw_text",
            schema: "public",
            table: "transactions",
            type: "text",
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_categorization_jobs_transaction_id_source_message_id",
            schema: "public",
            table: "categorization_jobs",
            columns: new[] { "transaction_id", "source_message_id" },
            unique: true,
            filter: "source_message_id IS NOT NULL");
    }
}
