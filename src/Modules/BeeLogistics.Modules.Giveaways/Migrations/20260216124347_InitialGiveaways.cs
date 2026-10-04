using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Giveaways.Migrations
{
    /// <inheritdoc />
    public partial class InitialGiveaways : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "giveaways");

            migrationBuilder.CreateTable(
                name: "Campaigns",
                schema: "giveaways",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImagePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CtaText = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CtaRoute = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Giveaways",
                schema: "giveaways",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImagePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RewardDetails = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Giveaways", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GiveawayEntries",
                schema: "giveaways",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GiveawayId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiveawayEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GiveawayEntries_Giveaways_GiveawayId",
                        column: x => x.GiveawayId,
                        principalSchema: "giveaways",
                        principalTable: "Giveaways",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_EndDate",
                schema: "giveaways",
                table: "Campaigns",
                column: "EndDate");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_IsActive",
                schema: "giveaways",
                table: "Campaigns",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_StartDate",
                schema: "giveaways",
                table: "Campaigns",
                column: "StartDate");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Type",
                schema: "giveaways",
                table: "Campaigns",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_GiveawayEntries_DriverId",
                schema: "giveaways",
                table: "GiveawayEntries",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_GiveawayEntries_GiveawayId",
                schema: "giveaways",
                table: "GiveawayEntries",
                column: "GiveawayId");

            migrationBuilder.CreateIndex(
                name: "IX_GiveawayEntries_GiveawayId_DriverId",
                schema: "giveaways",
                table: "GiveawayEntries",
                columns: new[] { "GiveawayId", "DriverId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Giveaways_EndDate",
                schema: "giveaways",
                table: "Giveaways",
                column: "EndDate");

            migrationBuilder.CreateIndex(
                name: "IX_Giveaways_IsActive",
                schema: "giveaways",
                table: "Giveaways",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Giveaways_StartDate",
                schema: "giveaways",
                table: "Giveaways",
                column: "StartDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Campaigns",
                schema: "giveaways");

            migrationBuilder.DropTable(
                name: "GiveawayEntries",
                schema: "giveaways");

            migrationBuilder.DropTable(
                name: "Giveaways",
                schema: "giveaways");
        }
    }
}
