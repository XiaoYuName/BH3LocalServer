"""Restore the captured mall without inventing missing 9.1 reward definitions.

Existing ordinary shops are preserved. Unresolved goods retain their captured
client display but reject purchases until explicit rewards are configured.
Run import_economy_catalog.py afterward to apply the verified 9.1 supplements.
"""
import json
from pathlib import Path

R = Path(__file__).resolve().parents[1]
O = R / 'src/BH3.Game/Operations'
REF = R / '../../LocalServer/千乐铃音_7.0/KianaBH/Resources/ExcelOutput'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
goods = {x['ID']: x for x in read(REF / 'ShopGoodsData.json')}
packs = {x['giftPackID']: x for x in read(REF / 'GiftPackData.json')}
rewards = {x['RewardID']: x for x in read(REF / 'RewardData.json')}
items = {x['id']: x for x in read(O / 'catalog.json')['items'] if x['kind'] != 'avatar'}
dresses = {x['dressID']: x['avatarIDList'] for x in read(REF / 'DressData.json')}
(O / 'dresses.json').write_text(json.dumps(dresses,separators=(',',':')),encoding='utf8')

def item(i, n, level=1):
    kind = {100: 'scoin', 101: 'hcoin', 102: 'stamina'}.get(i, 'dress' if i in dresses else items.get(i, {}).get('kind'))
    if not kind: raise ValueError(f'unknown item {i}')
    return dict(kind=kind, id=i, num=n, level=max(1, level))

def reward(rid):
    r = rewards[rid]; result = []
    if r['RewardExp'] or r['RewardFriendPoint'] or r['RewardDutyPoint']: raise ValueError('unsupported reward currency')
    for key, i in [('RewardHCoin',101), ('RewardStamina',102)]:
        if r[key]: result.append(item(i,r[key]))
    for k in range(1,7):
        if r[f'RewardItem{k}ID'] and r[f'RewardItem{k}Num']:
            result.append(item(r[f'RewardItem{k}ID'],r[f'RewardItem{k}Num'],r[f'RewardItem{k}Level']))
    return result

catalog = read(O / 'shops.json')
mall = read(R / 'evidence/lobby/shop-141/captured-6703.json')['shopList'][0]
ids = [g['goodsId'] for g in mall['goodsList']]
rows = []; issues = []
for g in mall['goodsList']:
    gid = g['goodsId']; d = goods.get(gid); rs = []; reason = ''
    try:
        if not d: raise ValueError('9.1 goods definition unavailable in reference')
        iid = d['ItemID']; pack = packs.get(iid)
        if pack:
            if pack['extraRewardDays'] or pack['SelectableRewardList']: raise ValueError('daily/selectable pack requires explicit GM rewards')
            rs = reward(pack['immediateReward']) if pack['immediateReward'] else []
            rs = [x | {'num': x['num'] * d['ItemNum']} for x in rs]
        else: rs = [item(iid,d['ItemNum'],d['ItemLevel'])]
        if not rs: raise ValueError('empty reference reward')
    except (ValueError,KeyError) as e: rs = []; reason = str(e)
    costs = []
    if d:
        if d['MCoinCost']: costs.append(dict(kind='mcoin',id=0,num=d['MCoinCost']))
        elif d['HCoinCost']: costs.append(item(101,d['HCoinCost']))
        else:
            for k in range(1,6):
                if d[f'CostItemId{k}'] and d[f'CostItemNum{k}']:
                    costs.append(item(d[f'CostItemId{k}'],d[f'CostItemNum{k}']))
        if not d['MCoinCost']:
            for cost in costs:
                # Current captured discounts have integral prices. Refuse to guess rounding.
                discounted = cost['num'] * d['Discount']
                assert discounted % 10000 == 0, f'fractional price needs a client rounding fixture: {gid}'
                cost['num'] = discounted // 10000
    # A receipt-only product cannot debit a balance when it has no defined reward.
    display_item = dict(kind='receipt',id=d['ItemID'] if d else 0,num=d['ItemNum'] if d else 1,level=max(1,d['ItemLevel']) if d else 1)
    rows.append(dict(id=gid,name=items.get(display_item['id'],{}).get('name',f'商城商品 #{gid}'),item=display_item,
        rewards=rs,costs=costs,maxBuyTimes=d['MaxBuyTimes'] if d else 0,maxPerPurchase=1,
        localPayment=False,receiptOnly=not rs,sourceNote=reason,display=g))
    if reason: issues.append(dict(goodsId=gid,reason=reason))
mall.update(beginTime=1,endTime=2147483647,scheduleChangeTime=2147483647,nextAutoRefreshTime=2147483647)
catalog['goods'] = [g for g in catalog['goods'] if g['id'] not in ids] + rows
catalog['shops'] = [s for s in catalog['shops'] if s['id'] != mall['shopId']] + [dict(id=mall['shopId'],name='商城',mall=True,template=mall,goods=ids)]
(O / 'shops.json').write_text(json.dumps(catalog,ensure_ascii=False,separators=(',',':')),encoding='utf8')
(R / 'evidence/lobby/mall-142/catalog-coverage.json').write_text(json.dumps(dict(captured=len(ids),referenceDefinitions=sum(i in goods for i in ids),resolvedRewards=sum(bool(x['rewards']) for x in rows),unresolved=issues),ensure_ascii=False,indent=2),encoding='utf8')
print('mall',len(ids),'resolved rewards',sum(bool(x['rewards']) for x in rows),'receipt only',len(issues))
