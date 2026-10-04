using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "messaging");

            migrationBuilder.CreateTable(
                name: "booking_rooms",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RoomId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RoomAlias = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DriverMatrixUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CustomerMatrixUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    FrozenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PurgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_rooms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "matrix_devices",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BeeUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LastIssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matrix_devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "matrix_identities",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BeeUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MatrixUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matrix_identities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "room_events",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatrixEventId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RoomId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sender = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: true),
                    Content = table.Column<string>(type: "jsonb", nullable: false),
                    OriginServerTs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_booking_rooms_BookingId",
                schema: "messaging",
                table: "booking_rooms",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_rooms_BookingNumber",
                schema: "messaging",
                table: "booking_rooms",
                column: "BookingNumber");

            migrationBuilder.CreateIndex(
                name: "IX_booking_rooms_RoomId",
                schema: "messaging",
                table: "booking_rooms",
                column: "RoomId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_rooms_State_FrozenAt",
                schema: "messaging",
                table: "booking_rooms",
                columns: new[] { "State", "FrozenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_matrix_devices_BeeUserId_Platform",
                schema: "messaging",
                table: "matrix_devices",
                columns: new[] { "BeeUserId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_matrix_identities_BeeUserId",
                schema: "messaging",
                table: "matrix_identities",
                column: "BeeUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_matrix_identities_MatrixUserId",
                schema: "messaging",
                table: "matrix_identities",
                column: "MatrixUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_room_events_BookingId_OriginServerTs",
                schema: "messaging",
                table: "room_events",
                columns: new[] { "BookingId", "OriginServerTs" });

            migrationBuilder.CreateIndex(
                name: "IX_room_events_MatrixEventId",
                schema: "messaging",
                table: "room_events",
                column: "MatrixEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_room_events_RoomId_OriginServerTs",
                schema: "messaging",
                table: "room_events",
                columns: new[] { "RoomId", "OriginServerTs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "booking_rooms",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "matrix_devices",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "matrix_identities",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "room_events",
                schema: "messaging");
        }
    }
}
