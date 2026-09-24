using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddMoneyModel : Migration
{
    // Operator-owned starting data, seeded the way AddCaptureModel seeds its tree and for the same reasons (see the
    // comment there): raw SQL, fixed ids, ON CONFLICT (id) DO NOTHING. Income is marked by its parent's slug, which
    // is how every category is offered to the model, so nothing needs a column of its own.
    internal const string SeedIncomeCategoriesSql = """
        INSERT INTO public.categories (id, is_active, name_en, name_ru, parent_id, slug) VALUES
            ('00000000-0000-0000-0001-000000000021', TRUE, 'Income', 'Доходы', NULL, 'income'),
            ('00000000-0000-0000-0001-000000000022', TRUE, 'Salary', 'Зарплата', '00000000-0000-0000-0001-000000000021', 'salary'),
            ('00000000-0000-0000-0001-000000000023', TRUE, 'Refund', 'Возврат', '00000000-0000-0000-0001-000000000021', 'refund'),
            ('00000000-0000-0000-0001-000000000024', TRUE, 'Gift', 'Подарок', '00000000-0000-0000-0001-000000000021', 'gift'),
            ('00000000-0000-0000-0001-000000000025', TRUE, 'Other income', 'Прочие доходы', '00000000-0000-0000-0001-000000000021', 'other-income')
        ON CONFLICT (id) DO NOTHING;
        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX public.ix_wallets_single_default;");

        migrationBuilder.DropIndex(
            name: "IX_transactions_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions");

        // A rename, not the drop-and-add EF scaffolds: the seeded Main Wallet stays the default, now for RSD.
        migrationBuilder.RenameColumn(
            name: "is_default",
            schema: "public",
            table: "wallets",
            newName: "is_default_for_currency");

        migrationBuilder.AddColumn<string[]>(
            name: "aliases",
            schema: "public",
            table: "wallets",
            type: "text[]",
            nullable: false,
            defaultValueSql: "'{}'");

        migrationBuilder.AddColumn<bool>(
            name: "archived",
            schema: "public",
            table: "wallets",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "created_at",
            schema: "public",
            table: "wallets",
            type: "timestamptz",
            nullable: false,
            defaultValueSql: "now()");

        migrationBuilder.AlterColumn<Guid>(
            name: "wallet_id",
            schema: "public",
            table: "transactions",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.AlterColumn<long>(
            name: "telegram_chat_id",
            schema: "public",
            table: "transactions",
            type: "bigint",
            nullable: true,
            oldClrType: typeof(long),
            oldType: "bigint");

        migrationBuilder.AlterColumn<int>(
            name: "telegram_message_id",
            schema: "public",
            table: "transactions",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.AddColumn<int>(
            name: "kind",
            schema: "public",
            table: "transactions",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "backup_runs",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                finished_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                succeeded = table.Column<bool>(type: "boolean", nullable: false),
                file_name = table.Column<string>(type: "text", nullable: true),
                size_bytes = table.Column<long>(type: "bigint", nullable: true),
                error = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_backup_runs", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "balance_checks",
            schema: "public",
            columns: table => new
            {
                transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                computed_before = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                stated_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_balance_checks", x => x.transaction_id);
                table.ForeignKey(
                    name: "FK_balance_checks_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_balance_checks_wallets_wallet_id",
                    column: x => x.wallet_id,
                    principalSchema: "public",
                    principalTable: "wallets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "entries",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<int>(type: "integer", nullable: false),
                amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_entries", x => x.id);
                table.ForeignKey(
                    name: "FK_entries_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_entries_wallets_wallet_id",
                    column: x => x.wallet_id,
                    principalSchema: "public",
                    principalTable: "wallets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_wallets_one_default_per_currency",
            schema: "public",
            table: "wallets",
            column: "currency",
            unique: true,
            filter: "is_default_for_currency");

        migrationBuilder.CreateIndex(
            name: "IX_transactions_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "transactions",
            columns: new[] { "telegram_chat_id", "telegram_message_id" },
            unique: true,
            filter: "telegram_chat_id IS NOT NULL");

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_telegram_ids_match_capture_kind",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind = 2 AND telegram_chat_id IS NULL AND telegram_message_id IS NULL) OR (capture_kind <> 2 AND telegram_chat_id IS NOT NULL AND telegram_message_id IS NOT NULL)");

        migrationBuilder.CreateIndex(
            name: "IX_balance_checks_wallet_id",
            schema: "public",
            table: "balance_checks",
            column: "wallet_id");

        migrationBuilder.CreateIndex(
            name: "IX_entries_transaction_id",
            schema: "public",
            table: "entries",
            column: "transaction_id");

        migrationBuilder.CreateIndex(
            name: "ix_entries_wallet_id",
            schema: "public",
            table: "entries",
            column: "wallet_id");

        migrationBuilder.Sql(SeedIncomeCategoriesSql);

        // Every record so far is an expense in the wallet it was captured into; its entries are minus its lines,
        // per currency (M5). Status is not a filter here: a cancelled record keeps its lines and gets its entries,
        // so Restore brings its money back, and the balance leaves out whatever is not Completed.
        migrationBuilder.Sql(
            """
            INSERT INTO public.entries (id, transaction_id, wallet_id, amount, currency, role)
            SELECT gen_random_uuid(), t.id, t.wallet_id, -SUM(li.amount), li.currency, 0
            FROM public.transactions t
            JOIN public.line_items li ON li.transaction_id = t.id
            WHERE t.kind = 0
            GROUP BY t.id, t.wallet_id, li.currency;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Down cannot hand back a database holding what the old schema has no room for: a capture with no wallet
        // yet, a Manual record, a second currency's default, or a line in an income category each make one of the
        // statements below fail, and it stops there.
        migrationBuilder.Sql(
            """
            DELETE FROM public.categories WHERE id IN (
                '00000000-0000-0000-0001-000000000022', '00000000-0000-0000-0001-000000000023',
                '00000000-0000-0000-0001-000000000024', '00000000-0000-0000-0001-000000000025');
            DELETE FROM public.categories WHERE id = '00000000-0000-0000-0001-000000000021';
            """);

        migrationBuilder.DropTable(
            name: "backup_runs",
            schema: "public");

        migrationBuilder.DropTable(
            name: "balance_checks",
            schema: "public");

        migrationBuilder.DropTable(
            name: "entries",
            schema: "public");

        migrationBuilder.DropIndex(
            name: "ix_wallets_one_default_per_currency",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropIndex(
            name: "IX_transactions_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_telegram_ids_match_capture_kind",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "aliases",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropColumn(
            name: "archived",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropColumn(
            name: "created_at",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropColumn(
            name: "kind",
            schema: "public",
            table: "transactions");

        migrationBuilder.RenameColumn(
            name: "is_default_for_currency",
            schema: "public",
            table: "wallets",
            newName: "is_default");

        migrationBuilder.AlterColumn<Guid>(
            name: "wallet_id",
            schema: "public",
            table: "transactions",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.AlterColumn<long>(
            name: "telegram_chat_id",
            schema: "public",
            table: "transactions",
            type: "bigint",
            nullable: false,
            oldClrType: typeof(long),
            oldType: "bigint",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "telegram_message_id",
            schema: "public",
            table: "transactions",
            type: "integer",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_transactions_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "transactions",
            columns: new[] { "telegram_chat_id", "telegram_message_id" },
            unique: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind = 0 AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)");

        migrationBuilder.Sql("CREATE UNIQUE INDEX ix_wallets_single_default ON wallets ((true)) WHERE is_default;");
    }
}
