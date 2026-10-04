using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Verification.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerVerifications",
                schema: "verification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    DiditSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FaceMatchScore = table.Column<double>(type: "double precision", nullable: true),
                    LivenessScore = table.Column<double>(type: "double precision", nullable: true),
                    ReferenceSelfiePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IdDocumentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IdNumber = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FullName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DateOfBirth = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StatusReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerVerifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerVerifications_DiditSessionId",
                schema: "verification",
                table: "CustomerVerifications",
                column: "DiditSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerVerifications_Status",
                schema: "verification",
                table: "CustomerVerifications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerVerifications_UserId",
                schema: "verification",
                table: "CustomerVerifications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerVerifications_UserId_CreatedAt",
                schema: "verification",
                table: "CustomerVerifications",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerVerifications",
                schema: "verification");
        }
    }
}
