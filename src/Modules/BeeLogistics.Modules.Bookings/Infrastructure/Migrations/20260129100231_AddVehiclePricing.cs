using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVehiclePricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VehiclePricings",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Types = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    BaseFare = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    PerKm0to5 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    PerKmAbove5 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    AdditionalStopFee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    WeightLimitKg = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    WeightSurchargePerKg = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    SizeLimit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LongDistanceBaseFare = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    LongDistancePerKm41to60 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    LongDistancePerKmAbove60 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    SurchargeInfo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehiclePricings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehiclePricingVersions",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehiclePricingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    BaseFare = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    PerKm0to5 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    PerKmAbove5 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    AdditionalStopFee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    WeightLimitKg = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    WeightSurchargePerKg = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    SizeLimit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Types = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LongDistanceBaseFare = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    LongDistancePerKm41to60 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    LongDistancePerKmAbove60 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    SurchargeInfo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedByUserName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehiclePricingVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehiclePricingVersions_VehiclePricings_VehiclePricingId",
                        column: x => x.VehiclePricingId,
                        principalSchema: "sales",
                        principalTable: "VehiclePricings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePricings_IsActive",
                schema: "sales",
                table: "VehiclePricings",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePricings_VehicleType",
                schema: "sales",
                table: "VehiclePricings",
                column: "VehicleType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePricings_Version",
                schema: "sales",
                table: "VehiclePricings",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePricingVersions_CreatedAt",
                schema: "sales",
                table: "VehiclePricingVersions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePricingVersions_VehiclePricingId",
                schema: "sales",
                table: "VehiclePricingVersions",
                column: "VehiclePricingId");

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePricingVersions_VehiclePricingId_Version",
                schema: "sales",
                table: "VehiclePricingVersions",
                columns: new[] { "VehiclePricingId", "Version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VehiclePricingVersions",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "VehiclePricings",
                schema: "sales");
        }
    }
}
