using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ArenaLeaderboard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "BaseModelId",
                table: "ModelEntries",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "AccessGrantsJson",
                table: "ModelEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaJson",
                table: "ModelEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ArenaBattles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ArenaModelId = table.Column<string>(type: "TEXT", nullable: false),
                    ModelA = table.Column<string>(type: "TEXT", nullable: false),
                    ModelB = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseA = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseB = table.Column<string>(type: "TEXT", nullable: false),
                    Winner = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    VotedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArenaBattles", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArenaBattles");

            migrationBuilder.DropColumn(
                name: "AccessGrantsJson",
                table: "ModelEntries");

            migrationBuilder.DropColumn(
                name: "MetaJson",
                table: "ModelEntries");

            migrationBuilder.AlterColumn<string>(
                name: "BaseModelId",
                table: "ModelEntries",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
