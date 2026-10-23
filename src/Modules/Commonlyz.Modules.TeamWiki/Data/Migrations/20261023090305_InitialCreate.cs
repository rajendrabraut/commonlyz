using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonlyz.Modules.TeamWiki.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wiki");

            migrationBuilder.CreateTable(
                name: "Page",
                schema: "wiki",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Page", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Page_Page_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "wiki",
                        principalTable: "Page",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Revision",
                schema: "wiki",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PageId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SavedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SavedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Revision", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Revision_Page_PageId",
                        column: x => x.PageId,
                        principalSchema: "wiki",
                        principalTable: "Page",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Page_ParentId_Position",
                schema: "wiki",
                table: "Page",
                columns: new[] { "ParentId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_Revision_PageId_SavedUtc",
                schema: "wiki",
                table: "Revision",
                columns: new[] { "PageId", "SavedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Revision",
                schema: "wiki");

            migrationBuilder.DropTable(
                name: "Page",
                schema: "wiki");
        }
    }
}
