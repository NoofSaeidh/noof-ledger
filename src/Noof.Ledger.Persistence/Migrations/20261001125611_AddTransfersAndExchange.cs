using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddTransfersAndExchange : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_wallets_one_default_per_payment_method",
            schema: "public",
            table: "wallets");

        migrationBuilder.AddColumn<int>(
            name: "failure_reason",
            schema: "public",
            table: "transactions",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "slip_number",
            schema: "public",
            table: "receipts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "role",
            schema: "public",
            table: "line_items",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "charges",
            schema: "public",
            columns: table => new
            {
                transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                charged_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                fee_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                rate_used = table.Column<decimal>(type: "numeric(24,12)", precision: 24, scale: 12, nullable: false),
                fee_percent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                fee_fixed = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                fee_minimum = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                source = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_charges", x => new { x.transaction_id, x.currency });
                table.CheckConstraint("ck_charges_amounts", "charged_amount > 0 AND fee_amount >= 0 AND rate_used > 0");
                table.ForeignKey(
                    name: "FK_charges_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "receipt_exchanges",
            schema: "public",
            columns: table => new
            {
                receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                given_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                given_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                received_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                received_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                rate = table.Column<decimal>(type: "numeric(24,12)", precision: 24, scale: 12, nullable: true),
                commission_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                commission_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                slip_number = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_exchanges", x => x.receipt_id);
                table.ForeignKey(
                    name: "FK_receipt_exchanges_receipts_receipt_id",
                    column: x => x.receipt_id,
                    principalSchema: "public",
                    principalTable: "receipts",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "transfers",
            schema: "public",
            columns: table => new
            {
                transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                from_wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                to_wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                fee_leg = table.Column<int>(type: "integer", nullable: true),
                stated_rate = table.Column<decimal>(type: "numeric(24,12)", precision: 24, scale: 12, nullable: true),
                stated_rate_base = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                venue_merchant_id = table.Column<Guid>(type: "uuid", nullable: true),
                from_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                from_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                to_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                to_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_transfers", x => x.transaction_id);
                table.CheckConstraint("ck_transfers_amounts_positive", "from_amount > 0 AND to_amount > 0");
                table.CheckConstraint("ck_transfers_stated_rate_has_base", "(stated_rate IS NULL) = (stated_rate_base IS NULL)");
                table.CheckConstraint("ck_transfers_wallets_differ", "from_wallet_id <> to_wallet_id");
                table.ForeignKey(
                    name: "FK_transfers_merchants_venue_merchant_id",
                    column: x => x.venue_merchant_id,
                    principalSchema: "public",
                    principalTable: "merchants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_transfers_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_transfers_wallets_from_wallet_id",
                    column: x => x.from_wallet_id,
                    principalSchema: "public",
                    principalTable: "wallets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_transfers_wallets_to_wallet_id",
                    column: x => x.to_wallet_id,
                    principalSchema: "public",
                    principalTable: "wallets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "wallet_fx_terms",
            schema: "public",
            columns: table => new
            {
                wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                rate = table.Column<decimal>(type: "numeric(24,12)", precision: 24, scale: 12, nullable: false),
                fee_percent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                fee_fixed = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                fee_minimum = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wallet_fx_terms", x => new { x.wallet_id, x.currency });
                table.CheckConstraint("ck_wallet_fx_terms_rate_positive", "rate > 0");
                table.ForeignKey(
                    name: "FK_wallet_fx_terms_wallets_wallet_id",
                    column: x => x.wallet_id,
                    principalSchema: "public",
                    principalTable: "wallets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_wallets_one_default_per_payment_method_and_currency",
            schema: "public",
            table: "wallets",
            columns: new[] { "default_for_payment", "currency" },
            unique: true,
            filter: "default_for_payment IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_receipts_seller_tax_id_slip_number",
            schema: "public",
            table: "receipts",
            columns: new[] { "seller_tax_id", "slip_number" },
            unique: true,
            filter: "receipt_kind = 6 AND seller_tax_id IS NOT NULL AND slip_number IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_transfers_from_wallet_id",
            schema: "public",
            table: "transfers",
            column: "from_wallet_id");

        migrationBuilder.CreateIndex(
            name: "ix_transfers_to_wallet_id",
            schema: "public",
            table: "transfers",
            column: "to_wallet_id");

        migrationBuilder.CreateIndex(
            name: "IX_transfers_venue_merchant_id",
            schema: "public",
            table: "transfers",
            column: "venue_merchant_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "charges",
            schema: "public");

        migrationBuilder.DropTable(
            name: "receipt_exchanges",
            schema: "public");

        migrationBuilder.DropTable(
            name: "transfers",
            schema: "public");

        migrationBuilder.DropTable(
            name: "wallet_fx_terms",
            schema: "public");

        migrationBuilder.DropIndex(
            name: "ix_wallets_one_default_per_payment_method_and_currency",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropIndex(
            name: "ix_receipts_seller_tax_id_slip_number",
            schema: "public",
            table: "receipts");

        migrationBuilder.DropColumn(
            name: "failure_reason",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "slip_number",
            schema: "public",
            table: "receipts");

        migrationBuilder.DropColumn(
            name: "role",
            schema: "public",
            table: "line_items");

        migrationBuilder.CreateIndex(
            name: "ix_wallets_one_default_per_payment_method",
            schema: "public",
            table: "wallets",
            column: "default_for_payment",
            unique: true,
            filter: "default_for_payment IS NOT NULL");
    }
}
