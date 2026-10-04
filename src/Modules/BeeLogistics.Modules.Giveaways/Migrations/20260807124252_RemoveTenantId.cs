using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Giveaways.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "giveaways",
                table: "GiveawayWinners");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "giveaways",
                table: "Giveaways");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "giveaways",
                table: "GiveawayPrizes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "giveaways",
                table: "GiveawayEntries");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "giveaways",
                table: "Campaigns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "giveaways",
                table: "GiveawayWinners",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "giveaways",
                table: "Giveaways",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "giveaways",
                table: "GiveawayPrizes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "giveaways",
                table: "GiveawayEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "giveaways",
                table: "Campaigns",
                type: "uuid",
                nullable: true);
        }
    }
}
