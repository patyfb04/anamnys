using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anamnys.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds BillingSystem column to Providers table.
    /// Stored as integer (EF Core default for enums). 0 = UsCpt.
    /// </summary>
    public partial class AddProviderBillingSystem : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BillingSystem",
                table: "Providers",
                type: "integer",
                nullable: false,
                defaultValue: 0);  // backfills existing rows as UsCpt
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BillingSystem",
                table: "Providers");
        }
    }
}
