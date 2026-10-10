using BH3.Game.Messaging;
using BH3.Game.Operations;
using BH3.Protocol.Messages;

namespace BH3.Game.Lobby;

public sealed partial class LobbyHandlers
{
    private readonly CompanionService companions=new(store);
    private IEnumerable<IGameMessageHandler> CreateCompanionHandlers()
    {
        yield return Respond(2100,2101,GetElfDataReq.Parser,(s,_)=>companions.Get(Uid(s)));
        yield return Change(2105,2106,ElfStarUpReq.Parser,companions.StarUp);
        yield return Change(2107,2108,AddElfExpByMaterialReq.Parser,companions.AddExp);
        yield return Change(2121,2122,ElfFragmentTransformReq.Parser,companions.Transform);
        yield return Change(2123,2124,ElfSkillLevelUpReq.Parser,companions.SkillUp);
        // These new commands live in Partthree, not Partelf, in client 9.1.
        yield return Change(1742,1743,SwitchElfSkillReq.Parser,companions.Switch);
        // Local accounts have no pre-rework debt. Never invent a compensation grant.
        yield return Respond(2125,2126,ElfTakeCompensationReq.Parser,(_,_)=>new ElfTakeCompensationRsp{Retcode=ElfTakeCompensationRsp.Types.Retcode.HasTake});
    }
}
