using System;
using FileHoster.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FileHoster.Data.Migrations;

[DbContext(typeof(FileHosterContext))]
[Migration("20260924190000_RequestNoncesAndHashes")]
public class RequestNoncesAndHashes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "MetadataJsonHash", table: "Files", type: "text", nullable: true);
        migrationBuilder.Sql(
            "UPDATE \"Files\" SET \"MetadataJsonHash\" = \"ContentHash\", \"ContentHash\" = NULL");
        migrationBuilder.CreateTable(
            name: "RequestNonces",
            columns: table => new
            {
                AppId = table.Column<string>(type: "text", nullable: false),
                Nonce = table.Column<string>(type: "text", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_RequestNonces", x => new { x.AppId, x.Nonce }));
        migrationBuilder.CreateIndex(
            name: "IX_RequestNonces_ExpiresAt", table: "RequestNonces", column: "ExpiresAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RequestNonces");
        migrationBuilder.DropColumn(name: "MetadataJsonHash", table: "Files");
    }
}
