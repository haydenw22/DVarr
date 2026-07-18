using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "Channels",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "Channels");
        }
    }
}
