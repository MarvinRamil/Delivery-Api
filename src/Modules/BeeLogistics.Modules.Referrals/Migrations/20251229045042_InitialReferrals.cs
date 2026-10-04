using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Referrals.Migrations
{
    /// <inheritdoc />
    public partial class InitialReferrals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "referrals");

            migrationBuilder.CreateTable(
                name: "PointsTransactions",
                schema: "referrals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Points = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RelatedReferralId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransactionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointsTransactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReferralCodes",
                schema: "referrals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UserType = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ReferralLink = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferralCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserPoints",
                schema: "referrals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TotalPoints = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AvailablePoints = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PendingPoints = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Referrals",
                schema: "referrals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferralCodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferrerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferredUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferrerType = table.Column<int>(type: "integer", nullable: false),
                    ReferredType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReferredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RewardedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PointsAwarded = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Referrals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Referrals_ReferralCodes_ReferralCodeId",
                        column: x => x.ReferralCodeId,
                        principalSchema: "referrals",
                        principalTable: "ReferralCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PointsTransactions_RelatedReferralId",
                schema: "referrals",
                table: "PointsTransactions",
                column: "RelatedReferralId");

            migrationBuilder.CreateIndex(
                name: "IX_PointsTransactions_TransactionDate",
                schema: "referrals",
                table: "PointsTransactions",
                column: "TransactionDate");

            migrationBuilder.CreateIndex(
                name: "IX_PointsTransactions_UserId",
                schema: "referrals",
                table: "PointsTransactions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PointsTransactions_UserId_TransactionDate",
                schema: "referrals",
                table: "PointsTransactions",
                columns: new[] { "UserId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCodes_Code",
                schema: "referrals",
                table: "ReferralCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCodes_IsActive",
                schema: "referrals",
                table: "ReferralCodes",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCodes_UserId",
                schema: "referrals",
                table: "ReferralCodes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferralCodeId",
                schema: "referrals",
                table: "Referrals",
                column: "ReferralCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferredUserId",
                schema: "referrals",
                table: "Referrals",
                column: "ReferredUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferrerId",
                schema: "referrals",
                table: "Referrals",
                column: "ReferrerId");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferrerId_Status",
                schema: "referrals",
                table: "Referrals",
                columns: new[] { "ReferrerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_Status",
                schema: "referrals",
                table: "Referrals",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UserPoints_UserId",
                schema: "referrals",
                table: "UserPoints",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PointsTransactions",
                schema: "referrals");

            migrationBuilder.DropTable(
                name: "Referrals",
                schema: "referrals");

            migrationBuilder.DropTable(
                name: "UserPoints",
                schema: "referrals");

            migrationBuilder.DropTable(
                name: "ReferralCodes",
                schema: "referrals");
        }
    }
}
