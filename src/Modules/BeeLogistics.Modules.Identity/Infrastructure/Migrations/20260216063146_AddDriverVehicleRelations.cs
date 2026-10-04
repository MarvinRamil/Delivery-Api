using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverVehicleRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Vehicles",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlateNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Color = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DriverVehicleAssignments",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<string>(type: "text", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverVehicleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriverVehicleAssignments_Users_DriverId",
                        column: x => x.DriverId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DriverVehicleAssignments_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "identity",
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriverVehicleAssignments_DriverId",
                schema: "identity",
                table: "DriverVehicleAssignments",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverVehicleAssignments_DriverId_IsPrimary",
                schema: "identity",
                table: "DriverVehicleAssignments",
                columns: new[] { "DriverId", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_DriverVehicleAssignments_DriverId_VehicleId",
                schema: "identity",
                table: "DriverVehicleAssignments",
                columns: new[] { "DriverId", "VehicleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverVehicleAssignments_VehicleId",
                schema: "identity",
                table: "DriverVehicleAssignments",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_PlateNumber",
                schema: "identity",
                table: "Vehicles",
                column: "PlateNumber",
                unique: true);

            // Backfill existing single-vehicle data from Users table into relation tables.
            migrationBuilder.Sql("""
                INSERT INTO identity."Vehicles" ("Id", "PlateNumber", "Model", "Color", "Type", "CreatedAt")
                SELECT
                    (
                        substr(md5('veh:' || upper(trim(u."VehiclePlate"))), 1, 8) || '-' ||
                        substr(md5('veh:' || upper(trim(u."VehiclePlate"))), 9, 4) || '-' ||
                        substr(md5('veh:' || upper(trim(u."VehiclePlate"))), 13, 4) || '-' ||
                        substr(md5('veh:' || upper(trim(u."VehiclePlate"))), 17, 4) || '-' ||
                        substr(md5('veh:' || upper(trim(u."VehiclePlate"))), 21, 12)
                    )::uuid AS "Id",
                    upper(trim(u."VehiclePlate")) AS "PlateNumber",
                    NULLIF(trim(u."VehicleModel"), '') AS "Model",
                    NULLIF(trim(u."VehicleColor"), '') AS "Color",
                    NULLIF(trim(u."VehicleType"), '') AS "Type",
                    NOW() AS "CreatedAt"
                FROM identity."Users" u
                WHERE u."VehiclePlate" IS NOT NULL
                  AND btrim(u."VehiclePlate") <> ''
                ON CONFLICT ("PlateNumber") DO NOTHING;
                """);

            migrationBuilder.Sql("""
                INSERT INTO identity."DriverVehicleAssignments" ("Id", "DriverId", "VehicleId", "IsPrimary", "AssignedAt")
                SELECT
                    (
                        substr(md5('drvveh:' || u."Id" || ':' || v."Id"::text), 1, 8) || '-' ||
                        substr(md5('drvveh:' || u."Id" || ':' || v."Id"::text), 9, 4) || '-' ||
                        substr(md5('drvveh:' || u."Id" || ':' || v."Id"::text), 13, 4) || '-' ||
                        substr(md5('drvveh:' || u."Id" || ':' || v."Id"::text), 17, 4) || '-' ||
                        substr(md5('drvveh:' || u."Id" || ':' || v."Id"::text), 21, 12)
                    )::uuid AS "Id",
                    u."Id" AS "DriverId",
                    v."Id" AS "VehicleId",
                    TRUE AS "IsPrimary",
                    NOW() AS "AssignedAt"
                FROM identity."Users" u
                INNER JOIN identity."Vehicles" v
                    ON v."PlateNumber" = upper(trim(u."VehiclePlate"))
                WHERE u."VehiclePlate" IS NOT NULL
                  AND btrim(u."VehiclePlate") <> ''
                ON CONFLICT ("DriverId", "VehicleId")
                DO UPDATE SET "IsPrimary" = TRUE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriverVehicleAssignments",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "Vehicles",
                schema: "identity");
        }
    }
}
