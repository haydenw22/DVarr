using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase25ChannelPrefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Favorite",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Hidden",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ChannelGroupPrefs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupName = table.Column<string>(type: "TEXT", nullable: false),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    Favorite = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelGroupPrefs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelGroupPrefs_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelGroupPrefs_SourceId_GroupName",
                table: "ChannelGroupPrefs",
                columns: new[] { "SourceId", "GroupName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelGroupPrefs");

            migrationBuilder.DropColumn(
                name: "Favorite",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "Hidden",
                table: "Channels");
        }
    }
}
