using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddReceipts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions");

        migrationBuilder.AddColumn<int>(
            name: "default_for_payment",
            schema: "public",
            table: "wallets",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "telegram_file_id",
            schema: "public",
            table: "transactions",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "verification_url",
            schema: "public",
            table: "transactions",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "tax_id",
            schema: "public",
            table: "merchants",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        // Nullable for now: existing rows get their ordinal from the backfill below, and only then
        // does the column become NOT NULL - a single AddColumn with a defaultValue would instead
        // give every existing row the same ordinal (0), which is not an order at all.
        migrationBuilder.AddColumn<int>(
            name: "ordinal",
            schema: "public",
            table: "line_items",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "receipt_line_id",
            schema: "public",
            table: "line_items",
            type: "uuid",
            nullable: true);

        // Backfill: every pre-existing line item gets the order it would have shown in before this
        // migration (by id, the only order the heap ever guaranteed) within its own transaction.
        migrationBuilder.Sql(
            """
            UPDATE public.line_items li
            SET ordinal = ranked.rn
            FROM (
                SELECT id, ROW_NUMBER() OVER (PARTITION BY transaction_id ORDER BY id) AS rn
                FROM public.line_items
            ) ranked
            WHERE li.id = ranked.id;
            """);

        migrationBuilder.AlterColumn<int>(
            name: "ordinal",
            schema: "public",
            table: "line_items",
            type: "integer",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.CreateTable(
            name: "receipts",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                source = table.Column<int>(type: "integer", nullable: false),
                verification_url = table.Column<string>(type: "text", nullable: true),
                seller_tax_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                seller_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                seller_address = table.Column<string>(type: "text", nullable: true),
                location_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                fiscal_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                issued_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                receipt_kind = table.Column<int>(type: "integer", nullable: false),
                payment_method = table.Column<int>(type: "integer", nullable: true),
                qr_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                telegram_file_id = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipts", x => x.id);
                table.ForeignKey(
                    name: "FK_receipts_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "receipt_lines",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                ordinal = table.Column<int>(type: "integer", nullable: false),
                name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                tax_label = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_lines", x => x.id);
                table.ForeignKey(
                    name: "FK_receipt_lines_receipts_receipt_id",
                    column: x => x.receipt_id,
                    principalSchema: "public",
                    principalTable: "receipts",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_wallets_one_default_per_payment_method",
            schema: "public",
            table: "wallets",
            column: "default_for_payment",
            unique: true,
            filter: "default_for_payment IS NOT NULL");

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL) OR (capture_kind = 3 AND (telegram_file_id IS NOT NULL OR verification_url IS NOT NULL))");

        migrationBuilder.CreateIndex(
            name: "ix_merchants_tax_id",
            schema: "public",
            table: "merchants",
            column: "tax_id",
            unique: true,
            filter: "tax_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_line_items_receipt_line_id",
            schema: "public",
            table: "line_items",
            column: "receipt_line_id");

        migrationBuilder.CreateIndex(
            name: "IX_receipt_lines_receipt_id_ordinal",
            schema: "public",
            table: "receipt_lines",
            columns: new[] { "receipt_id", "ordinal" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_receipts_seller_tax_id_fiscal_number",
            schema: "public",
            table: "receipts",
            columns: new[] { "seller_tax_id", "fiscal_number" },
            unique: true,
            filter: "seller_tax_id IS NOT NULL AND fiscal_number IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_receipts_transaction_id",
            schema: "public",
            table: "receipts",
            column: "transaction_id",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_line_items_receipt_lines_receipt_line_id",
            schema: "public",
            table: "line_items",
            column: "receipt_line_id",
            principalSchema: "public",
            principalTable: "receipt_lines",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_line_items_receipt_lines_receipt_line_id",
            schema: "public",
            table: "line_items");

        migrationBuilder.DropTable(
            name: "receipt_lines",
            schema: "public");

        migrationBuilder.DropTable(
            name: "receipts",
            schema: "public");

        migrationBuilder.DropIndex(
            name: "ix_wallets_one_default_per_payment_method",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropIndex(
            name: "ix_merchants_tax_id",
            schema: "public",
            table: "merchants");

        migrationBuilder.DropIndex(
            name: "IX_line_items_receipt_line_id",
            schema: "public",
            table: "line_items");

        migrationBuilder.DropColumn(
            name: "default_for_payment",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropColumn(
            name: "telegram_file_id",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "verification_url",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "tax_id",
            schema: "public",
            table: "merchants");

        migrationBuilder.DropColumn(
            name: "ordinal",
            schema: "public",
            table: "line_items");

        migrationBuilder.DropColumn(
            name: "receipt_line_id",
            schema: "public",
            table: "line_items");

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)");
    }
}
