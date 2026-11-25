using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowMind.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase11_BotLinkScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccentColor",
                table: "BotLinks",
                type: "nvarchar(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentIdsJson",
                table: "BotLinks",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccentColor",
                table: "BotLinks");

            migrationBuilder.DropColumn(
                name: "DocumentIdsJson",
                table: "BotLinks");
        }
    }
}
