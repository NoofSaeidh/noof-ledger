using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddTransactionRevisions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "transaction_revisions",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                revision_number = table.Column<int>(type: "integer", nullable: false),
                kind = table.Column<int>(type: "integer", nullable: false),
                instruction = table.Column<string>(type: "text", nullable: true),
                status_before = table.Column<int>(type: "integer", nullable: false),
                status_after = table.Column<int>(type: "integer", nullable: false),
                snapshot = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_transaction_revisions", x => x.id);
                table.ForeignKey(
                    name: "FK_transaction_revisions_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_transaction_revisions_transaction_id_revision_number",
            schema: "public",
            table: "transaction_revisions",
            columns: new[] { "transaction_id", "revision_number" },
            unique: true);

        migrationBuilder.Sql(
            """
            CREATE FUNCTION public.transaction_revisions_append_only() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'transaction_revisions is append-only: % on % is not permitted', TG_OP, TG_TABLE_NAME;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER transaction_revisions_append_only_guard
                BEFORE UPDATE OR DELETE ON public.transaction_revisions
                FOR EACH ROW EXECUTE FUNCTION public.transaction_revisions_append_only();

            CREATE TRIGGER transaction_revisions_no_truncate
                BEFORE TRUNCATE ON public.transaction_revisions
                FOR EACH STATEMENT EXECUTE FUNCTION public.transaction_revisions_append_only();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS transaction_revisions_no_truncate ON public.transaction_revisions;
            DROP TRIGGER IF EXISTS transaction_revisions_append_only_guard ON public.transaction_revisions;
            DROP FUNCTION IF EXISTS public.transaction_revisions_append_only();
            """);

        migrationBuilder.DropTable(
            name: "transaction_revisions",
            schema: "public");
    }
}
