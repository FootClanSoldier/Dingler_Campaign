extern alias HexGame;
using System.Text.Json;
using Dingler.Data.Repositories;
using Dingler.Game.Domain;
using Dingler.Game.Services;
using Dingler.Server;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using Microsoft.Extensions.Logging;
using Deck = Dingler.Data.Entities.GameData.Deck;

namespace Dingler.Game.DeckImport;

/// <summary>Where the importer finds the site's data and the inbox (appsettings "DeckImport").</summary>
public sealed class DeckImportOptions
{
	public string SiteDataPath { get; init; } = "";
	public string InboxPath { get; init; } = "";
}

/// <summary>
/// Imports a deck from the Hex Codex deck builder into a player's account. The client has no
/// deck import, and reads decks only from the login profile stream, so an imported deck appears at the next login.
/// Two ways in, one importer:
///   - chat: "/importdeck &lt;deck link or code&gt;" (the site's "Copy link"); equipment then set in the game's own editor;
///   - inbox: a site backup file (hex-codex-decks-*.json, carries equipment and personality) or a text file of links,
///     dropped into InboxPath/&lt;user name&gt;/, imported at that player's next login and moved to imported/ or failed/.
/// Cards use the player's collection copies (Dingler gives every ownable card x4, basic resources x300); gems are packed
/// with the game's own GemHelper. A deck never replaces an existing one: a taken name gets " (2)", " (3)", ...
/// </summary>
public sealed class DeckImportService
{
	private static readonly object Gate = new();
	private static SiteIds? _siteIds;
	private static string? _siteIdsPath;
	private static Dictionary<ResourceId, List<ulong>>? _copiesByTemplate;

	private readonly DeckRepository _decks;
	private readonly CollectionCacheService _collection;
	private readonly DeckImportOptions _options;
	private readonly ILogger<DeckImportService>? _logger;

	public DeckImportService(DeckRepository decks, CollectionCacheService collection, DeckImportOptions options,
		ILogger<DeckImportService>? logger = null)
	{
		_decks = decks;
		_collection = collection;
		_options = options;
		_logger = logger;
	}

	public sealed record Result(bool Ok, string Message);

	/// <summary>Imports one deck from a pasted link or code, for the session's player.</summary>
	public async Task<Result> ImportLinkAsync(SessionContext context, string text, IReadOnlyList<int?>? equipment = null, int personality = 0)
	{
		try
		{
			var code = DeckLinkCodec.FindCode(text) ?? throw new DeckLinkCodec.DeckLinkException("no deck link found");
			var deck = DeckLinkCodec.Decode(code);
			var bits = Build(deck, equipment, personality, context.ProfileId);
			var name = await SaveAsync(context, bits, deck.Name).ConfigureAwait(false);
			_logger?.LogInformation("Deck import: {user} imported \"{name}\" ({cards} cards, {reserves} reserves)",
				context.UserName, name, bits.CardsInDeck.Count, bits.CardsInSideboard.Count);
			return new Result(true, $"Deck \"{name}\" imported: {bits.CardsInDeck.Count} cards, {bits.CardsInSideboard.Count} in reserve. Log out and in again to see it.");
		}
		catch (Exception ex) when (ex is DeckLinkCodec.DeckLinkException or ImportException)
		{
			_logger?.LogWarning("Deck import: {user}: refused ({reason})", context.UserName, ex.Message);
			return new Result(false, "Deck not imported: " + ex.Message);
		}
	}

