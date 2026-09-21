using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddCaptureModel : Migration
{
    // These rows are starting data the operator owns (renames, re-parents — see P1-1), not reference
    // data EF owns. HasData/InsertData would make them part of the model snapshot, so a later
    // migration that merely touches an unrelated seed value would regenerate UpdateData/DeleteData
    // for ALL of them — silently overwriting an operator rename, or failing to boot against the
    // Restrict FK from line_items.category_id / categories.parent_id. Raw SQL keeps the model
    // ignorant of these rows entirely; ON CONFLICT (id) DO NOTHING makes re-running this migration's
    // Up() safe against a database that already has them (id is the row's real identity here — a
    // fixed, hand-assigned GUID — and its uniqueness exists from CREATE TABLE, unlike the slug unique
    // index which this same migration creates later).
    internal const string SeedTopLevelCategoriesSql = """
        INSERT INTO public.categories (id, is_active, name_en, name_ru, parent_id, slug) VALUES
            ('00000000-0000-0000-0001-000000000001', TRUE, 'Groceries', 'Продукты', NULL, 'groceries'),
            ('00000000-0000-0000-0001-000000000002', TRUE, 'Food & Drink', 'Еда и напитки', NULL, 'food-drink'),
            ('00000000-0000-0000-0001-000000000003', TRUE, 'Transport', 'Транспорт', NULL, 'transport'),
            ('00000000-0000-0000-0001-000000000004', TRUE, 'Housing', 'Жильё', NULL, 'housing'),
            ('00000000-0000-0000-0001-000000000005', TRUE, 'Utilities', 'Коммунальные услуги', NULL, 'utilities'),
            ('00000000-0000-0000-0001-000000000006', TRUE, 'Health', 'Здоровье', NULL, 'health'),
            ('00000000-0000-0000-0001-000000000007', TRUE, 'Shopping', 'Покупки', NULL, 'shopping'),
            ('00000000-0000-0000-0001-000000000008', TRUE, 'Entertainment', 'Развлечения', NULL, 'entertainment'),
            ('00000000-0000-0000-0001-000000000009', TRUE, 'Travel', 'Путешествия', NULL, 'travel'),
            ('00000000-0000-0000-0001-000000000010', TRUE, 'Education', 'Образование', NULL, 'education'),
            ('00000000-0000-0000-0001-000000000011', TRUE, 'Subscriptions', 'Подписки', NULL, 'subscriptions'),
            ('00000000-0000-0000-0001-000000000012', TRUE, 'Gifts & Donations', 'Подарки и пожертвования', NULL, 'gifts-donations'),
            ('00000000-0000-0000-0001-000000000013', TRUE, 'Fees & Charges', 'Комиссии и сборы', NULL, 'fees-charges'),
            ('00000000-0000-0000-0001-000000000014', TRUE, 'Personal Care', 'Личная гигиена', NULL, 'personal-care'),
            ('00000000-0000-0000-0001-000000000015', TRUE, 'Other', 'Прочее', NULL, 'other')
        ON CONFLICT (id) DO NOTHING;
        """;

    internal const string SeedDefaultWalletSql = """
        INSERT INTO public.wallets (id, currency, is_default, name)
        VALUES ('00000000-0000-0000-0000-000000000001', 'RSD', TRUE, 'Main Wallet')
        ON CONFLICT (id) DO NOTHING;
        """;

    internal const string SeedSubCategoriesSql = """
        INSERT INTO public.categories (id, is_active, name_en, name_ru, parent_id, slug) VALUES
            ('00000000-0000-0000-0001-000000000016', TRUE, 'Restaurants', 'Рестораны', '00000000-0000-0000-0001-000000000002', 'restaurants'),
            ('00000000-0000-0000-0001-000000000017', TRUE, 'Coffee', 'Кофе', '00000000-0000-0000-0001-000000000002', 'coffee'),
            ('00000000-0000-0000-0001-000000000018', TRUE, 'Fuel', 'Топливо', '00000000-0000-0000-0001-000000000003', 'fuel'),
            ('00000000-0000-0000-0001-000000000019', TRUE, 'Public Transport', 'Общественный транспорт', '00000000-0000-0000-0001-000000000003', 'public-transport'),
            ('00000000-0000-0000-0001-000000000020', TRUE, 'Clothing', 'Одежда', '00000000-0000-0000-0001-000000000007', 'clothing')
        ON CONFLICT (id) DO NOTHING;
        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "money_probe_entities",
            schema: "public");

        migrationBuilder.CreateTable(
            name: "categories",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                name_en = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                name_ru = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_categories", x => x.id);
                table.ForeignKey(
                    name: "FK_categories_categories_parent_id",
                    column: x => x.parent_id,
                    principalSchema: "public",
                    principalTable: "categories",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "merchants",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                kind = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_merchants", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "wallets",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                is_default = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wallets", x => x.id);
            });

        migrationBuilder.Sql("CREATE UNIQUE INDEX ix_wallets_single_default ON wallets ((true)) WHERE is_default;");

        migrationBuilder.CreateTable(
            name: "merchant_aliases",
            schema: "public",
            columns: table => new
            {
                folded = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_merchant_aliases", x => x.folded);
                table.ForeignKey(
                    name: "FK_merchant_aliases_merchants_merchant_id",
                    column: x => x.merchant_id,
                    principalSchema: "public",
                    principalTable: "merchants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.Sql(
            """
            CREATE FUNCTION public.merchant_aliases_write_once() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'merchant_aliases is write-once: % on % is not permitted', TG_OP, TG_TABLE_NAME;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER merchant_aliases_write_once_guard
                BEFORE UPDATE OR DELETE ON public.merchant_aliases
                FOR EACH ROW EXECUTE FUNCTION public.merchant_aliases_write_once();

            CREATE TRIGGER merchant_aliases_no_truncate
                BEFORE TRUNCATE ON public.merchant_aliases
                FOR EACH STATEMENT EXECUTE FUNCTION public.merchant_aliases_write_once();
            """);

        migrationBuilder.CreateTable(
            name: "transactions",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                raw_text = table.Column<string>(type: "text", nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                telegram_chat_id = table.Column<long>(type: "bigint", nullable: false),
                telegram_message_id = table.Column<int>(type: "integer", nullable: false),
                bot_message_id = table.Column<int>(type: "integer", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_transactions", x => x.id);
                table.ForeignKey(
                    name: "FK_transactions_wallets_wallet_id",
                    column: x => x.wallet_id,
                    principalSchema: "public",
                    principalTable: "wallets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "categorization_jobs",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                attempt_count = table.Column<int>(type: "integer", nullable: false),
                run_after = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                claimed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                claimed_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                last_error = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_categorization_jobs", x => x.id);
                table.ForeignKey(
                    name: "FK_categorization_jobs_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "line_items",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: true),
                categorized_by = table.Column<int>(type: "integer", nullable: false),
                merchant_id = table.Column<Guid>(type: "uuid", nullable: true),
                amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_line_items", x => x.id);
                table.ForeignKey(
                    name: "FK_line_items_categories_category_id",
                    column: x => x.category_id,
                    principalSchema: "public",
                    principalTable: "categories",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_line_items_merchants_merchant_id",
                    column: x => x.merchant_id,
                    principalSchema: "public",
                    principalTable: "merchants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_line_items_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql(SeedTopLevelCategoriesSql);

        migrationBuilder.Sql(SeedDefaultWalletSql);

        migrationBuilder.Sql(SeedSubCategoriesSql);

        migrationBuilder.CreateIndex(
            name: "IX_categories_parent_id",
            schema: "public",
            table: "categories",
            column: "parent_id");

        migrationBuilder.CreateIndex(
            name: "IX_categories_slug",
            schema: "public",
            table: "categories",
            column: "slug",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_categorization_jobs_status_run_after",
            schema: "public",
            table: "categorization_jobs",
            columns: new[] { "status", "run_after" });

        migrationBuilder.CreateIndex(
            name: "IX_categorization_jobs_transaction_id",
            schema: "public",
            table: "categorization_jobs",
            column: "transaction_id");

        migrationBuilder.CreateIndex(
            name: "IX_line_items_category_id",
            schema: "public",
            table: "line_items",
            column: "category_id");

        migrationBuilder.CreateIndex(
            name: "IX_line_items_merchant_id",
            schema: "public",
            table: "line_items",
            column: "merchant_id");

        migrationBuilder.CreateIndex(
            name: "IX_line_items_transaction_id",
            schema: "public",
            table: "line_items",
            column: "transaction_id");

        migrationBuilder.CreateIndex(
            name: "IX_merchant_aliases_merchant_id",
            schema: "public",
            table: "merchant_aliases",
            column: "merchant_id");

        migrationBuilder.CreateIndex(
            name: "IX_transactions_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "transactions",
            columns: new[] { "telegram_chat_id", "telegram_message_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_transactions_wallet_id",
            schema: "public",
            table: "transactions",
            column: "wallet_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "categorization_jobs",
            schema: "public");

        migrationBuilder.DropTable(
            name: "line_items",
            schema: "public");

        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS merchant_aliases_no_truncate ON public.merchant_aliases;
            DROP TRIGGER IF EXISTS merchant_aliases_write_once_guard ON public.merchant_aliases;
            DROP FUNCTION IF EXISTS public.merchant_aliases_write_once();
            """);

        migrationBuilder.DropTable(
            name: "merchant_aliases",
            schema: "public");

        migrationBuilder.DropTable(
            name: "categories",
            schema: "public");

        migrationBuilder.DropTable(
            name: "transactions",
            schema: "public");

        migrationBuilder.DropTable(
            name: "merchants",
            schema: "public");

        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_wallets_single_default;");

        migrationBuilder.DropTable(
            name: "wallets",
            schema: "public");

        migrationBuilder.CreateTable(
            name: "money_probe_entities",
            schema: "public",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                recorded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_money_probe_entities", x => x.id);
            });
    }
}
