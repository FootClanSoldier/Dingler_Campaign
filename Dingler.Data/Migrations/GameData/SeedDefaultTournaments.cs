using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Dingler.Data.Migrations.GameData
{
        public partial class SeedDefaultTournaments : Migration
    {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.InsertData(
                        table: "Tournaments",
                        columns:
                        [
                            "Id", "Description", "MatchTypeId", "NeededPlayers", "StartConditionId", "StartDate",
                            "TournamentTypeId"
                        ],
                        values: new object[,]
                        {
                            {
                                1, "1v1 Standard Match", 1, 2, 1,
                                new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), 3
                            },
                            {
                                2, "1v1 Immortal Match", 1, 2, 1,
                                new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), 4
                            }
                        });
                }

                protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Tournaments",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Tournaments",
                keyColumn: "Id",
                keyValue: 2);
        }
    }
}
