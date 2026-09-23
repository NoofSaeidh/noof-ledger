using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddOccurredOn : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>(
            name: "occurred_on",
            schema: "public",
            table: "transactions",
            type: "date",
            nullable: true);

        // Each existing row gets the local day its OWN stored zone puts it on - the rule capture applies to
        // new rows from now on. Nullable first, backfilled, then NOT NULL: no row ever carries an invented
        // default date.
        migrationBuilder.Sql("UPDATE public.transactions SET occurred_on = (occurred_at AT TIME ZONE time_zone_id)::date;");

        migrationBuilder.AlterColumn<DateOnly>(
            name: "occurred_on",
            schema: "public",
            table: "transactions",
            type: "date",
            nullable: false,
            oldClrType: typeof(DateOnly),
            oldType: "date",
            oldNullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "occurred_on",
            schema: "public",
            table: "transactions");
    }
}
