extern alias HexGame;
using System.Diagnostics;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Network;
using HexGame::Game.Shared.Resources;
using HexGame::Reckoning.Game;
using System.Text.Json;
using Dingler.Game.Campaign;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Data.Entities.GameData;
using Dingler.Game.Domain;
using HexGame::Game.Shared.Network.Profile;
using HexGame::Game.Shared.Profile;

namespace Dingler.Game.Services
{
    public sealed class CollectionCacheService : IStartupService
    {
        public byte[] Inventory { get; set; } = [];
        public byte[] Cards { get; set; } = [];
        private readonly string _path;
        private bool _isInitialized;
        public Dictionary<ulong, ResourceId> CollectionIds { get; set; } = new();
        private readonly List<card_instance_bits> _baseCardInstances = new();
        private readonly Dictionary<(int Race, string Choice), ulong> _shroomRewardIds = new();
        private readonly Dictionary<(int Race, string Encounter, string RuleId, string Template), ulong> _captureRewardIds = new();

        private readonly Dingler.Game.Arena.ArenaRunStore? _arenaRunStore;
        private readonly CampaignOptions? _campaignOptions;
        private readonly CampaignRunStore? _campaignRunStore;

        public CollectionCacheService(
            string path,
            Dingler.Game.Arena.ArenaRunStore? arenaRunStore = null,
            CampaignOptions? campaignOptions = null,
            CampaignRunStore? campaignRunStore = null)
        {
            _path = path;
            _arenaRunStore = arenaRunStore;
            _campaignOptions = campaignOptions;
            _campaignRunStore = campaignRunStore;
        }
        
        public void Initialize()
        {
            if (_isInitialized)
                return;
            
            HexGame::FolderUtils.Initialize(_path, null);
            BuiltInResources.Load();
            var manager = HexGame::Singleton<TemplateManager>.Instance;
            manager.LoadAssets();

            Inventory = InitializeInventory(manager);
            Cards = InitializeCardCollection(manager);

            _isInitialized = true;
        }

        private byte[] InitializeInventory(TemplateManager manager)
        {
            var inventory = manager.InventoryItems;

            List<inventory_bits> inventoryList = new List<inventory_bits>();

            foreach (var item in inventory.Values.Where(i => IsRelevantItem(i) && !i.m_TexturePath.Equals("")))
            {
                if (item.m_Name.Equals("Basic Battleboard"))
                    continue;

                var inventoryBits = new inventory_bits()
                {
                    Id = (ulong)inventoryList.Count + 1,
                    BoundToProfile = true,
                    ClaimDate = DateTime.MinValue,
                    ItemQuantity = 1,
                    TemplateID = item.Id
                };

                inventoryList.Add(inventoryBits);
            }

            return EncData.Encode(inventoryList);
        }

        private byte[] InitializeCardCollection(TemplateManager manager)
        {
            var ownableCards = manager.Cards.Values.Where(c => c.IsOwnable());
            var cardList = new List<card_instance_bits>();
            foreach (var card in ownableCards)
            {
                int count = card.IsBasicResource() ? 300 : 4;

                for (int i = 0; i < count; i++)
                {
                    var id = (ulong)cardList.Count + 1;
                    var cardBits = new card_instance_bits()
                    {
                        Id = id,
                        TemplateID = card.m_Id
                    };

                    CollectionIds.Add(cardBits.Id, card.m_Id);

                    cardList.Add(cardBits);
                }
            }

            _baseCardInstances.AddRange(cardList);
            // Reserve stable card instance IDs for each race and possible Shroom reward.
            // Ownership is still per-profile and per-choice.
            foreach (var race in Enumerable.Range(1, 8))
            {
                foreach (var choice in CampaignShroomRewardCatalog.ChoiceIds)
                {
                    var id = (ulong)CollectionIds.Count + 1;
                    _shroomRewardIds.Add((race, choice), id);
                    CollectionIds.Add(id, new ResourceId(choice));
                }
            }

            // Each CapturedCard grant reserves a stable slot from the reward
            // catalog. Slots 0 and 1 preserve existing Tamed reward instance
            // IDs; adding another rule never shifts previously issued IDs.
            foreach (var capture in CampaignRewardCatalog.CaptureRewards)
            {
                var templates = capture.TemplateGuids
                    .Where(guid => manager.Cards.ContainsKey(new ResourceId(guid)))
                    .OrderBy(static guid => guid, StringComparer.Ordinal)
                    .ToArray();
                if (templates.Length > 20)
                    throw new InvalidDataException($"Too many capture templates for {capture.EncounterGuid}:{capture.RuleId}");
                for (var race = 1; race <= 8; race++)
                {
                    for (var i = 0; i < templates.Length; i++)
                    {
                        var slot = capture.CollectionSlot;
                        var id = slot < 5
                            ? 1_000_000UL + (ulong)race * 100UL + (ulong)slot * 20UL + (ulong)i
                            : 10_000_000UL + (ulong)(race - 1) * 1_000_000UL
                                + (ulong)(slot - 5) * 20UL + (ulong)i;
                        var template = templates[i];
                        _captureRewardIds.Add((race, capture.EncounterGuid.ToLowerInvariant(),
                            capture.RuleId.ToLowerInvariant(), template), id);
                        CollectionIds.Add(id, new ResourceId(template));
                    }
                }
            }

            var collection = new card_collection()
            {
                Cards = cardList,
            };

            return EncData.Encode(collection);
        }

