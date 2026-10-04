using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverWalletVerificationFailureReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayMongoVerificationFailureReason",
                schema: "drivers",
                table: "DriverWallets",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayMongoVerificationFailureReason",
                schema: "drivers",
                table: "DriverWallets");
        }
    }
}
