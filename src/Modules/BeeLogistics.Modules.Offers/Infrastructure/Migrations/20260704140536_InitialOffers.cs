using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Offers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "offers");

            migrationBuilder.CreateTable(
                name: "Offers",
                schema: "offers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DiscountType = table.Column<int>(type: "integer", nullable: false),
                    DiscountValue = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    PromoCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TargetAudience = table.Column<int>(type: "integer", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImagePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Offers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Offers_EndsAt",
                schema: "offers",
                table: "Offers",
                column: "EndsAt");

            migrationBuilder.CreateIndex(
                name: "IX_Offers_IsActive",
                schema: "offers",
                table: "Offers",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Offers_PromoCode",
                schema: "offers",
                table: "Offers",
                column: "PromoCode");

            migrationBuilder.CreateIndex(
                name: "IX_Offers_StartsAt",
                schema: "offers",
                table: "Offers",
                column: "StartsAt");

            migrationBuilder.CreateIndex(
                name: "IX_Offers_TargetAudience",
                schema: "offers",
                table: "Offers",
                column: "TargetAudience");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Offers",
                schema: "offers");
        }
    }
}
