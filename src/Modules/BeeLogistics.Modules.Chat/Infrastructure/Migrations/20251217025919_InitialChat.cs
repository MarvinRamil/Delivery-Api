using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Chat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "chat");

            migrationBuilder.CreateTable(
                name: "Conversations",
                schema: "chat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    DispatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConversationParticipants",
                schema: "chat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationParticipants_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "chat",
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                schema: "chat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    SenderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsEdited = table.Column<bool>(type: "boolean", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "chat",
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationParticipants_ConversationId_UserId",
                schema: "chat",
                table: "ConversationParticipants",
                columns: new[] { "ConversationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_BookingId",
                schema: "chat",
                table: "Conversations",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_DispatchId",
                schema: "chat",
                table: "Conversations",
                column: "DispatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId",
                schema: "chat",
                table: "Messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_SentAt",
                schema: "chat",
                table: "Messages",
                column: "SentAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationParticipants",
                schema: "chat");

            migrationBuilder.DropTable(
                name: "Messages",
                schema: "chat");

            migrationBuilder.DropTable(
                name: "Conversations",
                schema: "chat");
        }
    }
}
