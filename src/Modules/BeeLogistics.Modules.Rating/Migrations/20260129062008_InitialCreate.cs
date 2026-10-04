using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Rating.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rating");

            migrationBuilder.CreateTable(
                name: "DriverRatings",
                schema: "rating",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    AverageRating = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false),
                    TotalRatings = table.Column<int>(type: "integer", nullable: false),
                    FiveStarCount = table.Column<int>(type: "integer", nullable: false),
                    FourStarCount = table.Column<int>(type: "integer", nullable: false),
                    ThreeStarCount = table.Column<int>(type: "integer", nullable: false),
                    TwoStarCount = table.Column<int>(type: "integer", nullable: false),
                    OneStarCount = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("PK_DriverRatings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Ratings",
                schema: "rating",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stars = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    RatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ratings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriverRatings_DriverId",
                schema: "rating",
                table: "DriverRatings",
                column: "DriverId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_BookingId",
                schema: "rating",
                table: "Ratings",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_CustomerId",
                schema: "rating",
                table: "Ratings",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_DriverId",
                schema: "rating",
                table: "Ratings",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_RatedAt",
                schema: "rating",
                table: "Ratings",
                column: "RatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriverRatings",
                schema: "rating");

            migrationBuilder.DropTable(
                name: "Ratings",
                schema: "rating");
        }
    }
}
