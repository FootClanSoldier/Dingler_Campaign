extern alias HexGame;
using Dingler.Game.Campaign;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Shared.Campaign.Messages;

namespace Dingler.Game.Handlers.Request.Campaign;

/// <summary>
/// ServiceCampaign data type 110000. HEX wraps campaign JSON in the real
/// CampSysGeneral Request/Response types, so Dingler can use its normal ObjFmt decoder/encoder.
/// </summary>
[Authenticated]
public sealed class CampaignSystemRequestHandler : IRequestHandler<CampSysGeneral.Request, CampSysGeneral.Response>
{
    private readonly CampaignService _campaign;

    public CampaignSystemRequestHandler(CampaignService campaign)
    {
        _campaign = campaign;
    }

    public CampSysGeneral.Response HandleRequest(SessionContext context, CampSysGeneral.Request request)
    {
        return new CampSysGeneral.Response
        {
            Envelope = _campaign.HandleEnvelope(context, request.Envelope),
        };
    }
}
