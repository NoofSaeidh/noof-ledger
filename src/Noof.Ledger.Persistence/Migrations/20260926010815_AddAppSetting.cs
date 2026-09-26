using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddAppSetting : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "app_setting",
            schema: "public",
            columns: table => new
            {
                key = table.Column<string>(type: "text", nullable: false),
                value = table.Column<string>(type: "text", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_app_setting", x => x.key);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "app_setting",
            schema: "public");
    }
}
