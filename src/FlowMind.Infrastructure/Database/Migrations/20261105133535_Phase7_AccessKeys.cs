using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowMind.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Phase7_AccessKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_TenantId_ApplicationId_UpdatedUtc",
                table: "Conversations");

            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "Conversations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AccessKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyPrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_OwnerId_ApplicationId_UpdatedUtc",
                table: "Conversations",
                columns: new[] { "TenantId", "OwnerId", "ApplicationId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessKeys_KeyHash",
                table: "AccessKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessKeys_TenantId",
                table: "AccessKeys",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessKeys");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_TenantId_OwnerId_ApplicationId_UpdatedUtc",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_ApplicationId_UpdatedUtc",
                table: "Conversations",
                columns: new[] { "TenantId", "ApplicationId", "UpdatedUtc" });
        }
    }
}
