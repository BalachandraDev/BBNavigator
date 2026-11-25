using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowMind.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Phase16_FolderStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FolderPath",
                table: "StorageSettings",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FolderPath",
                table: "StorageSettings");
        }
    }
}
