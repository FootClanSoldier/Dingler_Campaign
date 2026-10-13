using Dingler.Data.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dingler.Data.Migrations.GameData
{
    [DbContext(typeof(GameDataContext))]
    [Migration("20261012000000_AddDraftCollectionsAndSeedDraftTournament")]
    public partial class AddDraftCollectionsAndSeedDraftTournament : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DraftCollections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TournamentId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerProfileId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CardsJson = table.Column<string>(type: "TEXT", nullable: true),
                    DeckJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DraftCollections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DraftCollections_TournamentId_PlayerProfileId",
                table: "DraftCollections",
                columns: new[] { "TournamentId", "PlayerProfileId" },
                unique: true);

            migrationBuilder.Sql(
                "INSERT INTO \"Tournaments\" (\"Description\", \"MatchTypeId\", \"NeededPlayers\", \"StartConditionId\", \"StartDate\", \"TournamentTypeId\") " +
                "VALUES ('8-Player Booster Draft', 2, 8, 1, '0001-01-01 00:00:00', 1);");

            migrationBuilder.Sql(
                "INSERT INTO \"DraftSets\" (\"TournamentId\", \"CardSetId\", \"Order\") " +
                "SELECT \"Id\", 1, 1 FROM \"Tournaments\" WHERE \"Description\" = '8-Player Booster Draft';");

            migrationBuilder.Sql(
                "INSERT INTO \"DraftSets\" (\"TournamentId\", \"CardSetId\", \"Order\") " +
                "SELECT \"Id\", 2, 2 FROM \"Tournaments\" WHERE \"Description\" = '8-Player Booster Draft';");

            migrationBuilder.Sql(
                "INSERT INTO \"DraftSets\" (\"TournamentId\", \"CardSetId\", \"Order\") " +
                "SELECT \"Id\", 2, 3 FROM \"Tournaments\" WHERE \"Description\" = '8-Player Booster Draft';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM \"DraftSets\" WHERE \"TournamentId\" IN (SELECT \"Id\" FROM \"Tournaments\" WHERE \"Description\" = '8-Player Booster Draft');");
            migrationBuilder.Sql(
                "DELETE FROM \"Tournaments\" WHERE \"Description\" = '8-Player Booster Draft';");

            migrationBuilder.DropTable(
                name: "DraftCollections");
        }
    }
}
