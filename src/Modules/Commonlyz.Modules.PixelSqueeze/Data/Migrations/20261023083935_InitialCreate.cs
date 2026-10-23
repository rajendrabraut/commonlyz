using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonlyz.Modules.PixelSqueeze.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pixelsqueeze");

            migrationBuilder.CreateTable(
                name: "Jobs",
                schema: "pixelsqueeze",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    InputFormat = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    OutputFormat = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    OriginalBytes = table.Column<long>(type: "bigint", nullable: false),
                    CompressedBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Jobs",
                schema: "pixelsqueeze");
        }
    }
}
