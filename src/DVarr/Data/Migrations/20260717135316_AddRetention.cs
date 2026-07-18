using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Protected",
                table: "LibraryItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "WatchedUtc",
                table: "LibraryItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionGbCap",
                table: "Leagues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionKeepDays",
                table: "Leagues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionKeepLast",
                table: "Leagues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetentionMode",
                table: "Leagues",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Protected",
                table: "LibraryItems");

            migrationBuilder.DropColumn(
                name: "WatchedUtc",
                table: "LibraryItems");

            migrationBuilder.DropColumn(
                name: "RetentionGbCap",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "RetentionKeepDays",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "RetentionKeepLast",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "RetentionMode",
                table: "Leagues");
        }
    }
}
