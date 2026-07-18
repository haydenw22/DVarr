using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRescueTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RescueTicketId",
                table: "Recordings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RescueTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordingId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventId = table.Column<int>(type: "INTEGER", nullable: false),
                    LeagueId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    MatchQuery = table.Column<string>(type: "TEXT", nullable: false),
                    EventStartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EventEndUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSweepUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    NextSweepUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ReplayRecordingId = table.Column<int>(type: "INTEGER", nullable: true),
                    WholeSource = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RescueTickets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RescueTickets_EventId",
                table: "RescueTickets",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_RescueTickets_RecordingId",
                table: "RescueTickets",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_RescueTickets_State_NextSweepUtc",
                table: "RescueTickets",
                columns: new[] { "State", "NextSweepUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RescueTickets");

            migrationBuilder.DropColumn(
                name: "RescueTicketId",
                table: "Recordings");
        }
    }
}
