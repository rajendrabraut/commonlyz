using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonlyz.Modules.ShortLink.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "shortlink");

            migrationBuilder.CreateTable(
                name: "Link",
                schema: "shortlink",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slug = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    DestinationUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    ClickCount = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Link", x => x.Id);
                    table.CheckConstraint("CK_Link_Url", "[DestinationUrl] LIKE 'https://%' OR [DestinationUrl] LIKE 'http://%'");
                });

            migrationBuilder.CreateTable(
                name: "Clicks",
                schema: "shortlink",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LinkId = table.Column<int>(type: "int", nullable: false),
                    ClickedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    Referrer = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clicks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Clicks_Link_LinkId",
                        column: x => x.LinkId,
                        principalSchema: "shortlink",
                        principalTable: "Link",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clicks_LinkId_ClickedUtc",
                schema: "shortlink",
                table: "Clicks",
                columns: new[] { "LinkId", "ClickedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Link_Slug",
                schema: "shortlink",
                table: "Link",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Clicks",
                schema: "shortlink");

            migrationBuilder.DropTable(
                name: "Link",
                schema: "shortlink");
        }
    }
}
