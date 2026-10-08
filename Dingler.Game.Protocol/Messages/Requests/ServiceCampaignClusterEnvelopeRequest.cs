extern alias HexGame;
using HexGame::Game.Shared.Cluster;

namespace Dingler.Game.Protocol.Messages.Requests;

/// <summary>
/// Target-specific wrapper for the ClusterComms shared-service pick handshake.
/// ClusterComms.EnvelopeS is shared by multiple services, so only requests whose
/// HConnect header targets ServiceCampaign/Shared are wrapped as this type.
/// </summary>
public sealed record ServiceCampaignClusterEnvelopeRequest(ClusterComms.EnvelopeS Envelope);
