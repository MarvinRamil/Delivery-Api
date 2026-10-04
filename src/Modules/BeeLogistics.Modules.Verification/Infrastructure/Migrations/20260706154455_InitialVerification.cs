using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Verification.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "verification");

            migrationBuilder.CreateTable(
                name: "DriverVerifications",
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
                    ReferenceEmbedding = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("PK_DriverVerifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShiftFaceChecks",
                schema: "verification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    MatchScore = table.Column<double>(type: "double precision", nullable: true),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShiftFaceChecks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriverVerifications_DiditSessionId",
                schema: "verification",
                table: "DriverVerifications",
                column: "DiditSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverVerifications_Status",
                schema: "verification",
                table: "DriverVerifications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DriverVerifications_UserId",
                schema: "verification",
                table: "DriverVerifications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverVerifications_UserId_CreatedAt",
                schema: "verification",
                table: "DriverVerifications",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShiftFaceChecks_UserId",
                schema: "verification",
                table: "ShiftFaceChecks",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ShiftFaceChecks_UserId_CreatedAt",
                schema: "verification",
                table: "ShiftFaceChecks",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriverVerifications",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "ShiftFaceChecks",
                schema: "verification");
        }
    }
}
