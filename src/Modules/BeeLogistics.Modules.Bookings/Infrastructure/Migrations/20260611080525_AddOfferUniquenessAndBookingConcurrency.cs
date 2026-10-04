using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOfferUniquenessAndBookingConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: the Booking concurrency token maps to PostgreSQL's implicit xmin
            // system column, which exists on every table — no AddColumn needed.
            // (EF scaffolds one; it must not be executed.)

            // Remove duplicate offers before enforcing uniqueness. Keep the best row
            // per (BookingId, DriverId): Accepted over Pending over anything else,
            // then the earliest offer.
            migrationBuilder.Sql(@"
                DELETE FROM sales.""DriverBookingOffers""
                WHERE ""Id"" IN (
                    SELECT ""Id"" FROM (
                        SELECT ""Id"", ROW_NUMBER() OVER (
                            PARTITION BY ""BookingId"", ""DriverId""
                            ORDER BY
                                CASE ""Status"" WHEN 'Accepted' THEN 0 WHEN 'Pending' THEN 1 ELSE 2 END,
                                ""OfferedAt"",
                                ""Id""
                        ) AS rn
                        FROM sales.""DriverBookingOffers""
                    ) ranked
                    WHERE rn > 1
                );
            ");

            migrationBuilder.CreateIndex(
                name: "IX_DriverBookingOffers_BookingId_DriverId",
                schema: "sales",
                table: "DriverBookingOffers",
                columns: new[] { "BookingId", "DriverId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DriverBookingOffers_BookingId_DriverId",
                schema: "sales",
                table: "DriverBookingOffers");
        }
    }
}
