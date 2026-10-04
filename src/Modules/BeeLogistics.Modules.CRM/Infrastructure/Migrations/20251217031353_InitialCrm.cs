using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCrm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "crm");

            migrationBuilder.CreateTable(
                name: "CustomerProfiles",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CompanyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Address = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Tags = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TotalBookings = table.Column<int>(type: "integer", nullable: false),
                    TotalSpent = table.Column<decimal>(type: "numeric", nullable: false),
                    LastBookingDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastContactDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FaqArticles",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    ViewCount = table.Column<int>(type: "integer", nullable: false),
                    HelpfulCount = table.Column<int>(type: "integer", nullable: false),
                    NotHelpfulCount = table.Column<int>(type: "integer", nullable: false),
                    Tags = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaqArticles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FaqCategories",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Icon = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaqCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerNotes",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerNotes_CustomerProfiles_CustomerProfileId",
                        column: x => x.CustomerProfileId,
                        principalSchema: "crm",
                        principalTable: "CustomerProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupportTickets",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CustomerProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AssignedToUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    AssignedToName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupportTickets_CustomerProfiles_CustomerProfileId",
                        column: x => x.CustomerProfileId,
                        principalSchema: "crm",
                        principalTable: "CustomerProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerNotes_CustomerProfileId",
                schema: "crm",
                table: "CustomerNotes",
                column: "CustomerProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerProfiles_Email",
                schema: "crm",
                table: "CustomerProfiles",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerProfiles_UserId",
                schema: "crm",
                table: "CustomerProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FaqArticles_Category",
                schema: "crm",
                table: "FaqArticles",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_FaqArticles_IsPublished",
                schema: "crm",
                table: "FaqArticles",
                column: "IsPublished");

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_CustomerProfileId",
                schema: "crm",
                table: "SupportTickets",
                column: "CustomerProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_Status",
                schema: "crm",
                table: "SupportTickets",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_TicketNumber",
                schema: "crm",
                table: "SupportTickets",
                column: "TicketNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerNotes",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "FaqArticles",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "FaqCategories",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "SupportTickets",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "CustomerProfiles",
                schema: "crm");
        }
    }
}
