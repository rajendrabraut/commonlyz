using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commonlyz.Modules.SplitShare.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "splitshare");

            migrationBuilder.CreateTable(
                name: "Group",
                schema: "splitshare",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Group", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Members",
                schema: "splitshare",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Members_Group_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "splitshare",
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Expense",
                schema: "splitshare",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    PaidByMemberId = table.Column<int>(type: "int", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ExpenseDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SplitType = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Expense", x => x.Id);
                    table.CheckConstraint("CK_Expense_Amount", "[AmountMinor] > 0");
                    table.ForeignKey(
                        name: "FK_Expense_Group_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "splitshare",
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Expense_Members_PaidByMemberId",
                        column: x => x.PaidByMemberId,
                        principalSchema: "splitshare",
                        principalTable: "Members",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Settlement",
                schema: "splitshare",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    FromMemberId = table.Column<int>(type: "int", nullable: false),
                    ToMemberId = table.Column<int>(type: "int", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    SettledUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settlement", x => x.Id);
                    table.CheckConstraint("CK_Settlement_Amount", "[AmountMinor] > 0");
                    table.CheckConstraint("CK_Settlement_Parties", "[FromMemberId] <> [ToMemberId]");
                    table.ForeignKey(
                        name: "FK_Settlement_Group_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "splitshare",
                        principalTable: "Group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Settlement_Members_FromMemberId",
                        column: x => x.FromMemberId,
                        principalSchema: "splitshare",
                        principalTable: "Members",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Settlement_Members_ToMemberId",
                        column: x => x.ToMemberId,
                        principalSchema: "splitshare",
                        principalTable: "Members",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ExpenseShare",
                schema: "splitshare",
                columns: table => new
                {
                    ExpenseId = table.Column<int>(type: "int", nullable: false),
                    MemberId = table.Column<int>(type: "int", nullable: false),
                    ShareMinor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseShare", x => new { x.ExpenseId, x.MemberId });
                    table.CheckConstraint("CK_ExpenseShare_NonNegative", "[ShareMinor] >= 0");
                    table.ForeignKey(
                        name: "FK_ExpenseShare_Expense_ExpenseId",
                        column: x => x.ExpenseId,
                        principalSchema: "splitshare",
                        principalTable: "Expense",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExpenseShare_Members_MemberId",
                        column: x => x.MemberId,
                        principalSchema: "splitshare",
                        principalTable: "Members",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Expense_GroupId_ExpenseDate",
                schema: "splitshare",
                table: "Expense",
                columns: new[] { "GroupId", "ExpenseDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Expense_PaidByMemberId",
                schema: "splitshare",
                table: "Expense",
                column: "PaidByMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseShare_MemberId",
                schema: "splitshare",
                table: "ExpenseShare",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_Members_GroupId_Name",
                schema: "splitshare",
                table: "Members",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settlement_FromMemberId",
                schema: "splitshare",
                table: "Settlement",
                column: "FromMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_Settlement_GroupId",
                schema: "splitshare",
                table: "Settlement",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Settlement_ToMemberId",
                schema: "splitshare",
                table: "Settlement",
                column: "ToMemberId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpenseShare",
                schema: "splitshare");

            migrationBuilder.DropTable(
                name: "Settlement",
                schema: "splitshare");

            migrationBuilder.DropTable(
                name: "Expense",
                schema: "splitshare");

            migrationBuilder.DropTable(
                name: "Members",
                schema: "splitshare");

            migrationBuilder.DropTable(
                name: "Group",
                schema: "splitshare");
        }
    }
}
