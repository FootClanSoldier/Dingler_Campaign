using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Dingler.Data.Migrations.GameData
{
    /// <inheritdoc />
    public partial class AddDraftTournament : Migration
    {
        /// <inheritdoc />
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

            migrationBuilder.InsertData(
                table: "Tournaments",
                columns: new[] { "Id", "Description", "MatchTypeId", "NeededPlayers", "StartConditionId", "StartDate", "TournamentTypeId" },
                values: new object[] { 3, "8-Player Booster Draft", 2, 8, 1, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 });

            migrationBuilder.InsertData(
                table: "DraftSets",
                columns: new[] { "Id", "CardSetId", "Order", "TournamentId" },
                values: new object[,]
                {
                    { 1, 1, 1, 3 },
                    { 2, 2, 2, 3 },
                    { 3, 2, 3, 3 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_DraftCollections_TournamentId_PlayerProfileId",
                table: "DraftCollections",
                columns: new[] { "TournamentId", "PlayerProfileId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DraftCollections");

            migrationBuilder.DeleteData(
                table: "DraftSets",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "DraftSets",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "DraftSets",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "Tournaments",
                keyColumn: "Id",
                keyValue: 3);
        }
    }
}
