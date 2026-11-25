using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowMind.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Phase15_LinkUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BotLinkId",
                table: "AiUsageLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLogs_BotLinkId_TimestampUtc",
                table: "AiUsageLogs",
                columns: new[] { "BotLinkId", "TimestampUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiUsageLogs_BotLinkId_TimestampUtc",
                table: "AiUsageLogs");

            migrationBuilder.DropColumn(
                name: "BotLinkId",
                table: "AiUsageLogs");
        }
    }
}
