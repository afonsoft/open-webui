using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChatMessageVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VersionsJson",
                table: "ChatMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VersionsJson",
                table: "ChatMessages");
        }
    }
}
