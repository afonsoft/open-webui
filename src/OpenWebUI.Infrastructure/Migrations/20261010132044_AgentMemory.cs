using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentMemories",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    RepoSlug = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentMemories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemories_UserId_RepoSlug",
                table: "AgentMemories",
                columns: new[] { "UserId", "RepoSlug" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemories_UserId_Scope",
                table: "AgentMemories",
                columns: new[] { "UserId", "Scope" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentMemories");
        }
    }
}
