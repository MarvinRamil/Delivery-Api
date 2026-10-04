using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBeeBookingAssignmentSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AssignedAt",
                schema: "sales",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedByUserId",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedToTenantId",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignmentStatus",
                schema: "sales",
                table: "Bookings",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "BeeTenantId",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<decimal>(
                name: "PickupLatitude",
                schema: "sales",
                table: "Bookings",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PickupLongitude",
                schema: "sales",
                table: "Bookings",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Size",
                schema: "sales",
                table: "Bookings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeightKg",
                schema: "sales",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DriverBookingOffers",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OfferedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: false),
                    DistanceKm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverBookingOffers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriverBookingOffers_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalSchema: "sales",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_AssignedToTenantId",
                schema: "sales",
                table: "Bookings",
                column: "AssignedToTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_AssignmentStatus",
                schema: "sales",
                table: "Bookings",
                column: "AssignmentStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_BeeTenantId",
                schema: "sales",
                table: "Bookings",
                column: "BeeTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverBookingOffers_BookingId",
                schema: "sales",
                table: "DriverBookingOffers",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverBookingOffers_BookingId_SequenceNumber",
                schema: "sales",
                table: "DriverBookingOffers",
                columns: new[] { "BookingId", "SequenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_DriverBookingOffers_DriverId",
                schema: "sales",
                table: "DriverBookingOffers",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverBookingOffers_DriverId_Status",
                schema: "sales",
                table: "DriverBookingOffers",
                columns: new[] { "DriverId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DriverBookingOffers_ExpiresAt",
                schema: "sales",
                table: "DriverBookingOffers",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_DriverBookingOffers_Status",
                schema: "sales",
                table: "DriverBookingOffers",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriverBookingOffers",
                schema: "sales");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_AssignedToTenantId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_AssignmentStatus",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_BeeTenantId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AssignedAt",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AssignedByUserId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AssignedToTenantId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AssignmentStatus",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "BeeTenantId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PickupLatitude",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PickupLongitude",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "Size",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "WeightKg",
                schema: "sales",
                table: "Bookings");
        }
    }
}
