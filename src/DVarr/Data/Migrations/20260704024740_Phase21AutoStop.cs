using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase21AutoStop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutoStopMaxExtendS",
                table: "Leagues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoStopMode",
                table: "Leagues",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoStopMaxExtendS",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "AutoStopMode",
                table: "Leagues");
        }
    }
}
