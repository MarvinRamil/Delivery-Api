using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Notification.Migrations
{
    /// <inheritdoc />
    public partial class AddSmsRecordsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SmsRecords",
                schema: "notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    To = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    MessageType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SmsRecords_CreatedAt",
                schema: "notification",
                table: "SmsRecords",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SmsRecords_Status",
                schema: "notification",
                table: "SmsRecords",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SmsRecords_To",
                schema: "notification",
                table: "SmsRecords",
                column: "To");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SmsRecords",
                schema: "notification");
        }
    }
}
