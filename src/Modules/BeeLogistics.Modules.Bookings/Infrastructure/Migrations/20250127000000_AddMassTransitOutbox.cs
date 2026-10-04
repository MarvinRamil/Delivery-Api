using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMassTransitOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MassTransit Outbox tables
            // These tables store messages that need to be published after database transaction commits
            // This ensures exactly-once delivery semantics
            
            migrationBuilder.CreateTable(
                name: "InboxState",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<string>(type: "text", nullable: false),
                    ConsumerId = table.Column<string>(type: "text", nullable: false),
                    LockId = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceiveCount = table.Column<int>(type: "integer", nullable: false),
                    ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "sales",
                columns: table => new
                {
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnqueueTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Headers = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    InboxMessageId = table.Column<string>(type: "text", nullable: true),
                    InboxConsumerId = table.Column<string>(type: "text", nullable: true),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    MessageType = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceAddress = table.Column<string>(type: "text", nullable: true),
                    DestinationAddress = table.Column<string>(type: "text", nullable: true),
                    ResponseAddress = table.Column<string>(type: "text", nullable: true),
                    FaultAddress = table.Column<string>(type: "text", nullable: true),
                    ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.SequenceNumber);
                });

            migrationBuilder.CreateTable(
                name: "OutboxState",
                schema: "sales",
                columns: table => new
                {
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    LockId = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxState", x => x.OutboxId);
                });

            // Create indexes for performance
            migrationBuilder.CreateIndex(
                name: "IX_InboxState_MessageId_ConsumerId",
                schema: "sales",
                table: "InboxState",
                columns: new[] { "MessageId", "ConsumerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_EnqueueTime",
                schema: "sales",
                table: "OutboxMessage",
                column: "EnqueueTime");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ExpirationTime",
                schema: "sales",
                table: "OutboxMessage",
                column: "ExpirationTime");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
                schema: "sales",
                table: "OutboxMessage",
                columns: new[] { "InboxMessageId", "InboxConsumerId", "SequenceNumber" },
                unique: true,
                filter: "\"InboxMessageId\" IS NOT NULL AND \"InboxConsumerId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_OutboxId_SequenceNumber",
                schema: "sales",
                table: "OutboxMessage",
                columns: new[] { "OutboxId", "SequenceNumber" },
                unique: true,
                filter: "\"OutboxId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxState_Created",
                schema: "sales",
                table: "OutboxState",
                column: "Created");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxState",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "OutboxState",
                schema: "sales");
        }
    }
}
