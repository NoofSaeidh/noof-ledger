using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddCaptureModel : Migration
{
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

        migrationBuilder.InsertData(
            schema: "public",
            table: "categories",
            columns: new[] { "id", "is_active", "name_en", "name_ru", "parent_id", "slug" },
            values: new object[,]
            {
                { new Guid("00000000-0000-0000-0001-000000000001"), true, "Groceries", "Продукты", null, "groceries" },
                { new Guid("00000000-0000-0000-0001-000000000002"), true, "Food & Drink", "Еда и напитки", null, "food-drink" },
                { new Guid("00000000-0000-0000-0001-000000000003"), true, "Transport", "Транспорт", null, "transport" },
                { new Guid("00000000-0000-0000-0001-000000000004"), true, "Housing", "Жильё", null, "housing" },
                { new Guid("00000000-0000-0000-0001-000000000005"), true, "Utilities", "Коммунальные услуги", null, "utilities" },
                { new Guid("00000000-0000-0000-0001-000000000006"), true, "Health", "Здоровье", null, "health" },
                { new Guid("00000000-0000-0000-0001-000000000007"), true, "Shopping", "Покупки", null, "shopping" },
                { new Guid("00000000-0000-0000-0001-000000000008"), true, "Entertainment", "Развлечения", null, "entertainment" },
                { new Guid("00000000-0000-0000-0001-000000000009"), true, "Travel", "Путешествия", null, "travel" },
                { new Guid("00000000-0000-0000-0001-000000000010"), true, "Education", "Образование", null, "education" },
                { new Guid("00000000-0000-0000-0001-000000000011"), true, "Subscriptions", "Подписки", null, "subscriptions" },
                { new Guid("00000000-0000-0000-0001-000000000012"), true, "Gifts & Donations", "Подарки и пожертвования", null, "gifts-donations" },
                { new Guid("00000000-0000-0000-0001-000000000013"), true, "Fees & Charges", "Комиссии и сборы", null, "fees-charges" },
                { new Guid("00000000-0000-0000-0001-000000000014"), true, "Personal Care", "Личная гигиена", null, "personal-care" },
                { new Guid("00000000-0000-0000-0001-000000000015"), true, "Other", "Прочее", null, "other" }
            });

        migrationBuilder.InsertData(
            schema: "public",
            table: "wallets",
            columns: new[] { "id", "currency", "is_default", "name" },
            values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), "RSD", true, "Main Wallet" });

        migrationBuilder.InsertData(
            schema: "public",
            table: "categories",
            columns: new[] { "id", "is_active", "name_en", "name_ru", "parent_id", "slug" },
            values: new object[,]
            {
                { new Guid("00000000-0000-0000-0001-000000000016"), true, "Restaurants", "Рестораны", new Guid("00000000-0000-0000-0001-000000000002"), "restaurants" },
                { new Guid("00000000-0000-0000-0001-000000000017"), true, "Coffee", "Кофе", new Guid("00000000-0000-0000-0001-000000000002"), "coffee" },
                { new Guid("00000000-0000-0000-0001-000000000018"), true, "Fuel", "Топливо", new Guid("00000000-0000-0000-0001-000000000003"), "fuel" },
                { new Guid("00000000-0000-0000-0001-000000000019"), true, "Public Transport", "Общественный транспорт", new Guid("00000000-0000-0000-0001-000000000003"), "public-transport" },
                { new Guid("00000000-0000-0000-0001-000000000020"), true, "Clothing", "Одежда", new Guid("00000000-0000-0000-0001-000000000007"), "clothing" }
            });

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
