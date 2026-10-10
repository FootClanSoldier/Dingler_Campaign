namespace Dingler.Game.Campaign;

/// <summary>
/// Authored Crayburn identities used by the AZ0 quest handoff. QuestTemplate
/// identity/objectives come from the original HEX TemplateManager runtime; castle
/// node conversations and encounter scenes follow client-data-derived mappings
/// recorded in docs/Research/AZ0_Quest_Crayburn_Handoff.txt.
/// </summary>
public static class CampaignCrayburnConfig
{
    public const string QuestTemplateId = "dfe161d4-ad6a-4573-b309-dae6a41ba19f";
    public const string QuestScriptName = "az01_q_dwarf_crayburn";
    public const string QuestName = "AZ00 - Dwarf - Crayburn Castle";
    public const string QuestTitle = "Crayburn Castle";
    public const string QuestDungeonId = "b2153eef-22aa-46d9-977e-d8fc7d683691";
    public const string QuestFirstLocation = "Step1";
    public const string QuestFirstTitle = "Find the fallen shard";
    public const string QuestReportLocation = "Step2";
    public const string QuestReportTitle = "Report your success";
    public const string QuestAuthoredReportConversation = "21c62741-49c8-4da5-8b8d-440247911027";

    public const string DungeonTemplateName = "Crayburn Castle";
    public const string DungeonCampaignTemplateId = "5bcba43a-95c7-44b4-ba09-a3555a5edf05";
    public const string DungeonBackgroundPrefab = "campaign/az01/crayburncastle/prefabs/background";
    public const string DungeonNodesPrefab = "campaign/az01/crayburncastle/prefabs/nodes";

