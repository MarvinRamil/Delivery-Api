using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Giveaways.Migrations
{
    /// <inheritdoc />
    public partial class GiveawayOverhaul : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GiveawayEntries_GiveawayId_DriverId",
                schema: "giveaways",
                table: "GiveawayEntries");

            migrationBuilder.AddColumn<string>(
                name: "DtiPermitImagePath",
                schema: "giveaways",
                table: "Giveaways",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DtiPermitNumber",
                schema: "giveaways",
                table: "Giveaways",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EntryMode",
                schema: "giveaways",
                table: "Giveaways",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxEntriesPerDriver",
                schema: "giveaways",
                table: "Giveaways",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "giveaways",
                table: "Giveaways",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "BookingId",
                schema: "giveaways",
                table: "GiveawayEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                schema: "giveaways",
                table: "GiveawayEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "GiveawayPrizes",
                schema: "giveaways",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GiveawayId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiveawayPrizes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GiveawayPrizes_Giveaways_GiveawayId",
                        column: x => x.GiveawayId,
                        principalSchema: "giveaways",
                        principalTable: "Giveaways",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GiveawayWinners",
                schema: "giveaways",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GiveawayId = table.Column<Guid>(type: "uuid", nullable: false),
                    GiveawayPrizeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    DrawnAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiveawayWinners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GiveawayWinners_GiveawayPrizes_GiveawayPrizeId",
                        column: x => x.GiveawayPrizeId,
                        principalSchema: "giveaways",
                        principalTable: "GiveawayPrizes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GiveawayWinners_Giveaways_GiveawayId",
                        column: x => x.GiveawayId,
                        principalSchema: "giveaways",
                        principalTable: "Giveaways",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Giveaways_Status",
                schema: "giveaways",
                table: "Giveaways",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_GiveawayEntries_GiveawayId_DriverId_BookingId",
                schema: "giveaways",
                table: "GiveawayEntries",
                columns: new[] { "GiveawayId", "DriverId", "BookingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GiveawayPrizes_GiveawayId_Tier",
                schema: "giveaways",
                table: "GiveawayPrizes",
                columns: new[] { "GiveawayId", "Tier" });

            migrationBuilder.CreateIndex(
                name: "IX_GiveawayWinners_DriverId",
                schema: "giveaways",
                table: "GiveawayWinners",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_GiveawayWinners_GiveawayId",
                schema: "giveaways",
                table: "GiveawayWinners",
                column: "GiveawayId");

            migrationBuilder.CreateIndex(
                name: "IX_GiveawayWinners_GiveawayPrizeId",
                schema: "giveaways",
                table: "GiveawayWinners",
                column: "GiveawayPrizeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GiveawayWinners",
                schema: "giveaways");

            migrationBuilder.DropTable(
                name: "GiveawayPrizes",
                schema: "giveaways");

            migrationBuilder.DropIndex(
                name: "IX_Giveaways_Status",
                schema: "giveaways",
                table: "Giveaways");

            migrationBuilder.DropIndex(
                name: "IX_GiveawayEntries_GiveawayId_DriverId_BookingId",
                schema: "giveaways",
                table: "GiveawayEntries");

            migrationBuilder.DropColumn(
                name: "DtiPermitImagePath",
                schema: "giveaways",
                table: "Giveaways");

            migrationBuilder.DropColumn(
                name: "DtiPermitNumber",
                schema: "giveaways",
                table: "Giveaways");

            migrationBuilder.DropColumn(
                name: "EntryMode",
                schema: "giveaways",
                table: "Giveaways");

            migrationBuilder.DropColumn(
                name: "MaxEntriesPerDriver",
                schema: "giveaways",
                table: "Giveaways");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "giveaways",
                table: "Giveaways");

            migrationBuilder.DropColumn(
                name: "BookingId",
                schema: "giveaways",
                table: "GiveawayEntries");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "giveaways",
                table: "GiveawayEntries");

            migrationBuilder.CreateIndex(
                name: "IX_GiveawayEntries_GiveawayId_DriverId",
                schema: "giveaways",
                table: "GiveawayEntries",
                columns: new[] { "GiveawayId", "DriverId" },
                unique: true);
        }
    }
}
