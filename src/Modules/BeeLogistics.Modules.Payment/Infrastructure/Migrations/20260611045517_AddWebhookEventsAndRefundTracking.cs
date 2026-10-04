using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Payment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookEventsAndRefundTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RefundAmount",
                schema: "payment",
                table: "Payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RefundDriverId",
                schema: "payment",
                table: "Payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WebhookEvents",
                schema: "payment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EventKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_XenditRefundId",
                schema: "payment",
                table: "Payments",
                column: "XenditRefundId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_Provider_EventKey",
                schema: "payment",
                table: "WebhookEvents",
                columns: new[] { "Provider", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_ReceivedAt",
                schema: "payment",
                table: "WebhookEvents",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookEvents",
                schema: "payment");

            migrationBuilder.DropIndex(
                name: "IX_Payments_XenditRefundId",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundAmount",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundDriverId",
                schema: "payment",
                table: "Payments");
        }
    }
}
