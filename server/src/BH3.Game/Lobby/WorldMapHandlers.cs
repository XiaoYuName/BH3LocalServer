using BH3.Game.Operations;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Lobby;

public sealed partial class LobbyHandlers
{
    private static GetWorldMapDataRsp MainStoryWorldMap()
    {
        var result=JsonParser.Default.Parse<GetWorldMapDataRsp>(SystemsService.CapturedLobby["worldMap"].GetRawText());
        foreach(var entry in result.WorldMapList)
        {
            entry.AdvanceTime=1;entry.BeginTime=1;entry.EndTime=int.MaxValue;
        }
        return result;
    }
    private static GetWorldMapRecommendRsp MainStoryRecommendation()
    {
        var result=JsonParser.Default.Parse<GetWorldMapRecommendRsp>(SystemsService.CapturedLobby["recommend"].GetRawText());
        var scheduled=MainStoryWorldMap().WorldMapList.Select(x=>x.WorldMapId).ToHashSet();
        // The capture advertises future entry 2386 but has no schedule for it.
        // Only resolve recommendations against actual entry containers.
        foreach(var entry in result.ActivityRecommendList.Where(x=>!scheduled.Contains(x.WorldMapId)).ToArray())
            result.ActivityRecommendList.Remove(entry);
        result.PermanentRecommendList.Add(new WorldMapRecommend{WorldMapId=2,Weight=60});
        return result;
    }
}
