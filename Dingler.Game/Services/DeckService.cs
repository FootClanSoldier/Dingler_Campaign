extern alias HexGame;

using HexGame::Game.Client.Network.Profile;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Network.Profile;
using System.Text.Json;
using Dingler.Server;
using Dingler.Data.Repositories;
using HexGame::Reckoning.Game;
using Dingler.Game.Domain;
using Dingler.Game.Campaign;
using Deck = Dingler.Data.Entities.GameData.Deck;

namespace Dingler.Game.Services
{
                    public sealed class DeckService
    {
        private readonly DeckRepository _deckRepository;
        private readonly CollectionCacheService _collectionService;
        private readonly Dingler.Game.Arena.ArenaRunStore _arenaRunStore;
        private readonly CampaignRunStore _campaignRunStore;

        // Contains "locked": the client then shows its own "deck is locked" message.
        private const string ArenaLockedMessage = "This deck is locked to the Frost Ring Arena until the run ends.";

        public DeckService(DeckRepository deckRepository, CollectionCacheService collectionService,
            Dingler.Game.Arena.ArenaRunStore arenaRunStore, CampaignRunStore campaignRunStore)
        {
            _deckRepository = deckRepository;
            _collectionService = collectionService;
            _arenaRunStore = arenaRunStore;
            _campaignRunStore = campaignRunStore;
        }

        /// <summary>
        /// Materialize the native race starter deck from HEX's authored DeckTemplate,
        /// using owned collection instance IDs already exposed in the profile stream.
        /// The saved database deck and returned AddChampion deck use the same ID.
        /// </summary>
        public async Task<deck_bits> CreateChampionStarterDeckAsync(
            SessionContext context, ulong championId, string championName, ERace race)
        {
            var template = HexGame::Singleton<TemplateManager>.Instance.GetPvEStarterDeckForRace(race)
                ?? throw new InvalidOperationException($"No native starter deck found for race {race}");

            var deck = new deck_bits
            {
                PVEChampionId = championId,
                PVPChampionId = ResourceId.Invalid,
                player_id = context.ProfileId,
                DeckName = $"{championName}({championId})",
                equipment_1 = ResourceId.Invalid,
                equipment_2 = ResourceId.Invalid,
                equipment_3 = ResourceId.Invalid,
                equipment_4 = ResourceId.Invalid,
                equipment_5 = ResourceId.Invalid,
                equipment_6 = ResourceId.Invalid,
                talent_1 = ResourceId.Invalid,
                talent_2 = ResourceId.Invalid,
                talent_3 = ResourceId.Invalid,
                talent_4 = ResourceId.Invalid,
                talent_5 = ResourceId.Invalid,
                Coin = ResourceId.Invalid,
                deck_sleeve = ResourceId.Invalid,
                gameboard = ResourceId.Invalid,
            };

            var byTemplate = _collectionService.CollectionIds
                .GroupBy(static pair => pair.Value)
                .ToDictionary(static group => group.Key, static group => group.Select(static pair => pair.Key).OrderBy(static id => id).ToArray());
            var consumedByTemplate = new Dictionary<ResourceId, int>();
            foreach (var stack in template.m_DeckResources)
            {
                consumedByTemplate.TryGetValue(stack.m_idTemplate, out var used);
                if (!byTemplate.TryGetValue(stack.m_idTemplate, out var instances) ||
                    instances.Length < used + stack.m_Count)
                    throw new InvalidOperationException($"Starter deck card {stack.m_idTemplate} is unavailable in the profile collection");
                for (var index = 0; index < stack.m_Count; index++)
                {
                    var id = instances[used + index];
                    var gems = EGemTypesNew.Unknown;
                    if (stack.m_GemTypesNewList is not null && stack.m_GemTypesNewList.Count > index)
                    {
                        foreach (var gem in stack.m_GemTypesNewList[index])
                            gems = GemHelper.AddGemToGem(gem, gems);
                        deck.ActiveGems[id] = gems;
                    }
                    deck.CardsInDeck.Add(new card_instance_bits
                    {
                        Id = id, TemplateID = stack.m_idTemplate, SocketedGems = gems,
                    });
                }
                consumedByTemplate[stack.m_idTemplate] = used + stack.m_Count;
            }

            Deck? saved = null;
            var cachedByThisCall = false;
            try
            {
                saved = await _deckRepository.CreateDeckAsync(new Deck
                {
                    DeckName = deck.DeckName,
                    DeckGuid = Guid.NewGuid(),
                    ChampionGuid = ResourceId.Invalid.m_Guid,
                    PlayerProfileId = context.ProfileId,
                }).ConfigureAwait(false);
                deck.Id = (ulong)saved.Id;
                saved.DeckBitsJson = JsonSerializer.Serialize(new DinglerDeckBits(deck));
                await _deckRepository.UpdateDeckAsync(saved).ConfigureAwait(false);
                cachedByThisCall = context.Decks.TryAdd(deck.Id, deck);
                if (!cachedByThisCall)
                    throw new InvalidOperationException("New starter deck ID already exists in session");
                return deck;
            }
            catch
            {
                if (saved is not null)
                {
                    if (cachedByThisCall)
                        context.Decks.Remove((ulong)saved.Id, out _);
                    await _deckRepository.RemoveDeckAsync(saved.Id).ConfigureAwait(false);
                }
                throw;
            }
        }

