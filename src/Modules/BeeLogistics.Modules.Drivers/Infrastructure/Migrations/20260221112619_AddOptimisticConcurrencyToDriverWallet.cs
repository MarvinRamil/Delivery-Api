using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOptimisticConcurrencyToDriverWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "drivers",
                table: "DriverWallets",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "drivers",
                table: "DriverWallets");
        }
    }
}
