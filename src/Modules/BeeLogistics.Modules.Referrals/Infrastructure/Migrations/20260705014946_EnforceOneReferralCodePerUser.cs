using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Referrals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceOneReferralCodePerUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Referrals_ReferredUserId",
                schema: "referrals",
                table: "Referrals");

            migrationBuilder.DropIndex(
                name: "IX_ReferralCodes_UserId",
                schema: "referrals",
                table: "ReferralCodes");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferredUserId",
                schema: "referrals",
                table: "Referrals",
                column: "ReferredUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCodes_UserId",
                schema: "referrals",
                table: "ReferralCodes",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Referrals_ReferredUserId",
                schema: "referrals",
                table: "Referrals");

            migrationBuilder.DropIndex(
                name: "IX_ReferralCodes_UserId",
                schema: "referrals",
                table: "ReferralCodes");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferredUserId",
                schema: "referrals",
                table: "Referrals",
                column: "ReferredUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCodes_UserId",
                schema: "referrals",
                table: "ReferralCodes",
                column: "UserId");
        }
    }
}