    private static readonly IReadOnlyDictionary<int, CampaignCrayburnRaceConfig> Races =
        new Dictionary<int, CampaignCrayburnRaceConfig>
        {
            [1] = new(
                "ba9fb1a9-406b-49b0-aa46-18044a9ac296",
                "29ad6621-9b02-4380-b4ff-3dfe6d8a4b81",
                "60a7d90d-c16f-4922-9c43-8e59bbc416e3", "5919c63a-38f6-487a-9d66-3b13cb12a520",
                "e83ffcda-1d7f-4ccf-8dd2-2e5cbdc0dc1d", "3c635381-acb3-4912-a3ce-501c53dbb691",
                "52cdef5d-fa56-45f9-8699-16b835bc129c",
                "1f9acf0f-970c-4e68-a6ac-558550482550", "be2c5dd9-6cb5-4cf8-a1ef-f050079b1130",
                "cc4080df-4ec8-46d9-aaf8-0d67537bfe86", "cb4b5e85-dfda-45e8-9b96-92973d6eadf5",
                "94076e42-9435-490d-82a4-a32a592cf3fa", "61ae67b4-1691-4b1f-903c-687c396256a9",
                "10b3d849-3cda-41a8-bbaf-0d43819a0b5a", "7c4e314c-4d6c-432c-a097-8d702a44cdc2",
                "0427b61f-251c-47b5-b89d-a0f2e2f42b1a"),
            [2] = new(
                "7f3d1858-a4ca-4166-b6c6-a911b9b60a01",
                "bfc57f7f-7832-480c-b0cd-2713562aff9a",
                "260f9a04-28f9-443a-8741-67e6264e090d", "f25f20b1-3cb3-4066-ae91-1c557f0928c2",
                "4fe768f9-4f82-445c-a178-db3730035dfd", "36725ee7-4476-4650-ad02-43c4b7f9a6ee",
                "43f26b07-6693-439a-847c-893b9c6b884d",
                "28a7724d-264c-41b2-86a4-5cdc1a16afb2", "31d6b2aa-bdf1-47ea-a0a5-a62bacdb75fa",
                "9790dab2-2198-4d29-8544-79c8515fa0d3", "f86bb43a-de74-4ac0-a730-cdd1479a90e2",
                "4ab9607d-b45b-4638-ad4b-f2331c38cde3", "a5b0cfd3-c85e-4c8f-a4ae-172179ae1450",
                "f6ffc170-f15d-4c34-90db-07e321fd3067", "5719984b-06c2-41f0-8304-87dd9817aa37",
                "bbc4460d-8452-4d49-a6e9-c64216f483b3"),
            [3] = new(
                "a74f5fbb-fc87-488d-921e-cd1cc7a856d8",
                "1de6dac7-b539-4603-95fb-18b16e6a95e9",
                "4084b46c-3d26-4159-8e01-ec86c1d0e7b6", "f3c0ac5b-ff09-488c-ad63-f11ff15acdcd",
                "8a39b557-c62d-4f44-bfd4-87e0579cccbe", "50bd1063-7597-424c-9885-1e3adc36dea4",
                "c3146d14-b7d1-4737-8c14-d1c4cac4be9f",
                "e1a7a3cc-2ad9-44fc-bde1-16db12eeb014", "2ba61b7b-6864-4582-a634-f9124fb2fdee",
                "ae96f501-9acf-4c72-a72d-c71a3b96142a", "b22b0125-6f0a-4d4b-8e5d-c23369ddd9c4",
                "e6c52582-14b5-439a-949d-f2911ff03ac7", "5f222319-7b4e-4ba4-b0dc-f9678c000d8b",
                "d68f7078-8ea3-41b1-85a1-01af5ee0cfc8", "d842028a-75da-422c-9f0f-41c33c1d71b0",
                "9c139a1c-40a4-4ed7-b6ff-378c6f6bc1ea"),
            [4] = new(
                "3c0ee3b4-0453-43c4-bce9-d7882c9ea4e9",
                "fd9b329c-2341-43b3-932e-6bea48ed4faf",
                "51bba89c-d583-4d34-a47f-a58bc84ccab8", "e288f879-5860-4d29-9c2d-7977ff03f0f6",
                "8ae2c43e-e695-44df-adf8-f61d34d2c74b", "9087e9ec-e219-4cec-b181-439d95374f0e",
                "fe7818f3-df41-466f-8a1a-162ef7267493",
                "7a31ca71-cc29-45c8-a698-fc5f9c65529b", "b07c8713-b294-46cd-93e4-d067e36f51a7",
                "aa2da90c-9325-44fc-ad8b-abae6f6dba6d", "c16ba7a4-9d23-4de9-ae84-b6074d3fc36e",
                "a6a20599-fc46-48f4-8a29-134c34548936", "6355ca3d-1bab-4251-896c-b82e8877a2f4",
                "7b4fb960-9150-4175-8fee-444a29266c03", "1317c9bb-e1bc-4294-9806-52bf4f8fe953",
                "463a5ea3-847f-4102-83e4-512eb0ca97ab"),
            [5] = new(
                "fea06901-f852-4221-a740-d801087e7846",
                "10238e3b-6a07-4fab-a632-64ecaaf13671",
                "d6afd6b2-2d82-4e08-b099-281037116261", "96225626-52ef-4d18-8972-60200d2042e5",
                "1a251fae-0933-4560-bfd5-a9b2181031c5", "bc8ee2ca-0453-4274-a56f-1dee95d24944",
                "a9c78e63-478e-48a0-9223-56d5aff54882",
                "155a5947-2cdf-4cda-b396-3e6ffaf9f362", "8188e093-047c-40d5-ae06-fb99d54530d4",
                "e35a165c-a306-466f-8e02-bb298d3b6197", "76031387-ccb3-4fc1-993e-283395b5fe2b",
                "76b62ae6-ebc3-4801-82f2-83290957446a", "d796541f-c6c8-4e4f-8313-10ba37f1814b",
                "e2c269f1-7d82-4e48-a029-ab65d8e122cd", "ce4d8cba-ac4f-4dcf-8276-48a4bf55984d",
                "21c62741-49c8-4da5-8b8d-440247911027"),
            [6] = new(
                "f2e95564-a78e-4ae2-8ba9-be8b72b260f6",
                "c437e9e4-946f-4738-92a2-cb0ad621b349",
                "78c4a18a-c212-4005-9619-aa08aa453a85", "ed4ff0de-ce9a-4a50-b256-4cdca572e792",
                "97acd02e-1821-4730-a8e2-2b8dd5e5091d", "3afa146d-1c07-4abb-a0b2-f17889bda00f",
                "5c735a25-ab19-46db-a281-3d895680f940",
                "263472e5-e4fa-43e2-b58b-0a30424320c4", "c5cbbc95-a4ba-461e-9d42-1c592f120b1a",
                "84cf84b9-ac29-4296-b9b9-96baaa48fc97", "8a3aac87-6501-41b8-9054-7abb69e07e05",
                "37191606-9653-48f8-8cb7-b72e54badc7b", "1f29a2cc-ec2d-438b-9c46-296d8d8bf9ec",
                "b15bb1da-cffa-4f8e-9287-15bcee3706aa", "97c516e9-8622-4b8d-849f-afc9164977ec",
                "5f119ac9-1018-4161-9b87-2cc23dba9c71"),
            [7] = new(
                "25bc2cf2-3ab5-418e-8301-ef34a74bd60e",
                "509c4971-4d29-450d-acd5-88050919df49",
                "4c4e84a4-dbdd-481d-a8f6-efd3362c1416", "98158279-f641-48c1-8439-f1688ef09a9d",
                "c16d9605-b12f-41ff-baf2-4d48f439f3f2", "02d251fd-0f9a-435d-8586-2197aeb97ea2",
                "6c08b7be-cd2e-40bc-9942-6d7328daad0f",
                "26c93c43-b10d-4387-b394-a373fc46bdc6", "8afdb8ac-d155-4c63-9a73-623f16075ab8",
                "5121dde6-9b5f-4895-b0bd-24675a2e3afa", "8e537c2d-edab-4b21-9ab8-09ef54f1ecb8",
                "9f7aa210-a89f-4b49-942e-44e0a9ea6eb1", "3c1d4212-f5df-4efa-bfa7-b6b1e51e68b7",
                "9c20be5e-3e19-4a33-9fd5-30c3e74a9eae", "087bb91d-add5-4ed1-a17e-31c6d10c843d",
                "49eaafb9-6645-4c04-9966-5190c4e8ca3d"),
            [8] = new(
                "8bdde70c-06bf-460d-bd37-ce9029cc01c0",
                "49da653a-012f-42c4-843d-5eaf14b6731e",
                "83ffe2e5-bd78-47c1-8b85-849ef5e184bf", "69c451b9-4e02-4c66-8943-f0b4769d90d4",
                "e7d6489d-dff8-446c-8a10-87136d598b15", "df455c81-06e7-4634-9165-9f2b039d14b2",
                "2ba5b572-a454-4af2-b1d7-0d96f08d6729",
                "40a89f9a-a41c-465c-a3ef-6c9ab5a07c8d", "a2c5cef6-f76f-46a8-afa0-82558e6ebf46",
                "1fd4765f-78c5-4514-a7bf-074ca9b75a85", "a43f9a3a-19cf-4c95-8124-1122fbcae655",
                "e0674a7b-81a0-480a-9c55-e18409378690", "607b221d-dbe7-4fe7-8d4f-c7b9774fb187",
                "34d48257-b16e-443c-940c-d322b47d00b9", "23f92d62-c827-44e8-aa9f-f1c4d98fab47",
                "922aa66e-e41c-41ad-94a2-adb84f356430"),
        };

