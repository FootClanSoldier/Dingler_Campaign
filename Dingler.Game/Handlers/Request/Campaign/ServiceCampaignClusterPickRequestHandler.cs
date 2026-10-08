extern alias HexGame;
using System.Text;
using System.Text.Json;
using Dingler.Game.Protocol.Messages.Requests;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Cluster;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Campaign;

/// <summary>
/// Resolves the client's ClusterComms shared-service pick for ServiceCampaign.
/// The original HEX assembly owns the response format via Pick.Res.Make; Dingler
/// only selects the local ServiceCampaign instance.
/// </summary>
[Authenticated]
public sealed class ServiceCampaignClusterPickRequestHandler
    : IRequestHandler<ServiceCampaignClusterEnvelopeRequest, ClusterComms.EnvelopeR>
{
    private readonly ILogger<ServiceCampaignClusterPickRequestHandler>? _logger;

    public ServiceCampaignClusterPickRequestHandler(
        ILogger<ServiceCampaignClusterPickRequestHandler>? logger = null)
    {
        _logger = logger;
    }

    public ClusterComms.EnvelopeR HandleRequest(
        SessionContext context,
        ServiceCampaignClusterEnvelopeRequest request)
    {
        var envelope = request.Envelope;
        var kind = ReadKind(envelope.Data);
        if (!string.Equals(kind, ClusterComms.RType_PickReq, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported ServiceCampaign ClusterComms message kind '{kind ?? "<null>"}'");
        }

        if (!Enum.TryParse<UID.Type>("ServiceCampaign", out var serviceType))
            throw new InvalidOperationException("HEX UID.Type does not define ServiceCampaign");

        // Verified: HEX expects a ServiceCampaign UID in Pick.Res. Assumption for this
        // first vertical slice: Dingler's single shared ServiceCampaign uses instance 0.
        // The original HEX assembly still owns the pickres serialization itself.
        var serviceId = new UID(serviceType, 0);
        var response = ClusterComms.Pick.Res.Make(serviceId);

        _logger?.LogInformation(
            "ClusterComms: {user} picked ServiceCampaign -> {serviceId}; response={response}",
            context.UserName,
            serviceId,
            response.Data is { Length: > 0 } ? Encoding.UTF8.GetString(response.Data) : "<empty>");

        return response;
    }

    private static string? ReadKind(byte[]? data)
    {
        if (data is null || data.Length == 0)
            return null;

        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty("Kind", out var kind)
                ? kind.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Malformed ServiceCampaign ClusterComms payload: {Encoding.UTF8.GetString(data)}",
                ex);
        }
    }
}
