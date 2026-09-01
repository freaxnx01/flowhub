using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowHub.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0016_TelegramUpdateFileId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileId",
                table: "TelegramUpdates",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileId",
                table: "TelegramUpdates");
        }
    }
}
