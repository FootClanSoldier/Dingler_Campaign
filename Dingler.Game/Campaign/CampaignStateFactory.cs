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

    public static JsonObject BuildInputResponse(CampaignRunRecord record, bool success = true, JsonNode? stateOverride = null)
    {
        return new JsonObject
        {
            ["Cid"] = record.CampaignId,
            ["Success"] = success,
            ["Errors"] = new JsonArray(),
            ["CurState"] = stateOverride?.DeepClone() ?? record.State.DeepClone(),
            ["Applied"] = EmptyAppliedUpdates(),
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
        var cfg = Race(record.Race);
        return ToObject(new
        {
            CampID = record.CampaignId,
            ReckID = new { lo = 0, hi = 0 },
            CampType = 6,
            TypeInfo = new
            {
                Name = record.TemplateName,
                Type = "PANORAMA",
                Doc = (string?)null,
                NameExclusive = false,
                TypeExclusive = false,
                AssetBundle = cfg.Bundle,
                LevelPrefab = cfg.Prefab,
                BackgroundPrefab = "campaign/tutorial/prefabs/background1",
                NodesPrefab = "",
                CampaignTemplateId = "2f59b729-7cdf-4fb4-abc3-5654a644df65",
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

    private static JsonObject ToObject<T>(T value) =>
        JsonSerializer.SerializeToNode(value, Json)!.AsObject();
}