        public bool TryGetShroomRewardInstanceId(int race, string choice, out ulong instanceId) =>
            _shroomRewardIds.TryGetValue((race, choice.ToLowerInvariant()), out instanceId);

        public bool TryGetCaptureRewardInstanceId(int race, string encounter, string ruleId, string template, out ulong instanceId) =>
            _captureRewardIds.TryGetValue((race, encounter.ToLowerInvariant(), ruleId.ToLowerInvariant(), template.ToLowerInvariant()), out instanceId);

        private byte[] ProfileCardCollection(ulong profileId)
        {
            if (_campaignRunStore is null)
                return Cards;
            var earned = _campaignRunStore.GetProfileShroomRewards(profileId);
            var captures = _campaignRunStore.GetProfileCaptureRewards(profileId);
            if (earned.Count == 0 && captures.Count == 0)
                return Cards;
            var cards = new List<card_instance_bits>(_baseCardInstances);
            foreach (var (race, choice) in earned)
            {
                if (TryGetShroomRewardInstanceId(race, choice, out var id))
                    cards.Add(new card_instance_bits { Id = id, TemplateID = new ResourceId(choice) });
            }
            foreach (var (race, encounter, ruleId, template) in captures)
            {
                if (TryGetCaptureRewardInstanceId(race, encounter, ruleId, template, out var id))
                    cards.Add(new card_instance_bits { Id = id, TemplateID = new ResourceId(template) });
            }
            return EncData.Encode(new card_collection { Cards = cards });
        }

