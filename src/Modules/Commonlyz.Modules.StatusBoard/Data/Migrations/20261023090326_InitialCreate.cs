using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonlyz.Modules.StatusBoard.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "status");

            migrationBuilder.CreateTable(
                name: "Component",
                schema: "status",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Component", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Incidents",
                schema: "status",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Impact = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IncidentComponents",
                schema: "status",
                columns: table => new
                {
                    IncidentId = table.Column<int>(type: "int", nullable: false),
                    ComponentId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentComponents", x => new { x.IncidentId, x.ComponentId });
                    table.ForeignKey(
                        name: "FK_IncidentComponents_Component_ComponentId",
                        column: x => x.ComponentId,
                        principalSchema: "status",
                        principalTable: "Component",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IncidentComponents_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalSchema: "status",
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Updates",
                schema: "status",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IncidentId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Updates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Updates_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalSchema: "status",
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentComponents_ComponentId",
                schema: "status",
                table: "IncidentComponents",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ResolvedUtc",
                schema: "status",
                table: "Incidents",
                column: "ResolvedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Updates_IncidentId",
                schema: "status",
                table: "Updates",
                column: "IncidentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncidentComponents",
                schema: "status");

            migrationBuilder.DropTable(
                name: "Updates",
                schema: "status");

            migrationBuilder.DropTable(
                name: "Component",
                schema: "status");

            migrationBuilder.DropTable(
                name: "Incidents",
                schema: "status");
        }
    }
}
