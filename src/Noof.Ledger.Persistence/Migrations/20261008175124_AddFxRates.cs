using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddFxRates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "fx_rates",
            schema: "public",
            columns: table => new
            {
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                as_of_date = table.Column<DateOnly>(type: "date", nullable: false),
                source = table.Column<string>(type: "text", nullable: false),
                units_per_eur = table.Column<decimal>(type: "numeric(24,12)", precision: 24, scale: 12, nullable: false),
                fetched_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_fx_rates", x => new { x.currency, x.as_of_date, x.source });
                table.CheckConstraint("ck_fx_rates_currency_not_eur", "currency <> 'EUR'");
                table.CheckConstraint("ck_fx_rates_units_per_eur_positive", "units_per_eur > 0");
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "fx_rates",
            schema: "public");
    }
}
