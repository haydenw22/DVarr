using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2EventsLeagues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventProvider",
                table: "Leagues",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExternalLeagueId",
                table: "Leagues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IcsUrl",
                table: "Leagues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastEventSyncUtc",
                table: "Leagues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleHorizonDays",
                table: "Leagues",
                type: "INTEGER",
                nullable: false,
                defaultValue: 14);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventProvider",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "ExternalLeagueId",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "IcsUrl",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "LastEventSyncUtc",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "ScheduleHorizonDays",
                table: "Leagues");
        }
    }
}
