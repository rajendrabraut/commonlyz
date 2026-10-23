using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonlyz.Web.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "OutboxEmail",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ToAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 100000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    SentUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    FailedUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEmail", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmail_CreatedUtc",
                schema: "notifications",
                table: "OutboxEmail",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmail_Pending",
                schema: "notifications",
                table: "OutboxEmail",
                column: "NextAttemptUtc",
                filter: "[SentUtc] IS NULL AND [FailedUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxEmail",
                schema: "notifications");
        }
    }
}
