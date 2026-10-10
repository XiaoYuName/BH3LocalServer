using BH3.Game.Operations;
using BH3.Game.Messaging;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Lobby;

public sealed partial class LobbyHandlers
{
    private readonly SystemsService systems = new(store,clock??TimeProvider.System);
    private GetPhonePendantDataRsp PhonePendants(uint uid,GetPhonePendantDataReq r)=>store.Campaign(uid,tx=>new GetPhonePendantDataRsp
    {Retcode=GetPhonePendantDataRsp.Types.Retcode.Succ,IsAll=RequestsAll(r.PhonePendantIdList),
        PhonePendantList={tx.Campaign.Systems.PhonePendants.Where(id=>RequestsAll(r.PhonePendantIdList)||r.PhonePendantIdList.Contains(id))
            .Order().Select(id=>new PhonePendant{Id=id,EndTime=2145916800})}});
    private IEnumerable<IGameMessageHandler> CreateSystemsHandlers()
    {
        yield return Change(251,252,UseMaterialReq.Parser,systems.Use);
        yield return Respond(500,501,GetDeleteMaterialReq.Parser,(_,_)=>new GetDeleteMaterialRsp{Retcode=GetDeleteMaterialRsp.Types.Retcode.Succ});
        yield return Respond(506,507,GetGrandKeyReq.Parser,(s,r)=>systems.GrandKeys(Uid(s),r));
        yield return Change(753,754,GrandKeyLevelUpReq.Parser,systems.KeyLevel);
        yield return Change(755,756,GrandKeyResetReq.Parser,systems.KeyReset);
        yield return Change(757,758,GrandKeyBreachReq.Parser,systems.KeyBreach);
        yield return Change(759,760,GrandKeyActivateSkillReq.Parser,systems.KeyActivate);
        yield return Change(761,762,GrandKeyContrastReq.Parser,systems.KeyTune);
        yield return Change(763,764,GrandKeySetSkillReq.Parser,systems.KeySelect);
        yield return Change(765,766,GrandKeyUnlockSkillReq.Parser,systems.KeyUnlock);
        yield return Respond(1193,1194,GetWikiDataReq.Parser,(s,_)=>systems.Wiki(Uid(s)));
        yield return Change(1195,1196,TakeWikiRatingRewardReq.Parser,systems.WikiClaim);
        yield return Respond(137,138,GetBulletinReq.Parser,(s,r)=>systems.Bulletins(Uid(s),r));
        yield return Respond(4321,4322,GetBulletinActivityMissionReq.Parser,(s,r)=>systems.ActivityMissions(Uid(s),r));
        yield return Respond(3750,3751,GetBattlePassReq.Parser,(s,_)=>systems.Pass(Uid(s)));
        yield return Change(3752,3753,BuyBattlePassTicketReq.Parser,systems.BuyTicket);
        yield return Change(3754,3755,TakeBattlePassLevelRewardReq.Parser,systems.PassClaim);
        yield return Change(3756,3757,BuyBattlePassLevelReq.Parser,systems.BuyLevel);
        yield return Change(3758,3759,TakeBattlePassPhaseExpReq.Parser,systems.PhaseExp);
        yield return Respond(3767,3768,GetBattlePassMissionPanelReq.Parser,(s,r)=>systems.Panel(Uid(s),r));
        yield return Change(288,289,TakeDutyRewardReq.Parser,systems.DutyClaim);
        yield return Respond(1626,1627,ClientCheckNetworkEnvReq.Parser,(_,r)=>new ClientCheckNetworkEnvRsp{Retcode=ClientCheckNetworkEnvRsp.Types.Retcode.Succ,TokenStr=r.TokenStr});
        yield return Respond(5831,5832,GetCollaborationScheduleReq.Parser,(_,_)=>new GetCollaborationScheduleRsp{Retcode=GetCollaborationScheduleRsp.Types.Retcode.Succ});
    }
}
