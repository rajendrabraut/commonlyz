using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonlyz.Modules.TimeTrack.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "timetrack");

            migrationBuilder.CreateTable(
                name: "Projects",
                schema: "timetrack",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ColorHex = table.Column<string>(type: "char(7)", unicode: false, fixedLength: true, maxLength: 7, nullable: false),
                    ArchivedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "timetrack",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "TimeEntry",
                schema: "timetrack",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    StartUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeEntry", x => x.Id);
                    table.CheckConstraint("CK_TimeEntry_Range", "[EndUtc] IS NULL OR [EndUtc] > [StartUtc]");
                    table.ForeignKey(
                        name: "FK_TimeEntry_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "timetrack",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Name",
                schema: "timetrack",
                table: "Projects",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntry_ProjectId",
                schema: "timetrack",
                table: "TimeEntry",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntry_UserId_StartUtc",
                schema: "timetrack",
                table: "TimeEntry",
                columns: new[] { "UserId", "StartUtc" })
                .Annotation("SqlServer:Include", new[] { "EndUtc", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "UX_TimeEntry_OneRunningPerUser",
                schema: "timetrack",
                table: "TimeEntry",
                column: "UserId",
                unique: true,
                filter: "[EndUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Settings",
                schema: "timetrack");

            migrationBuilder.DropTable(
                name: "TimeEntry",
                schema: "timetrack");

            migrationBuilder.DropTable(
                name: "Projects",
                schema: "timetrack");
        }
    }
}
