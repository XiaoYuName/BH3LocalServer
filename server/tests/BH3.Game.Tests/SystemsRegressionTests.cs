using BH3.Game.Operations;
using BH3.Game.Players;
using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed partial class SystemsTests
{
    [Fact] public void ActivityConfigAndMissionGroupsShareDatesAndSelectedIds()
    {
        var a=new LocalActivity(17001,"活动","完成任务",true,1,2145916800,Missions:[38169]);
        gm.Execute(Uid,new(Guid.NewGuid().ToString(),"activity",Activity:a));
        var cfg=systems.ActivityConfigs(Uid).Single(x=>x.ActivityId==17001);
        Assert.Equal(4u,cfg.ActivityType);Assert.Equal(new uint[]{38169},cfg.MissionIds);
        var groups=systems.ActivityMissions(Uid,new(){ActivityIdList={17001}}).MissionGroupList;
        Assert.Single(groups);Assert.Equal(38169u,Assert.Single(groups[0].MissionList).MissionId);
        gm.Execute(Uid,new(Guid.NewGuid().ToString(),"activity",Revision:1,Activity:a with{Enabled=false}));
        Assert.Equal(1u,systems.ActivityConfigs(Uid).Single(x=>x.ActivityId==17001).EndTime);
        Assert.Empty(systems.ActivityMissions(Uid,new(){ActivityIdList={17001}}).MissionGroupList);
    }
    [Fact] public void PassLevelPurchaseUsesRemainingExperienceAndRejectsUnderquotedPrice()
    {
        systems.Pass(Uid);store.Campaign(Uid,tx=>{tx.Campaign.Systems.PassExp=1;return 0;});
        Assert.NotEqual(BuyBattlePassLevelRsp.Types.Retcode.Succ,systems.BuyLevel(Uid,new(){TargetLevel=2,HcoinCost=199}).Retcode);
        Assert.Equal(10000u,store.Read(Uid).Hcoin);
        Assert.Equal(BuyBattlePassLevelRsp.Types.Retcode.Succ,systems.BuyLevel(Uid,new(){TargetLevel=2,HcoinCost=200}).Retcode);
        Assert.Equal(9800u,store.Read(Uid).Hcoin);Assert.Equal(2u,systems.Pass(Uid).Level);
    }
    [Fact] public void GrandKeyLevelRequiresMaterialsThenDebitsOnceAndPersists()
    {
        Grant(new GrantItem("weapon",20590,1,30));
        Assert.Equal(1u,systems.GrandKeys(Uid,new(){KeyIdList={201}}).KeyList[0].Level);
        Assert.NotEqual(GrandKeyLevelUpRsp.Types.Retcode.Succ,systems.KeyLevel(Uid,new(){KeyId=201}).Retcode);
        Assert.Equal(10000000u,store.Read(Uid).Scoin);
        Grant(new GrantItem("material",2008,1));Assert.Equal(GrandKeyLevelUpRsp.Types.Retcode.Succ,systems.KeyLevel(Uid,new(){KeyId=201}).Retcode);
        Assert.Equal(9972000u,store.Read(Uid).Scoin);Assert.Equal(0u,store.Campaign(Uid,tx=>tx.Campaign.Materials[2008]));
        Assert.Equal(2u,new SystemsService(new LobbyStore(db),clock).GrandKeys(Uid,new(){KeyIdList={201}}).KeyList[0].Level);
        Assert.Equal(GrandKeyUnlockSkillRsp.Types.Retcode.Succ,systems.KeyUnlock(Uid,new(){SkillList={new GrandKeySkill{KeyId=201,SkillId=20102}}}).Retcode);
        Assert.Contains(20102u,systems.GrandKeys(Uid,new(){KeyIdList={201}}).KeyList[0].UnlockSkillList);
    }
    [Fact] public void GrandKeyTuneKeepsUniqueIdAndRejectsRepeat()
    {
        Grant(new GrantItem("weapon",20329));uint id=store.Campaign(Uid,tx=>GrantService.Inventory(tx).Equipment.WeaponList.Single(x=>x.Id==20329).UniqueId);
        Assert.Equal(GrandKeyContrastRsp.Types.Retcode.Succ,systems.KeyTune(Uid,new(){UniqueId=id}).Retcode);
        Assert.Equal(29001u,store.Campaign(Uid,tx=>GrantService.Inventory(tx).Equipment.WeaponList.Single(x=>x.UniqueId==id).Id));
        Assert.NotEqual(GrandKeyContrastRsp.Types.Retcode.Succ,systems.KeyTune(Uid,new(){UniqueId=id}).Retcode);
    }
    [Fact] public void CollectionScoreDeduplicatesCopiesAndEvolutionVariants()
    {
        uint before=store.Campaign(Uid,SystemsService.CollectionScore);
        Grant(new GrantItem("weapon",20001,2));Assert.Equal(before,store.Campaign(Uid,SystemsService.CollectionScore));
        store.Campaign(Uid,tx=>{var inv=GrantService.Inventory(tx);inv.Avatars.AvatarList[0].Star=5;GrantService.Save(tx,inv.Avatars,inv.Equipment,Uid);return 0;});
        Assert.Equal(before,store.Campaign(Uid,SystemsService.CollectionScore));
        Grant(new GrantItem("stigmata",30001));uint first=store.Campaign(Uid,SystemsService.CollectionScore);
        Grant(new GrantItem("stigmata",30001,2));Assert.Equal(first,store.Campaign(Uid,SystemsService.CollectionScore));
        Grant(new GrantItem("stigmata",30003));Assert.Equal(first+3,store.Campaign(Uid,SystemsService.CollectionScore));
    }
    [Fact] public void Free65GiftRejectsUnderleveledAccountWithoutUsingLimit()
    {
        store.Campaign(Uid,tx=>{tx.Lobby=tx.Lobby with{Level=64};return 0;});
        uint shop=GmService.ShopCatalog.Shops.First(x=>x.Goods.Contains(700597u)).Id;
        Assert.NotEqual(BuyGoodsRsp.Types.Retcode.Succ,gm.BuyGoods(Uid,new(){ShopId=shop,GoodsId=700597}).Retcode);
        Assert.False(store.Campaign(Uid,tx=>tx.Campaign.Materials.ContainsKey(6004)));
        store.Campaign(Uid,tx=>{tx.Lobby=tx.Lobby with{Level=65};return 0;});
        Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,gm.BuyGoods(Uid,new(){ShopId=shop,GoodsId=700597}).Retcode);
    }
    [Fact] public void InvalidGodKeyBatchDoesNotDebitOrActivateAnyKey()
    {
        store.Campaign(Uid,tx=>{tx.Campaign.Systems.GrandKeys[201]=new GrandKey{Id=201,Level=10}.ToByteArray();return 0;});
        var r=systems.KeyActivate(Uid,new(){KeyList={new GrandKeySkill{KeyId=201,SkillId=20110,LastTime=10800},new GrandKeySkill{KeyId=999,SkillId=99901,LastTime=10800}}});
        Assert.NotEqual(GrandKeyActivateSkillRsp.Types.Retcode.Succ,r.Retcode);Assert.Equal(10000000u,store.Read(Uid).Scoin);
        Assert.Equal(0u,systems.GrandKeys(Uid,new()).KeyList[0].EndTime);
    }
}