        public async Task DiscardChampionStarterDeckAsync(SessionContext context, ulong deckId)
        {
            if (context.Decks.TryGetValue(deckId, out var deck) && deck.PVEChampionId != 0)
            {
                context.Decks.Remove(deckId, out _);
                await _deckRepository.RemoveDeckAsync(checked((int)deckId)).ConfigureAwait(false);
            }
        }

        public async Task<List<Deck>> GetPlayerDecksAsync(ulong playerProfileId)
        {
            var decks = await _deckRepository.GetAllDecksOwnedByPlayerIdAsync(playerProfileId).ConfigureAwait(false);

            return decks
                .OrderBy(d => d.Id)
                .ToList();
        }

        public async Task<AddNewDeckResponse> AddNewDeck(SessionContext context, AddNewDeckRequestArgs args)
        {
            var playerId = context.ProfileId;
            if (!TryResolvePveChampionId(context.ProfileId, args.PvEChampionId.GetInstanceId(),
                    out var championId))
            {
                Dingler.Game.Protocol.StaticLogger.LogWarning(
                    "Campaign: rejected PvE deck create for profile {profile} and champion UID instance {champion}",
                    context.ProfileId, args.PvEChampionId.GetInstanceId());
                return new AddNewDeckResponse { Error = EAddNewDeckError.InternalServerError,
                    ErrorMessage = "Campaign champion is not owned by this profile" };
            }

            // Saving a new deck under an existing name replaces (deletes) the old one: never the arena's deck.
            if (context.Decks.Values.Any(d => string.Equals(d.DeckName, args.DeckName, StringComparison.OrdinalIgnoreCase) &&
                                              _arenaRunStore.IsDeckInRun(playerId, d.Id)))
            {
                return new AddNewDeckResponse() { Error = EAddNewDeckError.InternalServerError, ErrorMessage = ArenaLockedMessage };
            }

            var removeTask = _deckRepository.RemoveDeckWithNameOwnedByPlayer(args.DeckName, playerId);

            var newDeck = new Deck()
            {
                ChampionGuid = args.PvPChampionId.m_Guid,
                DeckGuid = Guid.NewGuid(),
                DeckName = args.DeckName,
                PlayerProfileId = playerId,
            };

            var removedDeckId = await removeTask;

            if (removedDeckId != 0)
                context.Decks.Remove(removedDeckId, out _);

            var addedDeck = await _deckRepository.CreateDeckAsync(newDeck).ConfigureAwait(false);

            var deckbitsString = CreateDeckBitsStringForNewDeck(addedDeck.Id, args, championId);

            addedDeck.DeckBitsJson = deckbitsString;

            await _deckRepository.UpdateDeckAsync(addedDeck);

            var dinglerDeckBits = JsonSerializer.Deserialize<DinglerDeckBits>(addedDeck.DeckBitsJson);

            var deckBits = dinglerDeckBits?.ToDeckBits();

            if (deckBits is null || !context.Decks.TryAdd(deckBits.Id, deckBits))
                throw new Exception($"Could not add deck to user's cached decks");

            var response = new AddNewDeckResponse
            {
                Deckbits = deckBits,
                DeckID = new UID(UID.Type.Deck, (ulong)addedDeck.Id),
                DeckName = addedDeck.DeckName,
                Error = EAddNewDeckError.Ok,
            };

            return response;
        }

        public GetDeckInfoResponse GetDeckInfo(SessionContext userProfile, GetDeckInfoRequestArgs args)
        {
            if (!userProfile.Decks.TryGetValue(args.DeckID.GetInstanceId(), out var deck))
            {
                throw new InvalidOperationException($"No such deck with id {args.DeckID.GetInstanceId()} available.");
            }

            var deckInfoResponse = new GetDeckInfoResponse()
            {
                CoinId = deck.Coin,
                DeckID = args.DeckID,
                DeckName = deck.DeckName,
                DeckSleeveId = deck.deck_sleeve,
                GameboardId = deck.gameboard,
                Persona = deck.Personality,
                PvEChampionId = new UID(UID.Type.Champion, deck.PVEChampionId),
                PvPChampionId = deck.PVPChampionId,
                ActiveGems = deck.ActiveGems.ToDictionary(),
                EquipmentIDs = new List<ResourceId>
                {
                    deck.equipment_1,
                    deck.equipment_2,
                    deck.equipment_3,
                    deck.equipment_4,
                    deck.equipment_5,
                    deck.equipment_6,
                },
                DeckCardIDs = [.. deck.CardsInDeck.Select(c => c.CardId.InstanceId)],
                SideboardCardIDs = [.. deck.CardsInSideboard.Select(c => c.CardId.InstanceId)],
                TalentIDs = new List<ResourceId>
                {
                    deck.talent_1,
                    deck.talent_2,
                    deck.talent_3,
                    deck.talent_4,
                    deck.talent_5
                }
            };

            return deckInfoResponse;
        }

