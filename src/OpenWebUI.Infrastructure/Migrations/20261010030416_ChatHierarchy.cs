using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChatHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParentChatId",
                table: "Chats",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentRunId",
                table: "ChatRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chats_ParentChatId",
                table: "Chats",
                column: "ParentChatId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatRuns_ParentRunId",
                table: "ChatRuns",
                column: "ParentRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chats_ParentChatId",
                table: "Chats");

            migrationBuilder.DropIndex(
                name: "IX_ChatRuns_ParentRunId",
                table: "ChatRuns");

            migrationBuilder.DropColumn(
                name: "ParentChatId",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "ParentRunId",
                table: "ChatRuns");
        }
    }
}