	/// <summary>At login, before the decks are read: imports every file in InboxPath/&lt;user name&gt;/ (the folder is
	/// created so the owner can see where to drop files). Never throws.</summary>
	public async Task ImportInboxAsync(SessionContext context)
	{
		try
		{
			if (string.IsNullOrEmpty(_options.InboxPath) || string.IsNullOrEmpty(context.UserName))
				return;
			var folder = Path.Combine(_options.InboxPath, string.Concat(context.UserName.Split(Path.GetInvalidFileNameChars())));
			Directory.CreateDirectory(folder);
			foreach (var file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
			{
				var ok = 0;
				var problems = new List<string>();
				foreach (var (link, equipment, personality) in ReadInboxFile(file, problems))
				{
					var r = await ImportLinkAsync(context, link, equipment, personality).ConfigureAwait(false);
					if (r.Ok) ok++;
					else problems.Add(r.Message);
				}
				var target = Path.Combine(folder, ok > 0 || problems.Count == 0 ? "imported" : "failed");
				Directory.CreateDirectory(target);
				File.Move(file, Path.Combine(target, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(file)}"), overwrite: true);
				_logger?.LogInformation("Deck import: {user}: {file}: {ok} deck(s) imported{problems}", context.UserName,
					Path.GetFileName(file), ok, problems.Count == 0 ? "" : "; " + string.Join("; ", problems));
			}
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Deck import: the inbox failed for {user}", context.UserName);
		}
	}

	// A site backup (format "hex-codex-decks") gives link + equipment + personality per deck; any other file is read as
	// text, one deck link or code per line.
	private static IEnumerable<(string Link, IReadOnlyList<int?>? Equipment, int Personality)> ReadInboxFile(string file, List<string> problems)
	{
		var text = File.ReadAllText(file);
		var items = new List<(string, IReadOnlyList<int?>?, int)>();
		if (text.TrimStart().StartsWith('{'))
		{
			try
			{
				using var doc = JsonDocument.Parse(text);
				var root = doc.RootElement;
				if (root.TryGetProperty("format", out var f) && f.GetString() == "hex-codex-decks" && root.TryGetProperty("decks", out var decks))
				{
					foreach (var d in decks.EnumerateArray())
					{
						if (!d.TryGetProperty("link", out var link) || link.GetString() is not { } l) continue;
						var equipment = d.TryGetProperty("equipment", out var e) && e.ValueKind == JsonValueKind.Array
							? e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : (int?)null).ToList()
							: null;
						var personality = d.TryGetProperty("personality", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
						items.Add((l, equipment, personality));
					}
					return items;
				}
				problems.Add("a JSON file that isn't a Hex Codex decks backup");
				return items;
			}
			catch (JsonException)
			{
				problems.Add("a damaged JSON file");
				return items;
			}
		}
		foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
			items.Add((line, null, 0));
		return items;
	}

	private sealed class ImportException(string message) : Exception(message);

	private DinglerDeckBits Build(DeckLinkCodec.Deck deck, IReadOnlyList<int?>? equipment, int personality, ulong profileId)
	{
		var ids = Ids();
		var champion = ids.Find(deck.Champion);
		if (deck.Champion == 0 || champion is null || champion.Kind != "champion")
			throw new ImportException(deck.Champion == 0 ? "the deck has no champion" : $"unknown champion (site id {deck.Champion})");

		var bits = new DinglerDeckBits
		{
			PVPChampionId = new ResourceId(champion.Guid),
			PVEChampionId = 0,
			Personality = (EDeckPersonality)Math.Clamp(personality, 0, 8),
			PlayerId = profileId,
			DeckSleeve = ResourceId.Invalid,
			GameBoard = ResourceId.Invalid,
			Coin = ResourceId.Invalid,
		};
		for (var slot = 0; slot < 6; slot++)
		{
			var siteId = equipment is not null && slot < equipment.Count ? equipment[slot] : null;
			var item = siteId is { } s ? ids.Find(s) : null;
			bits.Equipment.Add(item is { Kind: "item-equipment" } ? new ResourceId(item.Guid) : ResourceId.Invalid);
		}

		var copies = CopiesByTemplate();
		var used = new Dictionary<ResourceId, int>();
		var missing = new List<string>();
		void Add(List<DinglerCardBits> into, DeckLinkCodec.Entry entry)
		{
			var card = ids.Find(entry.Id);
			if (card is null || card.Kind != "card") { missing.Add($"card #{entry.Id}"); return; }
			var template = new ResourceId(card.Guid);
			if (!copies.TryGetValue(template, out var owned)) { missing.Add($"{card.Name} (not in the collection)"); return; }
			var gems = EGemTypesNew.GemFormatBit;
			foreach (var g in entry.Gems)
			{
				var gem = ids.Find(g);
				if (gem is null || ids.GemType(gem.Guid) is not { } type || !Enum.TryParse<EGemTypesNew>(type, out var value))
				{
					missing.Add($"gem #{g} on {card.Name}");
					continue;
				}
				gems = GemHelper.AddGemToGem(value, gems);
			}
			for (var i = 0; i < entry.Copies; i++)
			{
				var n = used.GetValueOrDefault(template);
				if (n >= owned.Count) { missing.Add($"{card.Name} (more than {owned.Count} copies)"); return; }
				used[template] = n + 1;
				var instance = owned[n];
				into.Add(new DinglerCardBits { Id = instance, TemplateId = template, SocketedGems = gems, IsNotTradeable = true });
				if (GemHelper.GetGemCount(gems) > 0)
					bits.ActiveGems[instance] = gems;
			}
		}
		foreach (var e in deck.Main) Add(bits.CardsInDeck, e);
		foreach (var e in deck.Reserves) Add(bits.CardsInSideboard, e);
		if (missing.Count > 0)
			throw new ImportException(string.Join(", ", missing.Distinct().Take(6)) + (missing.Count > 6 ? ", ..." : ""));
		if (bits.CardsInDeck.Count == 0)
			throw new ImportException("the deck has no cards");
		return bits;
	}

	private async Task<string> SaveAsync(SessionContext context, DinglerDeckBits bits, string? wanted)
	{
		var existing = (await _decks.GetAllDecksOwnedByPlayerIdAsync(context.ProfileId).ConfigureAwait(false))
			.Select(d => d.DeckName).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var baseName = string.IsNullOrWhiteSpace(wanted) ? "Imported deck" : wanted.Trim();
		var name = baseName;
		for (var i = 2; existing.Contains(name); i++)
			name = $"{baseName} ({i})";

		var row = await _decks.CreateDeckAsync(new Deck
		{
			ChampionGuid = bits.PVPChampionId.m_Guid,
			DeckGuid = Guid.NewGuid(),
			DeckName = name,
			PlayerProfileId = context.ProfileId,
		}).ConfigureAwait(false);
		bits.Id = (ulong)row.Id;
		bits.DeckName = name;
		row.DeckBitsJson = JsonSerializer.Serialize(bits);
		await _decks.UpdateDeckAsync(row).ConfigureAwait(false);
		context.Decks[bits.Id] = bits.ToDeckBits();   // the session knows it at once; the client sees it at the next login
		return name;
	}

	private SiteIds Ids()
	{
		lock (Gate)
		{
			if (_siteIds is null || _siteIdsPath != _options.SiteDataPath)
			{
				if (!File.Exists(Path.Combine(_options.SiteDataPath, "ids.json")))
					throw new ImportException($"the Hex Codex data folder isn't set up on the server ({_options.SiteDataPath})");
				_siteIds = new SiteIds(_options.SiteDataPath);
				_siteIdsPath = _options.SiteDataPath;
				_logger?.LogInformation("Deck import: read {ids} site ids and {gems} gems from {path}", _siteIds.Count, _siteIds.GemCount, _siteIdsPath);
			}
			return _siteIds;
		}
	}

	private Dictionary<ResourceId, List<ulong>> CopiesByTemplate()
	{
		lock (Gate)
		{
			return _copiesByTemplate ??= _collection.CollectionIds
				.GroupBy(kv => kv.Value)
				.ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).OrderBy(id => id).ToList());
		}
	}
}
