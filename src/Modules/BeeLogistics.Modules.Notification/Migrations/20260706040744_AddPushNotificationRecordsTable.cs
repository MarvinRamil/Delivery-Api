using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Notification.Migrations
{
    /// <inheritdoc />
    public partial class AddPushNotificationRecordsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PushNotificationRecords",
                schema: "notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TargetMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetValue = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AppType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DevicesSent = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushNotificationRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushNotificationRecords_AppType",
                schema: "notification",
                table: "PushNotificationRecords",
                column: "AppType");

            migrationBuilder.CreateIndex(
                name: "IX_PushNotificationRecords_CreatedAt",
                schema: "notification",
                table: "PushNotificationRecords",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PushNotificationRecords_Status",
                schema: "notification",
                table: "PushNotificationRecords",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushNotificationRecords",
                schema: "notification");
        }
    }
}
