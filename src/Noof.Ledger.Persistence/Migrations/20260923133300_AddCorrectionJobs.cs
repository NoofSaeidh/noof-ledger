using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddCorrectionJobs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_categorization_jobs_transaction_id",
            schema: "public",
            table: "categorization_jobs");

        migrationBuilder.AddColumn<int>(
            name: "prompt_message_id",
            schema: "public",
            table: "transactions",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "instruction",
            schema: "public",
            table: "categorization_jobs",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "kind",
            schema: "public",
            table: "categorization_jobs",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "source_message_id",
            schema: "public",
            table: "categorization_jobs",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_categorization_jobs_transaction_id_source_message_id",
            schema: "public",
            table: "categorization_jobs",
            columns: new[] { "transaction_id", "source_message_id" },
            unique: true,
            filter: "source_message_id IS NOT NULL");

        migrationBuilder.AddCheckConstraint(
            name: "ck_categorization_jobs_correction_has_instruction",
            schema: "public",
            table: "categorization_jobs",
            sql: "kind <> 1 OR instruction IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_categorization_jobs_transaction_id_source_message_id",
            schema: "public",
            table: "categorization_jobs");

        migrationBuilder.DropCheckConstraint(
            name: "ck_categorization_jobs_correction_has_instruction",
            schema: "public",
            table: "categorization_jobs");

        migrationBuilder.DropColumn(
            name: "prompt_message_id",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "instruction",
            schema: "public",
            table: "categorization_jobs");

        migrationBuilder.DropColumn(
            name: "kind",
            schema: "public",
            table: "categorization_jobs");

        migrationBuilder.DropColumn(
            name: "source_message_id",
            schema: "public",
            table: "categorization_jobs");

        migrationBuilder.CreateIndex(
            name: "IX_categorization_jobs_transaction_id",
            schema: "public",
            table: "categorization_jobs",
            column: "transaction_id");
    }
}
