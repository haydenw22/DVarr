using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase26LiveMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LiveMarksJson",
                table: "Recordings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LiveMarksJson",
                table: "Recordings");
        }
    }
}