        public async Task<UpdateDeckResponse> UpdateDeckAsync(SessionContext context, UpdateDeckRequestArgs args)
        {
            var deckId = args.DeckID.GetInstanceId();
            if (!TryResolvePveChampionId(context.ProfileId, args.PvEChampionId.GetInstanceId(),
                    out var championId))
            {
                Dingler.Game.Protocol.StaticLogger.LogWarning(
                    "Campaign: rejected PvE deck update for profile {profile} and champion UID instance {champion}",
                    context.ProfileId, args.PvEChampionId.GetInstanceId());
                return new UpdateDeckResponse { DeckID = args.DeckID,
                    Error = EUpdateDeckError.InternalServerError,
                    ErrorMessage = "Campaign champion is not owned by this profile" };
            }

            if (_arenaRunStore.IsDeckInRun(context.ProfileId, deckId))
            {
                return new UpdateDeckResponse()
                {
                    DeckID = args.DeckID, Error = EUpdateDeckError.InternalServerError, ErrorMessage = ArenaLockedMessage, updated = false,
                };
            }

            var isUpdate = context.Decks.TryGetValue(deckId, out var deckBits);

            if (!isUpdate)
            {
                deckBits = new deck_bits();
            }

            var deck = await _deckRepository.GetDeckById((int)deckId).ConfigureAwait(false);

            if (deck is not null && deck.PlayerProfileId != context.ProfileId)
                throw new InvalidOperationException("Deck belongs to another profile");

            if (deck is null)
            {
                if (isUpdate)
                {
                    throw new InvalidOperationException($"Deck id {deckId} does not exist in database but was found in session for player {context.ProfileId}");
                }

                deck = new Deck();
                deck.DeckName = args.DeckName;
                deck.DeckGuid = Guid.NewGuid();
                deck.PlayerProfileId = context.ProfileId;
                deck.ChampionGuid = ResourceId.Invalid.m_Guid;

                await _deckRepository.CreateDeckAsync(deck).ConfigureAwait(false);
                deckBits!.Id = (ulong)deck.Id;
                context.Decks[deckBits.Id] = deckBits;
            }

            deckBits!.ActiveGems = args.ActiveGems;
            deckBits.deck_sleeve = args.DeckSleeveId;
            deckBits.DeckName = args.DeckName;
            deckBits.gameboard = args.GameboardId;
            deckBits.Coin = args.CoinId;
            deckBits.equipment_1 = args.EquipmentIDs.Count >= 1 ? args.EquipmentIDs[0] : ResourceId.Invalid;
            deckBits.equipment_2 = args.EquipmentIDs.Count >= 2 ? args.EquipmentIDs[1] : ResourceId.Invalid;
            deckBits.equipment_3 = args.EquipmentIDs.Count >= 3 ? args.EquipmentIDs[2] : ResourceId.Invalid;
            deckBits.equipment_4 = args.EquipmentIDs.Count >= 4 ? args.EquipmentIDs[3] : ResourceId.Invalid;
            deckBits.equipment_5 = args.EquipmentIDs.Count >= 5 ? args.EquipmentIDs[4] : ResourceId.Invalid;
            deckBits.equipment_6 = args.EquipmentIDs.Count >= 6 ? args.EquipmentIDs[5] : ResourceId.Invalid;
            deckBits.talent_1 = args.TalentIDs.Count >= 1 ? args.TalentIDs[0] : ResourceId.Invalid;
            deckBits.talent_2 = args.TalentIDs.Count >= 2 ? args.TalentIDs[1] : ResourceId.Invalid;
            deckBits.talent_3 = args.TalentIDs.Count >= 3 ? args.TalentIDs[2] : ResourceId.Invalid;
            deckBits.talent_4 = args.TalentIDs.Count >= 4 ? args.TalentIDs[3] : ResourceId.Invalid;
            deckBits.talent_5 = args.TalentIDs.Count >= 5 ? args.TalentIDs[4] : ResourceId.Invalid;
            deckBits.Personality = args.Persona;
            deckBits.PVEChampionId = championId;
            deckBits.PVPChampionId = args.PvPChampionId;

            deckBits.CardsInDeck.Clear();
            foreach (var card in args.DeckCardIDs)
            {
                if (!_collectionService.CollectionIds.TryGetValue(card, out var templateId))
                    continue;

                if (!deckBits.ActiveGems.TryGetValue(card, out var activeGems))
                {
                    activeGems = EGemTypesNew.GemFormatBit;
                }

                deckBits.CardsInDeck.Add(new card_instance_bits()
                {
                    Id = card,
                    TemplateID = templateId,
                    CardStats = null,
                    IsFoil = false,
                    IsExtended = false,
                    SocketedGems = activeGems,
                    IsNotTradeable = true,
                    EscrowStatus = null
                });
            }

            deckBits.CardsInSideboard.Clear();
            foreach (var card in args.SideboardCardIDs)
            {
                if (!_collectionService.CollectionIds.TryGetValue(card, out var templateId))
                    continue;

                if (!deckBits.ActiveGems.TryGetValue(card, out var activeGems))
                {
                    activeGems = EGemTypesNew.GemFormatBit;
                }

                deckBits.CardsInSideboard.Add(new card_instance_bits()
                {
                    Id = card,
                    TemplateID = templateId,
                    CardStats = null,
                    IsFoil = false,
                    IsExtended = false,
                    SocketedGems = activeGems,
                    IsNotTradeable = true,
                    EscrowStatus = null
                });
            }

            var dinglerBits = new DinglerDeckBits(deckBits);

            var deckJson = JsonSerializer.Serialize(dinglerBits);

            deck.DeckBitsJson = deckJson;
            deck.DeckName = args.DeckName;
            deck.ChampionGuid = args.PvPChampionId.m_Guid;

            await _deckRepository.UpdateDeckAsync(deck);

            return new UpdateDeckResponse()
            {
                DeckID = new UID(UID.Type.Deck, (ulong)deck.Id),
                updated = isUpdate,
                Error = EUpdateDeckError.Ok,
            };
        }

