using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anamnys.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op: previously assumed BillingSystem existed as text, but the prior
            // migration (AddProviderBillingSystem, now properly registered with its
            // Designer.cs) already adds it as integer. Nothing left to fix here.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
