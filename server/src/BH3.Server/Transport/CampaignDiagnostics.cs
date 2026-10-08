using BH3.Game.Messaging;
using BH3.Protocol;
using BH3.Protocol.Messages;

namespace BH3.Server.Transport;

internal static class CampaignDiagnostics
{
    // Log only gameplay identifiers and committed outcomes; never tickets or battle checksum/key bytes.
    public static object? Describe(GamePacket request, DispatchResult result)
    {
        if (result.Replies.Count == 0) return null;
        byte[] body = result.Replies[0].Body;
        switch (request.CommandId)
        {
            case 43:
                var begin = StageBeginRsp.Parser.ParseFrom(body);
                return new { operation = "begin", stageId = begin.StageId, retcode = begin.Retcode.ToString(), progress = begin.Progress };
            case 45:
                var end = StageEndRsp.Parser.ParseFrom(body);
                return new { operation = "end", stageId = end.StageId, retcode = end.Retcode.ToString(), endStatus = end.EndStatus.ToString(),
                    progress = end.Progress, firstWin = end.IsFirstWin, exp = end.PlayerExpReward, scoin = end.ScoinReward, challenges = end.ChallengeList.Select(c => c.ChallengeIndex).ToArray() };
            case 114:
                var mission = GetMissionRewardRsp.Parser.ParseFrom(body);
                return new { operation = "mission-reward", retcode = mission.Retcode.ToString(), missions = mission.MissionIdList.ToArray() };
            case 458:
                var act = TakeStageActChallengeRewardRsp.Parser.ParseFrom(body);
                return new { operation = "act-reward", retcode = act.Retcode.ToString(), actId = act.ActId, indices = act.SuccChallengeNumIndexList.ToArray() };
            default: return null;
        }
    }
}
