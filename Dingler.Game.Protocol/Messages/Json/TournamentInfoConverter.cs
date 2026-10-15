extern alias HexGame;
using System.Text.Json;
using System.Text.Json.Serialization;
using HexGame::Game.Shared.Tournaments;

namespace Dingler.Game.Protocol.Messages.Json;

public class TournamentInfoConverter : JsonConverter<TournamentInfo>
{
	public override TournamentInfo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		throw new NotImplementedException();
	}

	public override void Write(Utf8JsonWriter writer, TournamentInfo value, JsonSerializerOptions options)
	{
		writer.WriteStartObject();
		writer.WritePropertyName($"tourn:tournament-{value.TournamentID}");
		writer.WriteStartObject();
		writer.WriteString("id", $"t{value.TournamentID}");
		writer.WriteNumber("completionType", (long)value.CompletionType);
		writer.WritePropertyName("players");
		writer.WriteStartObject();
		for (int i = 0; i < value.Players.Count; i++)
		{
			var player = value.Players[i];
			player.PlayerID = (ulong)(i + 1);
			writer.WritePropertyName((i + 1).ToString());
			JsonSerializer.Serialize(writer, player, options);
		}
		writer.WriteEndObject();
		
		writer.WritePropertyName("matches");
		writer.WriteStartObject();
		for (int i = 0; i < value.Games.Count; i++)
		{
			var match = value.Games[i];
			writer.WritePropertyName(i.ToString());
			JsonSerializer.Serialize(writer, match, options);
		}
		writer.WriteEndObject();
		writer.WriteNumber("nextRoundTime", value.NextRoundTime.Ticks);
		writer.WriteString("state", value.State.FullPath);
		writer.WriteString("name", value.Description);
		writer.WriteNumber("numberOfRounds", value.NumberOfRounds);
		if (value.tournamentDescription is not null)
		{
			writer.WritePropertyName("description");
			JsonSerializer.Serialize(writer, TournamentDescRoomShape.FromTournamentDesc(value.tournamentDescription),
				options);
		}
		writer.WriteNumber("format", (long)value.Format);
		writer.WriteNumber("style", (long)value.Style);

		if (value.linkedTournament is not null)
		{
			writer.WritePropertyName("linkedTournament");
			JsonSerializer.Serialize(writer, value.linkedTournament, options);
		}
		writer.WriteEndObject();
		writer.WriteEndObject();	
	}
}