using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddReceiptCaptureSourceXor : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions");

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL) OR (capture_kind = 3 AND ((telegram_file_id IS NOT NULL) <> (verification_url IS NOT NULL)))");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions");

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL) OR (capture_kind = 3 AND (telegram_file_id IS NOT NULL OR verification_url IS NOT NULL))");
    }
}
