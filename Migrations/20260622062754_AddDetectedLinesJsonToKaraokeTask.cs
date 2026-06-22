using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaraokePlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddDetectedLinesJsonToKaraokeTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DetectedLinesJson",
                table: "KaraokeTasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetectedLinesJson",
                table: "KaraokeTasks");
        }
    }
}
