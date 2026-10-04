using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Notification.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailStatusTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ensure the notification schema exists
            migrationBuilder.EnsureSchema(
                name: "notification");

            migrationBuilder.CreateTable(
                name: "EmailRecords",
                schema: "notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    To = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    From = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FromName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HangfireJobId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailRecords_CreatedAt",
                schema: "notification",
                table: "EmailRecords",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EmailRecords_HangfireJobId",
                schema: "notification",
                table: "EmailRecords",
                column: "HangfireJobId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailRecords_Status",
                schema: "notification",
                table: "EmailRecords",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_EmailRecords_To",
                schema: "notification",
                table: "EmailRecords",
                column: "To");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailRecords",
                schema: "notification");
        }
    }
}
