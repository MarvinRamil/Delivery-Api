using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Revenue.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialRevenue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "revenue");

            migrationBuilder.CreateTable(
                name: "PlatformCommissions",
                schema: "revenue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GrossAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CommissionRate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    CommissionAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DriverAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformCommissions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformCommissions_BookingId",
                schema: "revenue",
                table: "PlatformCommissions",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformCommissions_CreatedAt",
                schema: "revenue",
                table: "PlatformCommissions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformCommissions_DriverId",
                schema: "revenue",
                table: "PlatformCommissions",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformCommissions_Status",
                schema: "revenue",
                table: "PlatformCommissions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformCommissions",
                schema: "revenue");
        }
    }
}
