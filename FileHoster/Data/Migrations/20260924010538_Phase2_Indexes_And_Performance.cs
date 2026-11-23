using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileHoster.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2_Indexes_And_Performance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "IX_Files_CreatedAt",
                table: "Files",
                newName: "idx_files_createdat");

            migrationBuilder.RenameIndex(
                name: "IX_Files_AppId",
                table: "Files",
                newName: "idx_files_appid");

            migrationBuilder.CreateIndex(
                name: "idx_files_appid_createdat",
                table: "Files",
                columns: new[] { "AppId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_files_appid_filename",
                table: "Files",
                columns: new[] { "AppId", "Filename" });

            migrationBuilder.CreateIndex(
                name: "idx_applications_isactive",
                table: "Applications",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_files_appid_createdat",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "idx_files_appid_filename",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "idx_applications_isactive",
                table: "Applications");

            migrationBuilder.RenameIndex(
                name: "idx_files_createdat",
                table: "Files",
                newName: "IX_Files_CreatedAt");

            migrationBuilder.RenameIndex(
                name: "idx_files_appid",
                table: "Files",
                newName: "IX_Files_AppId");
        }
    }
}
