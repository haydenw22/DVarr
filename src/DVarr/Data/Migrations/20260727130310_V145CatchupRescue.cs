using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class V145CatchupRescue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CatchupShape",
                table: "Sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StreamFormat",
                table: "Sources",
                type: "TEXT",
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<string>(
                name: "Timezone",
                table: "Sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CatchupAttempts",
                table: "RescueTickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RequireCorroborated",
                table: "RescueTickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CatchupDurationS",
                table: "Recordings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CatchupSourceStartUtc",
                table: "Recordings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GuideUncorroborated",
                table: "Recordings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CatchupShape",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "StreamFormat",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "Timezone",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "CatchupAttempts",
                table: "RescueTickets");

            migrationBuilder.DropColumn(
                name: "RequireCorroborated",
                table: "RescueTickets");

            migrationBuilder.DropColumn(
                name: "CatchupDurationS",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "CatchupSourceStartUtc",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "GuideUncorroborated",
                table: "Recordings");
        }
    }
}
