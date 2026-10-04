using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLastZammadArticleId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastZammadArticleId",
                schema: "crm",
                table: "SupportTickets",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastZammadArticleId",
                schema: "crm",
                table: "SupportTickets");
        }
    }
}
