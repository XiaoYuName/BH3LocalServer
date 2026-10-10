using BH3.Game.Messaging;
using BH3.Game.Operations;
using BH3.Game.Sessions;
using BH3.Protocol;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Lobby;
public sealed partial class LobbyHandlers
{
    private readonly ChallengeService challenges = new(store, clock ?? TimeProvider.System);
    private IEnumerable<IGameMessageHandler> CreateChallengeHandlers()
    {
        yield return Respond(527, 528, GetExBossRankReq.Parser, (s, r) => challenges.BossRank(Uid(s), r));
        yield return Respond(529, 530, ExBossStageBeginReq.Parser, (s, r) => challenges.BossBegin(Uid(s), r));
        yield return new Handler(531, SessionState.Authenticated, (s, p) => {
            uint uid = Uid(s); var (response, rewards) = challenges.BossEnd(uid, ExBossStageEndReq.Parser.ParseFrom(p.Body));
            var result = new List<GamePacket>();
            void Add(ushort cmd, IMessage value) => result.Add(GamePacketCodec.Reply(p, cmd, value.ToByteArray(), uid));
            Add(511, challenges.BossInfo(uid));
            if (rewards.Length > 0) Add(533, new TakeExBossScoreRewardNotify { RewardList = { rewards } });
            Add(532, response);
            if (response.Retcode == ExBossStageEndRsp.Types.Retcode.Succ) { Add(11, Main(s)); Add(27, Equipment(s, new())); }
            return result;
        });
        yield return Respond(5200, 5201, UltraEndlessGetTopRankReq.Parser, (s, r) => challenges.AbyssRank(Uid(s), r));
        yield return Respond(5211, 5212, UltraEndlessEnterSiteReq.Parser, (s, r) => challenges.EnterSite(Uid(s), r));
        yield return new Handler(5206, SessionState.Authenticated, (s, p) => {
            uint uid = Uid(s); var (response, rewards) = challenges.ReportFloor(uid, UltraEndlessReportSiteFloorReq.Parser.ParseFrom(p.Body));
            var result = new List<GamePacket>();
            void Add(ushort cmd, IMessage value) => result.Add(GamePacketCodec.Reply(p, cmd, value.ToByteArray(), uid));
            Add(5203, challenges.AbyssInfo(uid, profiles.Find(uid)!.Nickname)); Add(5207, response);
            if (response.Retcode == UltraEndlessReportSiteFloorRsp.Types.Retcode.Succ) Add(113, campaign.Missions(uid));
            return result;
        });
        yield return new Handler(5219, SessionState.Authenticated, (_, p) => { UltraEndlessClientReportNotify.Parser.ParseFrom(p.Body); return []; });
    }
}