        public async Task<RemoveDeckResponse> RemoveDeckAsync(SessionContext userProfile, RemoveDeckRequestArgs args)
        {
            var deckId = args.DeckID.GetInstanceId();

            if (_arenaRunStore.IsDeckInRun(userProfile.ProfileId, deckId))
            {
                return new RemoveDeckResponse()
                {
                    DeckID = args.DeckID, Error = ERemoveDeckError.InternalServerError, ErrorMessage = ArenaLockedMessage, succeded = false,
                };
            }

            var owned = userProfile.Decks.ContainsKey(deckId)
                && (await _deckRepository.GetDeckById(checked((int)deckId)).ConfigureAwait(false))?.PlayerProfileId == userProfile.ProfileId;
            if (!owned)
                throw new InvalidOperationException("Deck does not belong to this profile");
            var removeTask = _deckRepository.RemoveDeckAsync((int)deckId);

            userProfile.Decks.Remove(deckId, out _);

            await removeTask;

            return new RemoveDeckResponse()
            {
                DeckID = new UID(UID.Type.Deck, 0),
                Error = ERemoveDeckError.Ok,
                succeded = true,
            };
        }

        private bool TryResolvePveChampionId(ulong profileId, ulong candidate, out ulong championId)
        {
            if (candidate == 0)
            {
                championId = 0; // Non-campaign decks have no PvE champion.
                return true;
            }

            var owned = _campaignRunStore.TryGetOwnedChampion(
                profileId, candidate, out _);
            championId = owned ? candidate : 0;
            return owned;
        }

        private string CreateDeckBitsStringForNewDeck(int id, AddNewDeckRequestArgs args, ulong championId)
        {
            var deckBits = new DinglerDeckBits()
            {
                Id = (ulong)id,
                PVEChampionId = championId,
                PVPChampionId = args.PvPChampionId,
                ActiveGems = args.ActiveGems,
                Coin = args.CoinId,
                DeckName = args.DeckName,
                DeckSleeve = args.DeckSleeveId,
                Personality = args.Persona,
                PlayerId = args.RecID.GetInstanceId(),
                Equipment = args.EquipmentIDs,
                Talents = args.TalentIDs
            };

            foreach (var cardId in args.DeckCardIDs)
            {
                if (!_collectionService.CollectionIds.TryGetValue(cardId, out var collectionId))
                    continue;

                deckBits.CardsInDeck.Add(new DinglerCardBits()
                {
                    Id = cardId,
                    TemplateId = collectionId
                });
            }

            foreach (var cardId in args.SideboardCardIDs)
            {
                if (!_collectionService.CollectionIds.TryGetValue(cardId, out var collectionId))
                    continue;

                deckBits.CardsInSideboard.Add(new DinglerCardBits()
                {
                    Id = cardId,
                    TemplateId = collectionId,
                });
            }

            var deckbitsJson = JsonSerializer.Serialize(deckBits);

            return deckbitsJson;
        }
    }
}
