using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowMind.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Phase11_Storage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StorageKind",
                table: "DocumentVersions",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "local");

            migrationBuilder.CreateTable(
                name: "StorageSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RemoteType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Bucket = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Region = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    AccessKeyCipher = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    SecretKeyCipher = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: true),
                    AccessKeyHint = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastTestUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastTestOk = table.Column<bool>(type: "bit", nullable: true),
                    LastTestMessage = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageSettings_TenantId",
                table: "StorageSettings",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageSettings");

            migrationBuilder.DropColumn(
                name: "StorageKind",
                table: "DocumentVersions");
        }
    }
}
