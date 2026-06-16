using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase6Feedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Programmes_ChannelId_StartUtc",
                table: "Programmes");

            migrationBuilder.RenameColumn(
                name: "ChannelId",
                table: "Programmes",
                newName: "SourceId");

            migrationBuilder.AddColumn<string>(
                name: "MatchQuery",
                table: "Recordings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EpgChannelId",
                table: "Programmes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BadgeUrl",
                table: "Leagues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PosterUrl",
                table: "Leagues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Round",
                table: "Events",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Season",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbUrl",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TsdbEventId",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Programmes_SourceId_EpgChannelId_StartUtc",
                table: "Programmes",
                columns: new[] { "SourceId", "EpgChannelId", "StartUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Programmes_SourceId_EpgChannelId_StartUtc",
                table: "Programmes");

            migrationBuilder.DropColumn(
                name: "MatchQuery",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "EpgChannelId",
                table: "Programmes");

            migrationBuilder.DropColumn(
                name: "BadgeUrl",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "PosterUrl",
                table: "Leagues");

            migrationBuilder.DropColumn(
                name: "Round",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Season",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ThumbUrl",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "TsdbEventId",
                table: "Events");

            migrationBuilder.RenameColumn(
                name: "SourceId",
                table: "Programmes",
                newName: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Programmes_ChannelId_StartUtc",
                table: "Programmes",
                columns: new[] { "ChannelId", "StartUtc" });
        }
    }
}
