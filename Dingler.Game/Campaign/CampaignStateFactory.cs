using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dingler.Game.Campaign;

public static class CampaignStateFactory
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    private static readonly IReadOnlyDictionary<int, CampaignRaceConfig> Races =
        new Dictionary<int, CampaignRaceConfig>
        {
            [1] = new(1, "Human", "adventurezone01/p_hmm_wrenscastle", "p_hmm_wrenscastle",
                "CaptainCedric", "GarethKay", "ColonelSterling", "TrainingWithGareth",
                "a31d27d0-0c2b-4b8b-90ec-25abae6c5987", "d41a4a46-fd3d-4c98-bafb-a87c30aad05c",
                "bcadcd54-f196-443a-aeea-e77e6aba87b3", "3b42dc53-51cf-411d-b43c-a1841f69272b",
                "abdcca36-20fa-4af7-8c24-8b5e0fcb61d3", "c92ff657-ea66-4fd5-9b90-1f8161ffd964",
                "5227b1b0-193a-45e8-a1a3-8215f20ac95b", "a633844d-bb26-4776-9351-aea16b4c71ba",
                "CastleExterior", "Comfortable"),
            [2] = new(2, "Elf", "adventurezone01/p_elf_satyrsroost", "p_elf_satyrsroost",
                "Emilia", "Nerissa", "Balthasar", "TrainingWithNerissa",
                "e33c74c8-8b90-4549-a8f2-25c06c35a7a4", "ed3b4737-b824-48e6-8a53-cc6269f46b37",
                "557dcfdc-f26c-4b36-94b9-182d1f8c6be3", "7756b814-2c9b-49f1-bb9a-651d2a4f0e43",
                "18b84529-d187-46ad-bb98-9a591d5287ea", "366a9029-5545-4967-95d4-b10f54279b9c",
                "e5cd349e-a77d-4175-8cfc-3565395c2eb4", "11b18575-fab3-49d1-a1ab-2ef6447c99d1",
                "Forest", "Defensive"),
            [3] = new(3, "Coyotle", "adventurezone01/p_cytl_thunderfield", "p_cytl_thunderfield",
                "ShortBuffalo", "WhisperingBreeze", "DuskDaughter", "TrainingWithWhisperingBreeze",
                "04829c2c-b544-4232-bd47-cbd6cb860d91", "081487f9-c107-4c2f-a085-643aa3f19018",
                "ee681f28-72a9-4e0b-bb25-fb8f45dd3da3", "1d2dac36-6e91-4ed1-b4cc-a0eaf66d55b1",
                "0934ceae-03be-436e-b8e5-4f8922e02a0b", "c5f75a23-4044-47d7-b80f-0615cfd2546c",
                "064d32cd-9171-4052-82a2-869eb0017b2a", "b41ca664-789c-4958-8386-8e7b339825c8",
                "CanyonDesert", "Aggressive"),
            [4] = new(4, "Orc", "adventurezone01/p_orc_xamahuac", "p_orc_xamahuac",
                "Xolotl", "Moqui", "Montecuma", "TrainingWithMoqui",
                "f048e5f8-399c-4fa8-88c2-ceeddd6e6d7e", "49272f71-1cff-451e-a311-5a6c31c57bf0",
                "ac38b34a-4f16-44fb-ac6f-6950bb20f33b", "875ae8cd-7ed2-4f8a-ad8a-472df8e6f824",
                "578dd9ea-c5ab-49a0-93c5-44ad235cf0a9", "0445cf14-1725-4488-ad78-79642b90e519",
                "84cdd061-93ec-4126-a2f6-530ac5217c96", "cb2e56cd-5502-4e4c-88e0-a9955715569b",
                "CastleExterior", "Aggressive"),
            [5] = new(5, "Dwarf", "adventurezone01/p_dwrf_thequarry", "p_dwrf_thequarry",
                "NodeA", "NodeB", "NodeC", "TrainingWithGwendower",
                "2a606713-1fa8-4253-ade3-c05d1a0bdd3a", "3e72f03f-150a-4788-9024-08020711cacf",
                "d40ba708-26d2-43cb-a28d-386dcc829bac", "84df3448-eb70-41f0-96d3-d088c8344fb9",
                "88fe73d5-a42a-408d-97c2-ca5651affc1b", "d4a78dc8-2505-484e-8088-245447fa3f9d",
                "de641801-55a2-4df8-b298-d2e91c21718d", "b43e98a6-040e-4447-a576-e8e5bdd3df11",
                "CastleExterior", "Comfortable"),
            [6] = new(6, "Shin'hare", "adventurezone01/p_shnhr_jinguru", "p_shnhr_jinguru",
                "Mitsuo", "Sora", "Uyuki", "TrainingWithSora",
                "d6fb2c8c-51a3-4a9d-8761-bb9275f53fd6", "9c508f59-0c71-4ca6-8cf1-1c5647f3cde4",
                "645e5c27-7ed2-485b-babd-f8bc7856a0b1", "a95cf2a9-a860-4caa-9022-070c24cbc626",
                "d89040c9-75c1-4f10-a647-f35679bcff72", "beb123ef-c255-43c0-bebc-06cd79565f66",
                "510ed850-5a3e-437b-875e-7834cacd1865", "ddc235eb-c6dc-4384-b567-70eb7498b729",
                "Forest", "Comfortable"),
            [7] = new(7, "Vennen", "adventurezone01/p_vnnn_thehatchery", "p_vnnn_thehatchery",
                "Orzh", "Zilth", "Xarlot", "TrainingWithZilth",
                "c1d936df-518b-4dfa-bdfe-8ce60e6aa278", "4198e540-3301-42de-a394-a8e1f80b8b64",
                "3ab83907-6db4-4c29-ab0d-5076393dd744", "25b66849-845a-41e9-b0d5-ee11d79879a0",
                "782369c4-61c2-4a3e-97ce-660f2d062be7", "8f27b71d-42ce-4120-8e6f-380a31f273d2",
                "f56ba80f-af31-4d75-a4bc-640f899ddd32", "e7539c3a-0bac-486c-9792-0293db113268",
                "CastleExterior", "Comfortable"),
            [8] = new(8, "Necrotic", "adventurezone01/p_ncrtc_necropolis", "p_ncrtc_necropolis",
                "Drokkord", "Iddi", "Margugram", "TrainingWithIddi",
                "827e9c9b-7d79-4a01-81ca-1c11907ab24d", "5ca75696-f381-4d4f-abd9-a6428dfb3cd9",
                "f526e886-6577-4088-8bcc-9dfff3615ada", "6374b9a7-7ead-48b1-9752-e57024d4dec3",
                "4cf66046-086b-40f8-8237-4e4d13e64a42", "263dbbaa-d710-4e4f-b763-e638816bbcfa",
                "7b9a5a0d-727f-4df8-923f-1e5a5b24c522", "b173e3d2-bdd3-44cb-b554-85d1c56b0cd2",
                "CastleExterior", "Comfortable"),
        };

    public static CampaignRaceConfig Race(int race) =>
        Races.TryGetValue(race, out var config) ? config : Races[1];

    public static int CampaignTypeToInt(string type) => type.ToUpperInvariant() switch
    {
        "ANY" => 0,
        "DUNGEON" => 1,
        "AREA" => 2,
        "WORLD" => 3,
        "QUEST" => 4,
        "ACHIEVE" => 5,
        "PANORAMA" => 6,
        "STRONGHOLD" => 7,
        "TEST" => 8,
        _ => 2,
    };

    public static JsonObject CreateStarterPanoramaState(CampaignRunRecord record)
    {
        var cfg = Race(record.Race);
        object[] locNodes =
        [
            new { Name = cfg.IntroNpc, Data = new { id = cfg.IntroNpc, type = "DEFAULT" } },
            new { Name = cfg.TrainerNpc, Data = new { id = cfg.TrainerNpc, type = "DEFAULT", championId = cfg.AiChampionGuid } },
            new { Name = cfg.QuestNpc, Data = new { id = cfg.QuestNpc, type = "DEFAULT" } },
            new { Name = cfg.TrainingNode, Data = new { id = cfg.TrainingNode, type = "DEFAULT" } },
        ];

        return ToObject(new
        {
            CampID = record.CampaignId,
            ChampID = record.ChampionId,
            TempType = "PANORAMA",
            PayGroups = Array.Empty<object>(),
            CSlide = (object?)null,
            ALoc = cfg.IntroNpc,
            VisLocs = new object[] { ConversationLocation(cfg.IntroNpc, cfg.IntroConversation) },
            LocNodes = locNodes,
            Encounters = new object[]
            {
                new { Name = cfg.TrainingEncounter, Data = new { encscene = cfg.TrainingEncounter } },
            },
            Champions = Array.Empty<object>(),
            CurState = "EXPLORE",
            LastNode = cfg.IntroNpc,
            PublicState = new
            {
                Data = new
                {
                    CampaignGroup = "PANORAMA",
                    IsStarterPano = true,
                    HideQuickNavigation = false,
                    RaceTutorialBattleUnlocked = false,
                },
            },
            TutorialDone = false,
            Started = (string?)null,
            Finished = (string?)null,
            FinishReason = (string?)null,
            Wins = 0,
            Losses = 0,
            Score = 0,
            HealthAdj = 0,
            DungeonLifeAdj = 0,
            Flags = new Dictionary<string, object>(),
        });
    }

    public static JsonObject CreateCrayburnQuestState(CampaignRunRecord record)
    {
        return ToObject(new
        {
            CampID = record.CampaignId,
            ChampID = record.ChampionId,
            TempType = "QUEST",
            PayGroups = Array.Empty<object>(),
            CSlide = (object?)null,
            ALoc = CampaignCrayburnConfig.QuestFirstLocation,
            VisLocs = new object[]
            {
                CampaignLocation(
                    CampaignCrayburnConfig.QuestFirstLocation,
                    "Dungeon",
                    encounter: null,
                    conversationId: null,
                    enabled: true,
                    visible: true),
            },
            LocNodes = new object[]
            {
                new
                {
                    Name = CampaignCrayburnConfig.QuestFirstLocation,
                    Data = new
                    {
                        id = CampaignCrayburnConfig.QuestFirstLocation,
                        type = "DEFAULT",
                    },
                },
            },
            Encounters = Array.Empty<object>(),
            Champions = Array.Empty<object>(),
            CurState = "EXPLORE",
            LastNode = CampaignCrayburnConfig.QuestFirstLocation,
            PublicState = new
            {
                Data = new
                {
                    CampaignGroup = "DUNGEON",
                },
            },
            Started = (string?)null,
            Finished = (string?)null,
            FinishReason = (string?)null,
            Wins = 0,
            Losses = 0,
            Score = 0,
            HealthAdj = 0,
            DungeonLifeAdj = 0,
            Flags = new Dictionary<string, object>(),
        });
    }

    public static JsonObject CreateCrayburnDungeonState(CampaignRunRecord record, string startedUtc)
    {
        var cfg = CampaignCrayburnConfig.Race(record.Race);
        object[] locNodes =
        [
            Node("Entrance"),
            Node("WatchTower"),
            Node("Drawbridge"),
            Node("CastleGate"),
            Node("InnerBailey"),
            Node("TowerGate"),
            Node("PenworthTower"),
        ];

        object[] visLocs =
        [
            CampaignLocation("Entrance", "Dungeon", enabled: true, visible: true),
            CampaignLocation("WatchTower", "Convo", conversationId: cfg.WatchTowerConversation, enabled: true, visible: true),
            CampaignLocation("Drawbridge", "Convo", conversationId: cfg.DrawbridgeConversation),
            CampaignLocation("CastleGate", "Encounter", cfg.CastleGateEncounter, cfg.CastleGateConversation),
            CampaignLocation("InnerBailey", "Convo", conversationId: cfg.InnerBaileyConversation),
            CampaignLocation("TowerGate", "Encounter", cfg.TowerGateEncounter, cfg.TowerGateConversation),
            CampaignLocation("PenworthTower", "Encounter", cfg.PenworthTowerEncounter, cfg.PenworthTowerConversation),
        ];

        var encounters = new object[]
        {
            new { Name = cfg.CastleGateEncounter, Data = new { encscene = cfg.CastleGateEncounter } },
            new { Name = cfg.TowerGateEncounter, Data = new { encscene = cfg.TowerGateEncounter } },
            new { Name = cfg.PenworthTowerEncounter, Data = new { encscene = cfg.PenworthTowerEncounter } },
        };

        return ToObject(new
        {
            CampID = record.CampaignId,
            ChampID = record.ChampionId,
            TempType = "DUNGEON",
            PayGroups = Array.Empty<object>(),
            CSlide = (object?)null,
            ALoc = "Entrance",
            VisLocs = visLocs,
            LocNodes = locNodes,
            Encounters = encounters,
            Champions = Array.Empty<object>(),
            CurState = "EXPLORE",
            LastNode = "Entrance",
            PublicState = new
            {
                Data = new
                {
                    CampaignGroup = "DUNGEON",
                    visited_nodes = new[] { "Entrance" },
                    IsStarterDungeon = true,
                },
            },
            Started = startedUtc,
            Finished = (string?)null,
            FinishReason = (string?)null,
            Wins = 0,
            Losses = 0,
            Score = 0,
            HealthAdj = 0,
            DungeonLifeAdj = 0,
            Flags = new Dictionary<string, object>(),
        });
    }

    /// <summary>
    /// Entry state for the authored AZ1 overworld. Only the verified opening
    /// is actionable; other directly mapped nodes stay hidden and disabled.
    /// </summary>
    public static JsonObject CreateAz1AreaState(CampaignRunRecord record, string startedUtc)
    {
        var entry = CampaignLocation(CampaignAz1Config.StartingTitle, "Empty", enabled: true, visible: true);
        var next = CampaignLocation(CampaignAz1Config.NextTitle, "Empty", enabled: true, visible: true);
        var third = CampaignLocation(CampaignAz1Config.ThirdTitle, "Empty");
        var firstData = entry["Data"]!.AsObject();
        firstData["node"] = CampaignAz1Config.StartingNode;
        firstData["title"] = CampaignAz1Config.StartingTitle;
        firstData["description"] = string.Empty;
        firstData["completed"] = true;
        var secondData = next["Data"]!.AsObject();
        secondData["node"] = CampaignAz1Config.NextNode;
        secondData["title"] = CampaignAz1Config.NextTitle;
        secondData["description"] = string.Empty;
        var thirdData = third["Data"]!.AsObject();
        thirdData["node"] = CampaignAz1Config.ThirdNode;
        thirdData["title"] = CampaignAz1Config.ThirdTitle;
        thirdData["description"] = string.Empty;

        var state = ToObject(new
        {
            CampID = record.CampaignId,
            ChampID = record.ChampionId,
            TempType = "AREA",
            PayGroups = Array.Empty<object>(),
            CSlide = (object?)null,
            ALoc = (string?)null,
            VisLocs = new[] { entry, next, third },
            LocNodes = new[] { Node(CampaignAz1Config.StartingNode), Node(CampaignAz1Config.NextNode), Node(CampaignAz1Config.ThirdNode) },
            Encounters = Array.Empty<object>(),
            Champions = Array.Empty<object>(),
            CurState = "EXPLORE",
            LastNode = CampaignAz1Config.StartingNode,
            PublicState = new
            {
                Data = new
                {
                    CampaignGroup = "AREA",
                    visited_nodes = new[] { CampaignAz1Config.StartingNode },
                    visited_paths = Array.Empty<string>(),
                    locked_paths = Array.Empty<string>(),
                    quest_nodes = Array.Empty<string>(),
                },
            },
            Started = startedUtc,
            Finished = (string?)null,
            FinishReason = (string?)null,
            Wins = 0,
            Losses = 0,
            Score = 0,
            HealthAdj = 0,
            DungeonLifeAdj = 0,
            Flags = new Dictionary<string, object>(),
        });
        EnsureAz1MapState(state);
        SyncAz1OpeningExitLocks(state);
        return state;
    }

    /// <summary>
    /// Add only directly mapped, original-client AZ1 locations to a new or
    /// persisted AREA. All additional locations remain hidden and disabled;
    /// their encounter/action types are deliberately not inferred from titles.
    /// </summary>
    public static bool EnsureAz1MapState(JsonObject state)
    {
        if (state["VisLocs"] is not JsonArray locations || state["LocNodes"] is not JsonArray nodes)
            return false;

        var changed = false;
        foreach (var authored in CampaignAz1MapCatalog.DirectLocations)
        {
            if (!locations.OfType<JsonObject>().Any(loc =>
                string.Equals(loc["Data"]?["node"]?.ToString(), authored.Id, StringComparison.Ordinal)))
            {
                var loc = CampaignLocation(authored.Title, "None");
                loc["Data"]!["node"] = authored.Id;
                loc["Data"]!["title"] = authored.Title;
                locations.Add(loc);
                changed = true;
            }

            if (!nodes.OfType<JsonObject>().Any(node =>
                string.Equals(node["Name"]?.ToString(), authored.Id, StringComparison.Ordinal)))
            {
                nodes.Add(ToObject(new
                {
                    Name = authored.Id,
                    Data = new { id = authored.PhysicalId, type = "DEFAULT" },
                }));
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Reconcile the first authored AZ1 conversation for existing AREA saves.
    /// This preserves all visited paths and completed nodes, and never creates
    /// quest objectives or reveals an unearned map destination.
    /// </summary>
    public static bool PrepareAz1Welcome(JsonObject state, string conversationId)
    {
        if (state["VisLocs"] is not JsonArray locations)
            return false;
        var changed = false;
        var nodeTwo = locations.OfType<JsonObject>()
            .Select(static location => location["Data"] as JsonObject)
            .FirstOrDefault(data => data?["node"]?.ToString() == CampaignAz1Config.NextNode);
        if (nodeTwo is null)
            return false;

        if (nodeTwo["completed"]?.GetValue<bool>() != true)
        {
            changed |= SetValue(nodeTwo, "type", JsonValue.Create("Convo"));
            changed |= SetValue(nodeTwo, "conversationId", JsonValue.Create(conversationId));
            changed |= SetValue(nodeTwo, "givequest", JsonValue.Create(true));
            changed |= SetValue(nodeTwo, "autostart", JsonValue.Create(true));
            // Earlier AREA saves could retain the internal node ID as ALoc.
            // Repair only this active welcome location; preserve LastNode and visits.
            if (string.Equals(state["ALoc"]?.ToString(), CampaignAz1Config.NextNode, StringComparison.Ordinal))
            {
                var activeName = nodeTwo["name"]?.ToString();
                state["ALoc"] = string.IsNullOrWhiteSpace(activeName)
                    ? CampaignAz1Config.NextTitle : activeName;
                changed = true;
            }
        }

        var nodeThree = locations.OfType<JsonObject>()
            .Select(static location => location["Data"] as JsonObject)
            .FirstOrDefault(data => data?["node"]?.ToString() == CampaignAz1Config.ThirdNode);
        if (nodeThree is null)
        {
            var third = CampaignLocation(CampaignAz1Config.ThirdTitle, "Empty");
            third["Data"]!["node"] = CampaignAz1Config.ThirdNode;
            third["Data"]!["title"] = CampaignAz1Config.ThirdTitle;
            locations.Add(third);
            changed = true;
        }
        if (state["LocNodes"] is JsonArray nodes &&
            !nodes.OfType<JsonObject>().Any(node => node["Name"]?.ToString() == CampaignAz1Config.ThirdNode))
        {
            nodes.Add(ToObject(Node(CampaignAz1Config.ThirdNode)));
            changed = true;
        }
        // Populate the directly authored exits without revealing them. These
        // become reachable only after the Wild Cub encounter is resolved.
        foreach (var (node, title) in new[]
        {
            (CampaignAz1Config.FourthNode, CampaignAz1Config.FourthTitle),
            (CampaignAz1Config.OaksNode, CampaignAz1Config.OaksTitle),
        })
        {
            if (!locations.OfType<JsonObject>().Any(item => item["Data"]?["node"]?.ToString() == node))
            {
                var location = CampaignLocation(title, "Empty");
                location["Data"]!["node"] = node;
                location["Data"]!["title"] = title;
                location["Data"]!["description"] = string.Empty;
                locations.Add(location);
                changed = true;
            }
            if (state["LocNodes"] is JsonArray locNodes &&
                !locNodes.OfType<JsonObject>().Any(item => item["Name"]?.ToString() == node))
            {
                locNodes.Add(ToObject(Node(node)));
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Promote Dunnwood from the earlier empty map placeholder into the
    /// original Wild Cub encounter, retaining the authored first-visit prelude.
    /// </summary>
    public static bool PrepareAz1Dunnwood(JsonObject state, string encounterId, string preludeId)
    {
        if (state["VisLocs"] is not JsonArray locations)
            return false;
        var third = locations.OfType<JsonObject>()
            .Select(static loc => loc["Data"] as JsonObject)
            .FirstOrDefault(data => data?["node"]?.ToString() == CampaignAz1Config.ThirdNode);
        if (third is null)
            return false;
        var changed = false;
        var preludeFinished = third["pre_encounter_completed"]?.GetValue<bool>() == true;
        var completed = third["completed"]?.GetValue<bool>() == true;
        if (!completed)
        {
            changed |= SetValue(third, "type", JsonValue.Create(preludeFinished ? "Encounter" : "Convo"));
            changed |= SetValue(third, "conversationId", preludeFinished ? null : JsonValue.Create(preludeId));
            changed |= SetValue(third, "encounter", preludeFinished ? JsonValue.Create(encounterId) : null);
            changed |= SetValue(third, "autostart", JsonValue.Create(!preludeFinished));
            changed |= SetValue(third, "pre_encounter", JsonValue.Create(true));
            // Rehydrating the authored scene must not erase a capture-gated retry
            // established by the completed battle result.
            changed |= SetValue(third, "repeatable", JsonValue.Create(
                preludeFinished && third["repeatable"]?.GetValue<bool>() == true));
        }
        if (state["Encounters"] is JsonArray encounters &&
            !encounters.OfType<JsonObject>().Any(e => e["Name"]?.ToString() == encounterId))
        {
            encounters.Add(ToObject(new { Name = encounterId, Data = new { encscene = encounterId } }));
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Rehydrate the original Sporemist intro and card chooser. The offer is
    /// client-readable only after the conversation, and a claimed reward never
    /// reopens. Reference card choices are separately validated at startup.
    /// </summary>
    public static bool PrepareAz1Sporemist(JsonObject state, string conversationId)
    {
        if (state["VisLocs"] is not JsonArray locations)
            return false;
        var location = locations.OfType<JsonObject>()
            .Select(static item => item["Data"] as JsonObject)
            .FirstOrDefault(data => data?["node"]?.ToString() == CampaignAz1Config.FourthNode);
        if (location is null)
            return false;

        var changed = false;
        var introductionDone = location["shroom_intro_completed"]?.GetValue<bool>() == true;
        var completed = location["completed"]?.GetValue<bool>() == true;
        var validOffers = CampaignShroomRewardCatalog.TryGetVerifiedClientTemplates(out var offers);
        var chooserReady = introductionDone && !completed && validOffers;
        changed |= SetValue(location, "type", JsonValue.Create(completed ? "None" : chooserReady ? "ShroomHaus" : "Convo"));
        changed |= SetValue(location, "conversationId", introductionDone ? null : JsonValue.Create(conversationId));
        changed |= SetValue(location, "encounter", null);
        changed |= SetValue(location, "autostart", JsonValue.Create(!completed && (!introductionDone || chooserReady)));
        changed |= SetValue(location, "shroomhaus_ready", JsonValue.Create(chooserReady));
        changed |= SetValue(location, "choices", chooserReady
            ? new JsonArray(offers.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()) : null);
        changed |= SetValue(location, "selection-type", JsonValue.Create("Shroomkin_Haus"));
        changed |= SetValue(location, "selection-desc", JsonValue.Create("Shroomkin_Instructions"));
        // Neither an unvisited nor a dialogue-only node may bypass Dunnwood.
        var opened = HasWonAz1Bear(state);
        changed |= SetValue(location, "enabled", JsonValue.Create(opened));
        return changed;
    }

    /// <summary>
    /// A battle win opens map exits independently from the capture objective.
    /// A persisted retryable encounter with an AREA win is evidence of an
    /// earlier victory when the dedicated win receipt is absent.
    /// </summary>
    public static bool HasWonAz1Bear(JsonObject state)
    {
        var bear = FindAz1Location(state, CampaignAz1Config.ThirdNode);
        return bear?["bear_defeated"]?.GetValue<bool>() == true
            || bear?["completed"]?.GetValue<bool>() == true
            || (bear?["repeatable"]?.GetValue<bool>() == true
                && (state["Wins"]?.GetValue<int>() ?? 0) > 0);
    }

    public static bool PrepareAz1GaalCamp(JsonObject state, string first, string repeat, string hasFortune)
    {
        var location = FindAz1Location(state, CampaignAz1Config.OaksNode);
        if (location is null)
            return false;
        var changed = false;
        var visits = location["gaal_visits"]?.GetValue<int>() ?? 0;
        // A real active Fortune is not minted by the conversation itself.
        // Keep its original dialogue branch reserved for future validated grants.
        var fortune = state["PublicState"]?["Data"]?["gaal_fortune_guid"]?.ToString();
        var conversation = !string.IsNullOrEmpty(fortune) ? hasFortune : visits > 0 ? repeat : first;
        changed |= SetValue(location, "type", JsonValue.Create("Convo"));
        changed |= SetValue(location, "encounter", null);
        changed |= SetValue(location, "conversationId", JsonValue.Create(conversation));
        changed |= SetValue(location, "completed", JsonValue.Create(false));
        changed |= SetValue(location, "repeatable", JsonValue.Create(visits > 0));
        changed |= SetValue(location, "autostart", JsonValue.Create(visits == 0));
        changed |= SetValue(location, "enabled", JsonValue.Create(HasWonAz1Bear(state)));
        return changed;
    }

    /// <summary>
    /// Project the Masked Sergeant conversation authored for Node R/Node00R.
    /// Quest ownership and completion are resolved by the run store; visiting
    /// Vale neither grants that faction quest nor opens its bridge/side paths.
    /// </summary>
    public static bool PrepareAz1ValeOfOberon(JsonObject state, string step1, string step2,
        string step3, bool hasFactionQuest, bool factionQuestFinished)
    {
        var vale = FindAz1Location(state, CampaignAz1Config.ValeNode);
        var gaal = FindAz1Location(state, CampaignAz1Config.OaksNode);
        if (vale is null || gaal is null)
            return false;

        var visits = vale["vale_visits"]?.GetValue<int>() ?? 0;
        var completed = vale["completed"]?.GetValue<bool>() == true;
        var available = gaal["enabled"]?.GetValue<bool>() == true
            && (gaal["gaal_visits"]?.GetValue<int>() ?? 0) > 0;
        var selected = !hasFactionQuest ? step1 : factionQuestFinished ? step3 : step2;
        var changed = false;
        changed |= SetValue(vale, "visible", JsonValue.Create(available));
        changed |= SetValue(vale, "enabled", JsonValue.Create(available));
        changed |= SetValue(vale, "type", JsonValue.Create(completed ? "None" : "Convo"));
        changed |= SetValue(vale, "conversationId", completed ? null : JsonValue.Create(selected));
        changed |= SetValue(vale, "encounter", null);
        changed |= SetValue(vale, "autostart", JsonValue.Create(available && !completed && visits == 0));
        changed |= SetValue(vale, "repeatable", JsonValue.Create(!completed && visits > 0));
        return changed;
    }

    /// <summary>
    /// The first completed Milosh conversation opens the authored path to
    /// the Cockatwice Chick encounter. Its opening dialogue is independent
    /// from optional Fortune payment. A won battle remains retryable until the
    /// matching Tamed capture objective is completed.
    /// </summary>
    public static bool PrepareAz1Sutherland(JsonObject state, string sceneId, string preludeId)
    {
        var location = FindAz1Location(state, CampaignAz1Config.SutherlandNode);
        var gaal = FindAz1Location(state, CampaignAz1Config.OaksNode);
        if (location is null || gaal is null)
            return false;
        var changed = false;
        var visitedGaal = (gaal["gaal_visits"]?.GetValue<int>() ?? 0) > 0;
        var available = visitedGaal && gaal["enabled"]?.GetValue<bool>() == true;
        var completed = location["completed"]?.GetValue<bool>() == true;
        var preludeDone = location["pre_encounter_completed"]?.GetValue<bool>() == true;
        changed |= SetValue(location, "visible", JsonValue.Create(available));
        changed |= SetValue(location, "enabled", JsonValue.Create(available));
        changed |= SetValue(location, "type", JsonValue.Create(completed ? "None" : preludeDone ? "Encounter" : "Convo"));
        changed |= SetValue(location, "conversationId", !completed && !preludeDone ? JsonValue.Create(preludeId) : null);
        changed |= SetValue(location, "encounter", !completed && preludeDone ? JsonValue.Create(sceneId) : null);
        changed |= SetValue(location, "autostart", JsonValue.Create(available && !completed && !preludeDone));
        changed |= SetValue(location, "repeatable", JsonValue.Create(!completed && preludeDone));
        if (state["Encounters"] is JsonArray encounters
            && !encounters.OfType<JsonObject>().Any(e => string.Equals(e["Name"]?.ToString(), sceneId, StringComparison.OrdinalIgnoreCase)))
        {
            encounters.Add(ToObject(new { Name = sceneId, Data = new { encscene = sceneId } }));
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Project all original prefab path locks while unlocking only explicitly
    /// implemented opening exits. A visible node may still have a locked path.
    /// Saved visited-path history and unknown lock identifiers are retained.
    /// </summary>
    public static bool SyncAz1OpeningExitLocks(JsonObject state)
    {
        if (state["PublicState"]?["Data"] is not JsonObject publicData)
            return false;
        var changed = false;
        if (publicData["locked_paths"] is not JsonArray locked)
        {
            locked = new JsonArray();
            publicData["locked_paths"] = locked;
            changed = true;
        }

        // A physical path is not automatically a playable connection. Only
        // explicitly implemented opening exits can unlock; all other original
        // prefab paths stay locked until their own authored gates are known.
        foreach (var path in CampaignAz1MapCatalog.Paths)
        {
            var permitted = CampaignAz1Config.IsOpeningPath(path.Id)
                && FindAz1Location(state, path.From)?["enabled"]?.GetValue<bool>() == true
                && FindAz1Location(state, path.To)?["enabled"]?.GetValue<bool>() == true;
            if (path.Id == CampaignAz1Config.SecondPath)
                permitted &= FindAz1Location(state, CampaignAz1Config.NextNode)?["completed"]?.GetValue<bool>() == true;
            if (path.Id is CampaignAz1Config.DunnwoodToHollow or CampaignAz1Config.DunnwoodToOaks)
                permitted &= HasWonAz1Bear(state);
            if (path.Id == CampaignAz1Config.OaksToVale)
                permitted &= (FindAz1Location(state, CampaignAz1Config.OaksNode)?["gaal_visits"]?.GetValue<int>() ?? 0) > 0;
            if (path.Id == CampaignAz1Config.OaksToSutherland)
                permitted &= (FindAz1Location(state, CampaignAz1Config.OaksNode)?["gaal_visits"]?.GetValue<int>() ?? 0) > 0;

            var index = -1;
            for (var i = 0; i < locked.Count; i++)
                if (string.Equals(locked[i]?.ToString(), path.Id, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }

            if (!permitted && index < 0)
            {
                locked.Add(path.Id);
                changed = true;
            }
            else if (permitted && index >= 0)
            {
                locked.RemoveAt(index);
                changed = true;
            }
        }
        return changed;
    }

    private static JsonObject? FindAz1Location(JsonObject state, string node) =>
        (state["VisLocs"] as JsonArray)?.OfType<JsonObject>()
            .Select(static item => item["Data"] as JsonObject)
            .FirstOrDefault(data => string.Equals(data?["node"]?.ToString(), node, StringComparison.Ordinal));

    public static JsonObject CreateAz1QuestState(CampaignRunRecord record, CampaignAz1AuthoredCatalog.Quest quest)
    {
        var objectiveLocations = new JsonArray();
        var objectiveNodes = new JsonArray();
        foreach (var objective in quest.Objectives)
        {
            var loc = CampaignLocation(objective.Node,
                objective.Encounter is not null ? "Encounter" : "Convo",
                objective.Encounter, objective.Conversation, enabled: true, visible: true);
            loc["Data"]!["title"] = objective.Title;
            objectiveLocations.Add(loc);
            objectiveNodes.Add(ToObject(Node(objective.Node)));
        }
        return new JsonObject
        {
            ["CampID"] = record.CampaignId,
            ["ChampID"] = record.ChampionId,
            ["TempType"] = "QUEST",
            ["PayGroups"] = new JsonArray(),
            ["CSlide"] = null,
            ["ALoc"] = null,
            ["VisLocs"] = objectiveLocations,
            ["LocNodes"] = objectiveNodes,
            ["Encounters"] = new JsonArray(),
            ["Champions"] = new JsonArray(),
            ["CurState"] = "EXPLORE",
            ["LastNode"] = quest.Objectives[0].Node,
            ["PublicState"] = ToObject(new { Data = new { CampaignGroup = "AREA" } }),
            ["QuestTemplateId"] = quest.TemplateId,
            ["Started"] = (string?)null,
            ["Finished"] = null,
            ["FinishReason"] = null,
            ["Wins"] = 0,
            ["Losses"] = 0,
            ["Score"] = 0,
            ["HealthAdj"] = 0,
            ["DungeonLifeAdj"] = 0,
            ["Flags"] = new JsonObject(),
        };
    }

    public static JsonObject BuildInputResponse(CampaignRunRecord record, bool success = true, JsonNode? stateOverride = null, JsonObject? appliedOverride = null)
    {
        return new JsonObject
        {
            ["Cid"] = record.CampaignId,
            ["Success"] = success,
            ["Errors"] = new JsonArray(),
            ["CurState"] = stateOverride?.DeepClone() ?? record.State.DeepClone(),
            ["Applied"] = appliedOverride ?? EmptyAppliedUpdates(),
        };
    }

    public static JsonObject BuildFailure(ulong campaignId, string error)
    {
        return new JsonObject
        {
            ["Cid"] = campaignId,
            ["Success"] = false,
            ["Errors"] = new JsonArray { error },
            ["CurState"] = null,
            ["Applied"] = EmptyAppliedUpdates(),
        };
    }

    public static JsonObject BuildTemplateInfo(CampaignRunRecord record)
    {
        return ToObject(new
        {
            CampID = record.CampaignId,
            CampType = CampaignTypeToInt(record.CampaignType),
            ReckID = new { lo = 0, hi = 0 },
            TemplateName = record.TemplateName,
            Version = 1,
            ClosedOn = (string?)null,
        });
    }

    public static JsonObject BuildCampSummary(CampaignRunRecord record)
    {
        var type = record.CampaignType.ToUpperInvariant();
        var campType = CampaignTypeToInt(type);
        var cfg = Race(record.Race);

        string assetBundle;
        string levelPrefab;
        string backgroundPrefab;
        string nodesPrefab;
        string campaignTemplateId;

        switch (type)
        {
            case "DUNGEON":
                assetBundle = string.Empty;
                levelPrefab = string.Empty;
                backgroundPrefab = CampaignCrayburnConfig.DungeonBackgroundPrefab;
                nodesPrefab = CampaignCrayburnConfig.DungeonNodesPrefab;
                campaignTemplateId = CampaignCrayburnConfig.DungeonCampaignTemplateId;
                break;
            case "AREA":
                assetBundle = string.Empty;
                levelPrefab = string.Empty;
                backgroundPrefab = CampaignAz1Config.BackgroundPrefab;
                nodesPrefab = CampaignAz1Config.NodesPrefab;
                campaignTemplateId = CampaignAz1Config.CampaignTemplateId;
                break;
            case "QUEST":
                // QUEST is journal state, not a rendered scene. Keep summary identity
                // type-correct without assigning panorama/dungeon scene prefabs.
                assetBundle = string.Empty;
                levelPrefab = string.Empty;
                backgroundPrefab = string.Empty;
                nodesPrefab = string.Empty;
                campaignTemplateId = record.State["QuestTemplateId"]?.ToString() ?? CampaignCrayburnConfig.QuestTemplateId;
                break;
            default:
                campType = 6;
                type = "PANORAMA";
                assetBundle = cfg.Bundle;
                levelPrefab = cfg.Prefab;
                backgroundPrefab = "campaign/tutorial/prefabs/background1";
                nodesPrefab = string.Empty;
                campaignTemplateId = "2f59b729-7cdf-4fb4-abc3-5654a644df65";
                break;
        }

        return ToObject(new
        {
            CampID = record.CampaignId,
            ReckID = new { lo = 0, hi = 0 },
            CampType = campType,
            TypeInfo = new
            {
                Name = record.TemplateName,
                Type = type,
                Doc = (string?)null,
                NameExclusive = false,
                TypeExclusive = false,
                AssetBundle = assetBundle,
                LevelPrefab = levelPrefab,
                BackgroundPrefab = backgroundPrefab,
                NodesPrefab = nodesPrefab,
                CampaignTemplateId = campaignTemplateId,
            },
            TemplateName = record.TemplateName,
            IsDeckEditable = true,
            Version = 1,
            ClosedOn = (string?)null,
        });
    }

    public static JsonObject EmptyAppliedUpdates() => ToObject(new
    {
        Pending = Array.Empty<object>(),
        Completed = Array.Empty<object>(),
        Accounts = Array.Empty<object>(),
        Champions = Array.Empty<object>(),
        Decks = Array.Empty<object>(),
        Items = Array.Empty<object>(),
        Cards = Array.Empty<object>(),
        MercInfos = Array.Empty<object>(),
        AccountFlags = Array.Empty<object>(),
    });

    /// <summary>
    /// After the Crayburn report, the race's original introductory NPC offers
    /// the authored travel conversation. The change is safe to replay for a
    /// completed report on an existing saved starter panorama.
    /// </summary>
    public static bool PreparePostCrayburnTravel(JsonObject state, CampaignRaceConfig cfg)
    {
        var changed = false;
        if (state["VisLocs"] is not JsonArray locations)
        {
            locations = new JsonArray();
            state["VisLocs"] = locations;
            changed = true;
        }

        var intro = locations.OfType<JsonObject>()
            .Select(static location => location["Data"] as JsonObject)
            .FirstOrDefault(data => data is not null &&
                (string.Equals(data["node"]?.ToString(), cfg.IntroNpc, StringComparison.Ordinal)
                 || string.Equals(data["name"]?.ToString(), cfg.IntroNpc, StringComparison.Ordinal)));

        if (intro is null)
        {
            var location = ConversationLocation(cfg.IntroNpc, cfg.TransitionConversation, giveQuest: true);
            locations.Add(location);
            changed = true;
        }
        else
        {
            changed |= SetValue(intro, "name", JsonValue.Create(cfg.IntroNpc));
            changed |= SetValue(intro, "node", JsonValue.Create(cfg.IntroNpc));
            changed |= SetValue(intro, "type", JsonValue.Create("Convo"));
            changed |= SetValue(intro, "conversationId", JsonValue.Create(cfg.TransitionConversation));
            changed |= SetValue(intro, "encounter", null);
            changed |= SetValue(intro, "completed", JsonValue.Create(false));
            changed |= SetValue(intro, "enabled", JsonValue.Create(true));
            changed |= SetValue(intro, "visible", JsonValue.Create(true));
            changed |= SetValue(intro, "repeatable", JsonValue.Create(false));
            changed |= SetValue(intro, "givequest", JsonValue.Create(true));
            changed |= SetValue(intro, "turninquest", JsonValue.Create(false));
            changed |= SetValue(intro, "autostart", JsonValue.Create(false));
        }

        changed |= SetValue(state, "PostCrayburnReport", JsonValue.Create(true));
        if (string.Equals(state["ALoc"]?.ToString(), cfg.QuestNpc, StringComparison.Ordinal))
        {
            state["ALoc"] = null;
            changed = true;
        }
        return changed;
    }

    private static bool SetValue(JsonObject target, string key, JsonNode? desired)
    {
        if (JsonNode.DeepEquals(target[key], desired))
            return false;
        target[key] = desired?.DeepClone();
        return true;
    }

    public static JsonObject ConversationLocation(string name, string conversationId, bool giveQuest = false)
    {
        return ToObject(new
        {
            Data = new
            {
                name,
                node = name,
                type = "Convo",
                autostart = false,
                autopan = false,
                autotrigger = false,
                battle = (object?)null,
                completed = false,
                enabled = true,
                visible = true,
                repeatable = false,
                givequest = giveQuest,
                turninquest = false,
                impassable = false,
                unknown = false,
                encounter = (string?)null,
                encounter_desc = (string?)null,
                allow_cancel = false,
                conversationId,
            },
        });
    }

    private static object Node(string name) => new
    {
        Name = name,
        Data = new
        {
            id = name,
            type = "DEFAULT",
        },
    };

    private static JsonObject CampaignLocation(
        string name,
        string type,
        string? encounter = null,
        string? conversationId = null,
        bool enabled = false,
        bool visible = false)
    {
        return ToObject(new
        {
            Data = new
            {
                name,
                node = name,
                type,
                autostart = false,
                autopan = false,
                autotrigger = false,
                battle = (object?)null,
                completed = false,
                enabled,
                visible,
                repeatable = false,
                givequest = false,
                turninquest = false,
                impassable = false,
                unknown = false,
                encounter,
                encounter_desc = (string?)null,
                allow_cancel = false,
                conversationId,
            },
        });
    }

    private static JsonObject ToObject<T>(T value) =>
        JsonSerializer.SerializeToNode(value, Json)!.AsObject();
}