    public static CampaignCrayburnRaceConfig Race(int race) =>
        Races.TryGetValue(race, out var config) ? config : Races[1];

    public static bool TryEncounter(int race, string encounterGuid, out CampaignCrayburnEncounterConfig encounter)
    {
        var config = Race(race);
        if (string.Equals(encounterGuid, config.CastleGateEncounter, StringComparison.OrdinalIgnoreCase))
        {
            encounter = new(
                "CastleGate", config.CastleGateEncounter, config.CastleGateConversation,
                config.CastleGateSuccessConversation, config.CastleGateFailConversation,
                "Drawbridge", "InnerBailey");
            return true;
        }

        if (string.Equals(encounterGuid, config.TowerGateEncounter, StringComparison.OrdinalIgnoreCase))
        {
            encounter = new(
                "TowerGate", config.TowerGateEncounter, config.TowerGateConversation,
                config.TowerGateSuccessConversation, config.TowerGateFailConversation,
                "InnerBailey", "PenworthTower");
            return true;
        }

        if (string.Equals(encounterGuid, config.PenworthTowerEncounter, StringComparison.OrdinalIgnoreCase))
        {
            encounter = new(
                "PenworthTower", config.PenworthTowerEncounter, config.PenworthTowerConversation,
                config.PenworthTowerSuccessConversation, config.PenworthTowerFailConversation,
                "TowerGate", null);
            return true;
        }

        encounter = null!;
        return false;
    }

    public static bool TryEncounterNode(int race, string node, out CampaignCrayburnEncounterConfig encounter)
    {
        var config = Race(race);
        if (string.Equals(node, "CastleGate", StringComparison.Ordinal))
            return TryEncounter(race, config.CastleGateEncounter, out encounter);
        if (string.Equals(node, "TowerGate", StringComparison.Ordinal))
            return TryEncounter(race, config.TowerGateEncounter, out encounter);
        if (string.Equals(node, "PenworthTower", StringComparison.Ordinal))
            return TryEncounter(race, config.PenworthTowerEncounter, out encounter);

        encounter = null!;
        return false;
    }
}

public sealed record CampaignCrayburnRaceConfig(
    string WatchTowerConversation,
    string DrawbridgeConversation,
    string CastleGateConversation,
    string CastleGateEncounter,
    string CastleGateSuccessConversation,
    string CastleGateFailConversation,
    string InnerBaileyConversation,
    string TowerGateConversation,
    string TowerGateEncounter,
    string TowerGateSuccessConversation,
    string TowerGateFailConversation,
    string PenworthTowerConversation,
    string PenworthTowerEncounter,
    string PenworthTowerSuccessConversation,
    string PenworthTowerFailConversation,
    string ReportConversation);

public sealed record CampaignCrayburnEncounterConfig(
    string Node,
    string Encounter,
    string OpeningConversation,
    string SuccessConversation,
    string FailConversation,
    string ReturnNode,
    string? NextNode);
