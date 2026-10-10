using BH3.Game.Operations;
using BH3.Game.Messaging;
using BH3.Game.Sessions;
using BH3.Protocol;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Lobby;

public sealed partial class LobbyHandlers
{
    private readonly GmService operations=new(store,clock);
    private IEnumerable<IGameMessageHandler> CreateOperationsHandlers()
    {
        yield return Respond(3800,3801,GetClientMailDataReq.Parser,(s,r)=>operations.Mail(Uid(s),r));
        yield return Change(3802,3803,TakeClientMailAttachmentReq.Parser,operations.TakeMail);
        yield return Respond(3804,3805,MarkReadClientMailReq.Parser,(s,r)=>operations.ReadMail(Uid(s),r));
        yield return Respond(3806,3807,DelClientMailReq.Parser,(s,r)=>operations.DeleteMail(Uid(s),r));
        yield return Respond(3809,3810,SetClientMailFavoriteReq.Parser,(s,r)=>operations.FavoriteMail(Uid(s),r));
        yield return Respond(4702,4703,GetGachaDisplayReq.Parser,(s,r)=>operations.GachaDisplay(Uid(s),r));
        yield return Change(4700,4701,GachaReq.Parser,operations.Draw);
        yield return Change(4704,4705,BuyGachaTicketReq.Parser,operations.BuyTickets);
        yield return Respond(4706,4707,GetGachaLogReq.Parser,(s,_)=>operations.GachaLog(Uid(s)));
        yield return Respond(4708,4709,GetGachaProbReq.Parser,(s,r)=>operations.Probability(Uid(s),r));
        yield return Respond(6700,6701,GetShopListReq.Parser,(s,_)=>operations.Shops(Uid(s)));
        yield return Respond(6702,6703,GetShoppingMallListReq.Parser,(s,_)=>operations.ShoppingMall(Uid(s)));
        yield return Respond(6704,6705,GetSingleShopWithoutRefreshReq.Parser,(s,r)=>operations.SingleShop(Uid(s),r));
        yield return Respond(6708,6709,ManualRefreshShopReq.Parser,(_,_)=>new ManualRefreshShopRsp{Retcode=ManualRefreshShopRsp.Types.Retcode.NoNeedRefresh});
        yield return Change(6714,6715,BuyGoodsReq.Parser,operations.BuyGoods);
        yield return Change(6719,6720,GetVipRewardReq.Parser,operations.ClaimVip);
        yield return Change(29,30,AvatarStarUpReq.Parser,operations.PromoteAvatar);
        yield return Change(6723,6724,TakeCardProductDailyRewardReq.Parser,(uid,_)=>operations.TakeCardDaily(uid));
        yield return Change(6725,6726,TakeCardProductBonusRewardReq.Parser,(uid,_)=>operations.TakeCardBonus(uid));
        yield return Change(6733,6734,ExchangeHcoinByMcoinReq.Parser,operations.ExchangeMcoin);
        yield return Respond(6729,6730,GetProductRecommendListReq.Parser,(s,_)=>new GetProductRecommendListRsp{Retcode=GetProductRecommendListRsp.Types.Retcode.Succ,RecommendList={operations.ProductList(Uid(s)).ProductList.Select(p=>p.Id)}});
        yield return Payment(6731,6732,BuyProductReq.Parser,r=>r.Name,ok=>new BuyProductRsp{Retcode=ok?BuyProductRsp.Types.Retcode.Succ:BuyProductRsp.Types.Retcode.ProductInvalid});
        yield return Payment(1494,1495,CreateAlipayOrderReq.Parser,r=>r.ProductName,ok=>new CreateAlipayOrderRsp{Retcode=ok?CreateAlipayOrderRsp.Types.Retcode.Succ:CreateAlipayOrderRsp.Types.Retcode.Fail});
        yield return Payment(207,208,CreateWeiXinOrderReq.Parser,r=>r.Attach,ok=>new CreateWeiXinOrderRsp{Retcode=ok?CreateWeiXinOrderRsp.Types.Retcode.Succ:CreateWeiXinOrderRsp.Types.Retcode.Fail});
        yield return new Handler(6743,SessionState.Authenticated,(_,p)=>{ReportClickRechargeButtonNotify.Parser.ParseFrom(p.Body);return [];});
    }
    private IGameMessageHandler Payment<T>(ushort command,ushort reply,MessageParser<T> parser,Func<T,string> name,Func<bool,IMessage> response) where T:IMessage<T>
        =>new Handler(command,SessionState.Authenticated,(session,packet)=>
        {
            uint uid=Uid(session);RechargeFinishNotify notice;
            try{notice=operations.PurchaseProduct(uid,name(parser.ParseFrom(packet.Body)));}
            catch(Exception e) when(e is ArgumentException or OverflowException){notice=new(){Retcode=RechargeFinishNotify.Types.Retcode.Fail};}
            bool ok=notice.Retcode==RechargeFinishNotify.Types.Retcode.Succ;
            var result=new List<GamePacket>{GamePacketCodec.Reply(packet,reply,response(ok).ToByteArray(),uid)};
            if(ok){result.Insert(0,GamePacketCodec.Reply(packet,6718,operations.VipRewards(uid).ToByteArray(),uid));result.Add(GamePacketCodec.Reply(packet,11,Main(session).ToByteArray(),uid));result.Add(GamePacketCodec.Reply(packet,6707,operations.ProductList(uid).ToByteArray(),uid));result.Add(GamePacketCodec.Reply(packet,6722,operations.CardInfo(uid).ToByteArray(),uid));result.Add(GamePacketCodec.Reply(packet,6742,notice.ToByteArray(),uid));}
            return result;
        });
    public IReadOnlyList<GamePacket> GmNotifications(GameSession s,bool newMail)
    {
        var request=new GamePacket(new byte[GamePacketCodec.PrefixSize],0,[],[]);
        GamePacket Packet(ushort command,IMessage body)=>GamePacketCodec.Reply(request,command,body.ToByteArray(),Uid(s));
        var list=new List<GamePacket>{Packet(11,Main(s)),Packet(25,Avatars(s,new())),Packet(27,Equipment(s,new())),Packet(4703,operations.GachaDisplay(Uid(s),new(){IsAll=true}))};
        var elf=companions.Get(Uid(s));
        list.Add(Packet(2102,new SyncElfDataNotify{ElfList={elf.ElfList}}));list.Add(Packet(2103,new SyncElfFragmentNotify{ElfFragmentList={elf.ElfFragmentList}}));
        list.Add(Packet(113,campaign.Missions(Uid(s))));
        list.Add(Packet(6701,operations.Shops(Uid(s))));list.Add(Packet(6703,operations.ShoppingMall(Uid(s))));
        if(newMail)list.Add(Packet(3808,new NewClientMailNotify{Type=(ClientMailType)1}));
        return list;
    }
}
