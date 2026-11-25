using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowMind.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Phase12_Access : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AudienceId",
                table: "BotLinks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AudienceId",
                table: "AccessKeys",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccessRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Everyone = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Audiences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Aliases = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audiences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessRuleAudiences",
                columns: table => new
                {
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AudienceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRuleAudiences", x => new { x.RuleId, x.AudienceId });
                    table.ForeignKey(
                        name: "FK_AccessRuleAudiences_AccessRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AccessRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessRuleAudiences_Audiences_AudienceId",
                        column: x => x.AudienceId,
                        principalTable: "Audiences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BotLinks_AudienceId",
                table: "BotLinks",
                column: "AudienceId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessKeys_AudienceId",
                table: "AccessKeys",
                column: "AudienceId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRuleAudiences_AudienceId",
                table: "AccessRuleAudiences",
                column: "AudienceId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRuleAudiences_TenantId_AudienceId",
                table: "AccessRuleAudiences",
                columns: new[] { "TenantId", "AudienceId" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRules_TenantId_TargetKind_TargetId",
                table: "AccessRules",
                columns: new[] { "TenantId", "TargetKind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Audiences_TenantId_Name",
                table: "Audiences",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AccessKeys_Audiences_AudienceId",
                table: "AccessKeys",
                column: "AudienceId",
                principalTable: "Audiences",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BotLinks_Audiences_AudienceId",
                table: "BotLinks",
                column: "AudienceId",
                principalTable: "Audiences",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccessKeys_Audiences_AudienceId",
                table: "AccessKeys");

            migrationBuilder.DropForeignKey(
                name: "FK_BotLinks_Audiences_AudienceId",
                table: "BotLinks");

            migrationBuilder.DropTable(
                name: "AccessRuleAudiences");

            migrationBuilder.DropTable(
                name: "AccessRules");

            migrationBuilder.DropTable(
                name: "Audiences");

            migrationBuilder.DropIndex(
                name: "IX_BotLinks_AudienceId",
                table: "BotLinks");

            migrationBuilder.DropIndex(
                name: "IX_AccessKeys_AudienceId",
                table: "AccessKeys");

            migrationBuilder.DropColumn(
                name: "AudienceId",
                table: "BotLinks");

            migrationBuilder.DropColumn(
                name: "AudienceId",
                table: "AccessKeys");
        }
    }
}
