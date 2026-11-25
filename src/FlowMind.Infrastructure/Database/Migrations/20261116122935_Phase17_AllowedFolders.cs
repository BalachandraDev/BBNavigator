using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowMind.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Phase17_AllowedFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AllowedFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllowedFolders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllowedFolders_Path",
                table: "AllowedFolders",
                column: "Path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AllowedFolders");
        }
    }
}
