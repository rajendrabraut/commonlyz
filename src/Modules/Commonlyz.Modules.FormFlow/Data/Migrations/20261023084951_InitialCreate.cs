using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonlyz.Modules.FormFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "formflow");

            migrationBuilder.CreateTable(
                name: "Forms",
                schema: "formflow",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slug = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsOpen = table.Column<bool>(type: "bit", nullable: false),
                    PublishedVersionId = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Forms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Question",
                schema: "formflow",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FormId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Prompt = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Help = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    Options = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Question", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Question_Forms_FormId",
                        column: x => x.FormId,
                        principalSchema: "formflow",
                        principalTable: "Forms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Version",
                schema: "formflow",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FormId = table.Column<int>(type: "int", nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    QuestionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Version", x => x.Id);
                    table.CheckConstraint("CK_Version_Json", "ISJSON([QuestionsJson]) = 1");
                    table.ForeignKey(
                        name: "FK_Version_Forms_FormId",
                        column: x => x.FormId,
                        principalSchema: "formflow",
                        principalTable: "Forms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Response",
                schema: "formflow",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FormId = table.Column<int>(type: "int", nullable: false),
                    FormVersionId = table.Column<int>(type: "int", nullable: false),
                    AnswersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Response", x => x.Id);
                    table.CheckConstraint("CK_Response_Json", "ISJSON([AnswersJson]) = 1");
                    table.ForeignKey(
                        name: "FK_Response_Forms_FormId",
                        column: x => x.FormId,
                        principalSchema: "formflow",
                        principalTable: "Forms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Response_Version_FormVersionId",
                        column: x => x.FormVersionId,
                        principalSchema: "formflow",
                        principalTable: "Version",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Forms_Slug",
                schema: "formflow",
                table: "Forms",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Question_FormId",
                schema: "formflow",
                table: "Question",
                column: "FormId");

            migrationBuilder.CreateIndex(
                name: "IX_Response_FormId_SubmittedUtc",
                schema: "formflow",
                table: "Response",
                columns: new[] { "FormId", "SubmittedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Response_FormVersionId",
                schema: "formflow",
                table: "Response",
                column: "FormVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Version_FormId_VersionNo",
                schema: "formflow",
                table: "Version",
                columns: new[] { "FormId", "VersionNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Question",
                schema: "formflow");

            migrationBuilder.DropTable(
                name: "Response",
                schema: "formflow");

            migrationBuilder.DropTable(
                name: "Version",
                schema: "formflow");

            migrationBuilder.DropTable(
                name: "Forms",
                schema: "formflow");
        }
    }
}
