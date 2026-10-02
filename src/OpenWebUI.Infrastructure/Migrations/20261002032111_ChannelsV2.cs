using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChannelsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "ChannelMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ParentId",
                table: "ChannelMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastReadAt",
                table: "ChannelMembers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ChannelMessageReactions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Emoji = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelMessageReactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelMessageReactions_ChannelMessages_ChannelMessageId",
                        column: x => x.ChannelMessageId,
                        principalTable: "ChannelMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMessageReactions_ChannelMessageId",
                table: "ChannelMessageReactions",
                column: "ChannelMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelMessageReactions");

            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "ChannelMessages");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "ChannelMessages");

            migrationBuilder.DropColumn(
                name: "LastReadAt",
                table: "ChannelMembers");
        }
    }
}