        public async Task SendProfileStreamAsync(SessionContext context, Task<List<Deck>> deckTask,
            CancellationToken token)
        {
            var accountId = context.AccountId;
            var profileId = context.ProfileId;
            
            var identity = new Network.Ident(accountId, profileId);

            var keep = new KeepInfo()
            {
                Id = new UID(UID.Type.Keep, profileId),
                Owner = new UID(UID.Type.ServiceProfile, accountId),
                Name = context.UserName
            };

            List<champion_bits> campaignChampions = new();
            var campaignRuns = new List<CampaignRunRecord>();
            var ownedChampions = _campaignRunStore?.GetCreatedChampions(profileId).ToList()
                ?? new List<CampaignChampionRun>();
            if (_campaignRunStore is not null)
            {
                foreach (var hero in ownedChampions.Where(static item => item.IsPlayerCreated))
                {
                    if (_campaignRunStore.TryGet(profileId, hero.ChampionId, out var current))
                        campaignRuns.Add(current);
                }
            }

            var reckoningBits = new reckoning_bits()
            {
                // TODO: Temporary solution for Gaal Camp. The original HEX
                // dialogue requires 100 Gold before emitting gaal_camp_accept.
                // Show the same development-only prerequisite Gold at login,
                // including before the first champion is created, until real
                // currency/debit persistence exists.
                // The original HasCurrency check uses > rather than >=, so
                // a displayed 100 Gold did not satisfy the 100 Gold requirement.
                // This is NOT an owned or spendable server-side Gold balance.
                Gold = _campaignOptions is not null ? 1000 : 0,
                Platinum = 0,
                Name = context.UserName,
                Champions = campaignChampions,
            };

            List<deck_bits> deckBitsList = new List<deck_bits>();

            try
            {
                var decks = await deckTask.ConfigureAwait(false);
                foreach (var deck in decks)
                {
                    if (deck.DeckBitsJson is null)
                        continue;

                    var dinglerBits = JsonSerializer.Deserialize<DinglerDeckBits>(deck.DeckBitsJson);

                    if (dinglerBits is null)
                        continue;

                    var deckBits = dinglerBits.ToDeckBits();
                    // The arena run store is the truth for the arena lock; the client shows it in the deck list.
                    if (_arenaRunStore is not null && _arenaRunStore.IsDeckInRun(profileId, deckBits.Id))
                    {
                        deckBits.Lock = HexGame::Game.Shared.Mechanics.EDeckLock.Arena_Lock;
                        deckBits.LockHolder = _arenaRunStore.TryGet(profileId, out var arenaRun) ? arenaRun.ArenaId : 0;
                    }
                    else if (deckBits.Lock == HexGame::Game.Shared.Mechanics.EDeckLock.Arena_Lock)
                    {
                        deckBits.Lock = HexGame::Game.Shared.Mechanics.EDeckLock.Not_Locked;
                        deckBits.LockHolder = 0;
                    }
                    context.Decks.TryAdd(deckBits.Id, deckBits);
                    deckBitsList.Add(deckBits);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

            if (_campaignRunStore is not null)
            {
                foreach (var originalRun in campaignRuns)
                {
                    var campaignRun = originalRun;
                    var lastDeckId = campaignRun.LastDeckId;
                    if (lastDeckId != 0 && !context.Decks.ContainsKey(lastDeckId))
                    {
                        // A deleted/stale deck must not be sent back as LastDeckID: the client immediately
                        // tries to resolve it when launching the campaign. Clear it and reopen the deck editor.
                        Dingler.Game.Protocol.StaticLogger.LogWarning(
                            "Campaign profile: clearing stale LastDeckID {deck} for champion {champion}; deck is not in the profile stream",
                            lastDeckId, campaignRun.ChampionId);
                        campaignRun = _campaignRunStore.SetChampionDeck(
                            profileId, campaignRun.ChampionId, campaignRun.Race, 0);
                        lastDeckId = 0;
                    }

                    var stateCampaignId = StateUInt64(campaignRun.State["CampID"]);
                    var stateChampionId = StateUInt64(campaignRun.State["ChampID"]);
                    var stateTempType = StateString(campaignRun.State["TempType"]);
                    var isCurrentCampaignConsistent =
                        campaignRun.CampaignId != 0 &&
                        campaignRun.CampaignId == stateCampaignId &&
                        campaignRun.ChampionId == stateChampionId &&
                        !string.Equals(campaignRun.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(campaignRun.CampaignType, stateTempType, StringComparison.OrdinalIgnoreCase);

                    if (!isCurrentCampaignConsistent)
                    {
                        Dingler.Game.Protocol.StaticLogger.LogWarning(
                            "Campaign profile: LastCampaignID verification mismatch for champion {champion}: recordCampaign={recordCampaign}, stateCampID={stateCampaign}, stateChampID={stateChampion}, recordType={recordType}, stateTempType={stateType}",
                            campaignRun.ChampionId, campaignRun.CampaignId, stateCampaignId, stateChampionId,
                            campaignRun.CampaignType, stateTempType);
                    }

                    var savedHero = ownedChampions.FirstOrDefault(hero => hero.ChampionId == campaignRun.ChampionId);
                    if (savedHero is null)
                        continue;
                    var champion = CampaignChampionProjection.CreateFromSaved(savedHero);
                    campaignChampions.Add(champion);

                    Dingler.Game.Protocol.StaticLogger.LogInformation(
                        "Campaign profile: user {user} champion={champion} race={race} LastCampaignID={campaign} LastDeckID={deck} talents={talents} verifiedCurrent={verified}",
                        context.UserName ?? "<unknown>", champion.Id, campaignRun.Race, champion.LastCampaignID, champion.LastDeckID,
                        campaignRun.ChampionTalents.Count, isCurrentCampaignConsistent);
                }
            }

            List<byte[]> encodedData =
            [
                EncData.Encode(identity),
                EncData.Encode(keep),
                EncData.Encode(reckoningBits),
                ProfileCardCollection(profileId),
                Inventory
            ];
            
            foreach (var deck in deckBitsList)
            {
                encodedData.Add(EncData.Encode(deck));
            }

            // Frost Ring Arena account flags (step 5): the client takes a List<FlagData> chunk as its whole flag list
            // (PlayerProfile.HandleProfileStream). Sent only when the player has some, so other profiles are unchanged.
            if (_arenaRunStore?.GetFlags(profileId) is { Count: > 0 } arenaFlags)
            {
                try
                {
                    encodedData.Add(EncData.Encode(Dingler.Game.Arena.ArenaAccount.ToFlagData(arenaFlags)));
                }
                catch (Exception ex)
                {
                    Dingler.Game.Protocol.StaticLogger.LogError("Arena: encoding the account flags failed ({error}); login goes on without them", ex.Message);
                }
            }

            List<Task> tasks = new();
            for (int i = 0; i < encodedData.Count; i++)
            {
                var streamEventArgs = new ProfileStreamEventArgs()
                {
                    done = i == encodedData.Count - 1,
                    Data = encodedData[i]
                };

                await context.SendMessageToClientAsync(streamEventArgs, token);
            }
        }

        private static ulong StateUInt64(System.Text.Json.Nodes.JsonNode? node) =>
            node is not null && ulong.TryParse(node.ToString(), out var value) ? value : 0;

        private static string StateString(System.Text.Json.Nodes.JsonNode? node) =>
            node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : node?.ToString() ?? string.Empty;

        private static bool IsRelevantItem(InventoryItemData item)
        {
            return item.Type is EInventoryItemType.Coin or EInventoryItemType.Gameboard or EInventoryItemType.DeckSleeve or EInventoryItemType.Equipment;
        }
    }
}
