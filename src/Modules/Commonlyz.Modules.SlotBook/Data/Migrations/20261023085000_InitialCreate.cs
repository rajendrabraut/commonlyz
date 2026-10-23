using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonlyz.Modules.SlotBook.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "slotbook");

            migrationBuilder.CreateTable(
                name: "MeetingTypes",
                schema: "slotbook",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slug = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    BufferMinutes = table.Column<int>(type: "int", nullable: false),
                    MinNoticeMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxDaysAhead = table.Column<int>(type: "int", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AvailabilityRule",
                schema: "slotbook",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MeetingTypeId = table.Column<int>(type: "int", nullable: false),
                    DayOfWeek = table.Column<int>(type: "int", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvailabilityRule", x => x.Id);
                    table.CheckConstraint("CK_Rule_Range", "[EndTime] > [StartTime]");
                    table.ForeignKey(
                        name: "FK_AvailabilityRule_MeetingTypes_MeetingTypeId",
                        column: x => x.MeetingTypeId,
                        principalSchema: "slotbook",
                        principalTable: "MeetingTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Booking",
                schema: "slotbook",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MeetingTypeId = table.Column<int>(type: "int", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    GuestName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    GuestEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    CancelToken = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Booking", x => x.Id);
                    table.CheckConstraint("CK_Booking_Range", "[EndUtc] > [StartUtc]");
                    table.ForeignKey(
                        name: "FK_Booking_MeetingTypes_MeetingTypeId",
                        column: x => x.MeetingTypeId,
                        principalSchema: "slotbook",
                        principalTable: "MeetingTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AvailabilityRule_MeetingTypeId",
                schema: "slotbook",
                table: "AvailabilityRule",
                column: "MeetingTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Booking_CancelToken",
                schema: "slotbook",
                table: "Booking",
                column: "CancelToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Booking_MeetingTypeId",
                schema: "slotbook",
                table: "Booking",
                column: "MeetingTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Booking_StartUtc_EndUtc",
                schema: "slotbook",
                table: "Booking",
                columns: new[] { "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTypes_Slug",
                schema: "slotbook",
                table: "MeetingTypes",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AvailabilityRule",
                schema: "slotbook");

            migrationBuilder.DropTable(
                name: "Booking",
                schema: "slotbook");

            migrationBuilder.DropTable(
                name: "MeetingTypes",
                schema: "slotbook");
        }
    }
}
