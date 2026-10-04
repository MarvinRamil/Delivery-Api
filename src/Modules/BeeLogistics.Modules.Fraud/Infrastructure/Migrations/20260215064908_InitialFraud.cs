using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Fraud.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialFraud : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fraud");

            migrationBuilder.CreateTable(
                name: "FraudEvents",
                schema: "fraud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<string>(type: "text", nullable: true),
                    Latitude = table.Column<decimal>(type: "numeric", nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric", nullable: true),
                    PreviousLatitude = table.Column<decimal>(type: "numeric", nullable: true),
                    PreviousLongitude = table.Column<decimal>(type: "numeric", nullable: true),
                    DistanceKm = table.Column<decimal>(type: "numeric", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FraudEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FraudSignals",
                schema: "fraud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleName = table.Column<string>(type: "text", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Label = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FraudSignals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FraudEvents_CustomerId",
                schema: "fraud",
                table: "FraudEvents",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_FraudEvents_DeviceId",
                schema: "fraud",
                table: "FraudEvents",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_FraudEvents_DriverId",
                schema: "fraud",
                table: "FraudEvents",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_FraudEvents_EventType_ActorId_OccurredAt",
                schema: "fraud",
                table: "FraudEvents",
                columns: new[] { "EventType", "ActorId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FraudEvents_OccurredAt",
                schema: "fraud",
                table: "FraudEvents",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_FraudSignals_ActorId",
                schema: "fraud",
                table: "FraudSignals",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_FraudSignals_DetectedAt",
                schema: "fraud",
                table: "FraudSignals",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FraudSignals_RuleName_DetectedAt",
                schema: "fraud",
                table: "FraudSignals",
                columns: new[] { "RuleName", "DetectedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FraudEvents",
                schema: "fraud");

            migrationBuilder.DropTable(
                name: "FraudSignals",
                schema: "fraud");
        }
    }
}
