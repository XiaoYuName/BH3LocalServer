"""Build local shops from captured 9.1 IDs and compatible fixed-price reference goods."""
from pathlib import Path
import json
R=Path(__file__).resolve().parents[1];O=R/'src/BH3.Game/Operations';W=R/'evidence/lobby/shop-141'
ref=R/'../../LocalServer/千乐铃音_7.0/KianaBH/Resources/ExcelOutput'
items=json.loads((O/'catalog.json').read_text(encoding='utf8'))['items'];byid={x['id']:x for x in items if x['kind']!='avatar'}
goods={x['ID']:x for x in json.loads((ref/'ShopGoodsData.json').read_text(encoding='utf-8-sig'))}
rows=[];shops=[];seen=set()
names={1:'日常商店',5:'兑换商店',9:'战场宝库',16:'星石商店',17:'魔女回廊',18:'战场商店',20:'舰团终端',23:'乐土商店',27:'礼包商店'}
for cmd in [6701,6703]:
 for s in json.loads((W/f'captured-{cmd}.json').read_text(encoding='utf8'))['shopList']:
  valid=[]
  for g in s.get('goodsList',[]):
   d=goods.get(g['goodsId'])
   if not d or d['MCoinCost'] or d['IsAutoOpen'] or d['CouponType'] or d['GlobalMaxBuyTimes'] or d['ChooseDialogType']:continue
   if d['PriceRateID']!=10 and not (d['PriceRateID']==1 and d['MaxBuyTimes']==1):continue
   iid=d['ItemID'];item=byid.get(iid)
   if iid==100:item={'kind':'scoin','name':'金币'}
   if not item or item['kind'] not in ['weapon','stigmata','material','scoin']:continue
   if d['ItemNum']<=0 or d['ItemNum']>(100 if item['kind'] in ['weapon','stigmata'] else 1000000):continue
   costs=[]
   if d['HCoinCost']:costs.append(dict(kind='hcoin',id=101,num=d['HCoinCost']))
   for i in range(1,6):
    cid=d[f'CostItemId{i}'];num=d[f'CostItemNum{i}']
    if cid and num:costs.append(dict(kind={100:'scoin',101:'hcoin',102:'stamina'}.get(cid,byid.get(cid,{}).get('kind','unknown')),id=cid,num=num))
   if len(costs)!=1 or costs[0]['kind'] not in ['scoin','hcoin','material']:continue
   valid.append(g['goodsId'])
   if g['goodsId'] not in seen:
    seen.add(g['goodsId']);rows.append(dict(id=g['goodsId'],name=item['name'],item=dict(kind=item['kind'],id=iid,num=d['ItemNum'],level=max(1,d['ItemLevel'])),costs=costs,maxBuyTimes=d['MaxBuyTimes'],maxPerPurchase=max(1,min(d['BuyMultipleMax'],100)) if d['IsBuyMultiple'] else 1))
  if valid:
   s.update(goodsList=[],allGoodsIdList=[],beginTime=1,endTime=2147483647,scheduleChangeTime=2147483647,nextAutoRefreshTime=2147483647,manualRefreshTimes=0,maxManualRefreshTimes=0,nextRefreshCost=0,freeManualRefreshTimes=0)
   shops.append(dict(id=s['shopId'],name=names.get(s['shopId'],s.get('shopName') or '本地商店'),mall=cmd==6703,template=s,goods=valid))
if not any(s['mall'] for s in shops):
 s=json.loads((W/'captured-6703.json').read_text(encoding='utf8'))['shopList'][0]
 s.update(goodsList=[],allGoodsIdList=[],beginTime=1,endTime=2147483647,scheduleChangeTime=2147483647,nextAutoRefreshTime=2147483647,manualRefreshTimes=0,maxManualRefreshTimes=0,nextRefreshCost=0,freeManualRefreshTimes=0)
 shops.append(dict(id=s['shopId'],name='本地兑换',mall=True,template=s,goods=[g['id'] for g in rows if g['costs'][0]['kind']=='hcoin'][:8]))
(O/'shops.json').write_text(json.dumps(dict(goods=rows,shops=shops),ensure_ascii=False,separators=(',',':')),encoding='utf8')
print('goods',len(rows),'shops',[(s['id'],len(s['goods'])) for s in shops])
