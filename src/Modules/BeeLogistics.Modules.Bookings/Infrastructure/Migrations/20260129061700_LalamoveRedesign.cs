using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LalamoveRedesign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TruckType",
                schema: "sales",
                table: "Bookings",
                newName: "VehicleType");

            migrationBuilder.AddColumn<decimal>(
                name: "DriverRating",
                schema: "sales",
                table: "DriverBookingOffers",
                type: "numeric(3,2)",
                precision: 3,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EstimatedArrivalMinutes",
                schema: "sales",
                table: "DriverBookingOffers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFavouriteDriver",
                schema: "sales",
                table: "DriverBookingOffers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "sales",
                table: "Bookings",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<decimal>(
                name: "DistanceKm",
                schema: "sales",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DriverAssignedAt",
                schema: "sales",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedFare",
                schema: "sales",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "FavouriteDriverId",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FinalFare",
                schema: "sales",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PriorityFee",
                schema: "sales",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledDateTime",
                schema: "sales",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduledPickupWindow",
                schema: "sales",
                table: "Bookings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedDriverId",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceType",
                schema: "sales",
                table: "Bookings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "DeliveryStops",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Latitude = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    ContactName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ContactPhone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ArrivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryStops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryStops_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalSchema: "sales",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FavouriteDrivers",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FavouriteDrivers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tips",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    TippedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tips_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalSchema: "sales",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProofOfDeliveries",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    StopId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImagePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SignaturePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecipientName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProofOfDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProofOfDeliveries_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalSchema: "sales",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProofOfDeliveries_DeliveryStops_StopId",
                        column: x => x.StopId,
                        principalSchema: "sales",
                        principalTable: "DeliveryStops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriverBookingOffers_IsFavouriteDriver",
                schema: "sales",
                table: "DriverBookingOffers",
                column: "IsFavouriteDriver");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_FavouriteDriverId",
                schema: "sales",
                table: "Bookings",
                column: "FavouriteDriverId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_ScheduledDateTime",
                schema: "sales",
                table: "Bookings",
                column: "ScheduledDateTime");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_SelectedDriverId",
                schema: "sales",
                table: "Bookings",
                column: "SelectedDriverId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_ServiceType",
                schema: "sales",
                table: "Bookings",
                column: "ServiceType");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_Status",
                schema: "sales",
                table: "Bookings",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryStops_BookingId",
                schema: "sales",
                table: "DeliveryStops",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryStops_BookingId_Sequence",
                schema: "sales",
                table: "DeliveryStops",
                columns: new[] { "BookingId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryStops_Status",
                schema: "sales",
                table: "DeliveryStops",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_FavouriteDrivers_CustomerId",
                schema: "sales",
                table: "FavouriteDrivers",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_FavouriteDrivers_CustomerId_DriverId",
                schema: "sales",
                table: "FavouriteDrivers",
                columns: new[] { "CustomerId", "DriverId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FavouriteDrivers_DriverId",
                schema: "sales",
                table: "FavouriteDrivers",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_ProofOfDeliveries_BookingId",
                schema: "sales",
                table: "ProofOfDeliveries",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_ProofOfDeliveries_StopId",
                schema: "sales",
                table: "ProofOfDeliveries",
                column: "StopId");

            migrationBuilder.CreateIndex(
                name: "IX_Tips_BookingId",
                schema: "sales",
                table: "Tips",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tips_CustomerId",
                schema: "sales",
                table: "Tips",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Tips_DriverId",
                schema: "sales",
                table: "Tips",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_Tips_TippedAt",
                schema: "sales",
                table: "Tips",
                column: "TippedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FavouriteDrivers",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "ProofOfDeliveries",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "Tips",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "DeliveryStops",
                schema: "sales");

            migrationBuilder.DropIndex(
                name: "IX_DriverBookingOffers_IsFavouriteDriver",
                schema: "sales",
                table: "DriverBookingOffers");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_FavouriteDriverId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_ScheduledDateTime",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_SelectedDriverId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_ServiceType",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_Status",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "DriverRating",
                schema: "sales",
                table: "DriverBookingOffers");

            migrationBuilder.DropColumn(
                name: "EstimatedArrivalMinutes",
                schema: "sales",
                table: "DriverBookingOffers");

            migrationBuilder.DropColumn(
                name: "IsFavouriteDriver",
                schema: "sales",
                table: "DriverBookingOffers");

            migrationBuilder.DropColumn(
                name: "DistanceKm",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "DriverAssignedAt",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "EstimatedFare",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "FavouriteDriverId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "FinalFare",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PriorityFee",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ScheduledDateTime",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ScheduledPickupWindow",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "SelectedDriverId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ServiceType",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.RenameColumn(
                name: "VehicleType",
                schema: "sales",
                table: "Bookings",
                newName: "TruckType");

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                schema: "sales",
                table: "Bookings",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30);
        }
    }
}
