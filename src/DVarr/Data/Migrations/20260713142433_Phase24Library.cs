using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase24Library : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LibraryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordingId = table.Column<int>(type: "INTEGER", nullable: true),
                    EventId = table.Column<int>(type: "INTEGER", nullable: true),
                    LeagueId = table.Column<int>(type: "INTEGER", nullable: true),
                    ShowName = table.Column<string>(type: "TEXT", nullable: false),
                    Sport = table.Column<string>(type: "TEXT", nullable: true),
                    SeasonYear = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNum = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    FileBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationS = table.Column<int>(type: "INTEGER", nullable: true),
                    VideoCodec = table.Column<string>(type: "TEXT", nullable: true),
                    AudioCodec = table.Column<string>(type: "TEXT", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    ChannelName = table.Column<string>(type: "TEXT", nullable: true),
                    SourceLabel = table.Column<string>(type: "TEXT", nullable: true),
                    Origin = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    MissingSinceUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Unsorted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryItems_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LibraryItems_Leagues_LeagueId",
                        column: x => x.LeagueId,
                        principalTable: "Leagues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LibraryItems_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryItems_EventId",
                table: "LibraryItems",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryItems_FilePath",
                table: "LibraryItems",
                column: "FilePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryItems_LeagueId",
                table: "LibraryItems",
                column: "LeagueId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryItems_RecordingId",
                table: "LibraryItems",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryItems_ShowName_SeasonYear",
                table: "LibraryItems",
                columns: new[] { "ShowName", "SeasonYear" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryItems_Status",
                table: "LibraryItems",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryItems");
        }
    }
}
