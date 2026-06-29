using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase19SessionsAndDurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MonitoredSessionsJson",
                table: "Leagues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionDurationsJson",
                table: "Leagues",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonitoredSessionsJson",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "SessionDurationsJson",
                table: "Leagues");
        }
    }
}
