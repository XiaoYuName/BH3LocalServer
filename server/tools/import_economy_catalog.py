"""Import promotion rules and four screenshot-verified 9.1 supply bundles.

Read-only reference tables remain untouched. New avatar 21101 uses the captured
base weapon/dress/skill IDs; its card/fragment IDs follow the second-part ID range.
No unavailable SKU is silently treated as a successful zero-reward purchase.
"""
import json
from pathlib import Path
R = Path(__file__).resolve().parents[1]
O = R / 'src/BH3.Game/Operations'
REF = R / '../../LocalServer/千乐铃音_7.0/KianaBH/Resources/ExcelOutput'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
write = lambda p, d: p.write_text(json.dumps(d, ensure_ascii=False, separators=(',', ':')), encoding='utf8')
avatars = read(REF/'AvatarData.json')
rules = read(REF/'AvatarStarType.json')
write(O/'avatar-promotion.json', dict(
    avatars=[dict(id=a['avatarID'], type=a['avatarType'], rankType=int(a['avatarStarUpType']['Hash'] != 0)) for a in avatars] + [dict(id=21101,type=0,rankType=1)],
    steps=[dict(star=r['Star'],subStar=r['SubStar'],type=r['avatarType'],rankType=r['avatarStarUpType'],cost=r['upgrade']) for r in rules]))
catalog = read(O/'catalog.json')
capture = read(Path('D:/Game/BH3/capture-20261009-131730-89d321/account-copy.json'))
responses = capture['Accounts'][0]['Responses']
av = {a['avatarId']:a for r in responses if r['CommandId']==25 for a in r['Parsed'].get('avatarList',[])}[21101]
eq = {w['uniqueId']:w for r in responses if r['CommandId']==27 for w in r['Parsed'].get('weaponList',[])}
spec = dict(kind='avatar',id=21101,name='梦照飞光',maxLevel=80,rarity=3,weapon=eq[av['weaponUniqueId']]['id'],dress=av['dressId'],fragment=2021101,card=3021101,skills=[s['skillId'] for s in av['skillList']])
catalog['items'] = [i for i in catalog['items'] if not (i['kind']=='avatar' and i['id']==21101)] + [spec]
for iid,name in [(spec['fragment'],'梦照飞光碎片'),(spec['card'],'梦照飞光角色卡')]:
    if not any(i['id']==iid and i['kind']=='material' for i in catalog['items']):
        catalog['items'].append(dict(kind='material',id=iid,name=name,maxLevel=1,rarity=3,weapon=0,dress=0,fragment=0,card=0,skills=[]))
write(O/'catalog.json',catalog)
shops = read(O/'shops.json')
for g in shops['goods']:
    # Gift coins are an in-game wallet even though the original currency is paid.
    if any(c['kind']=='mcoin' for c in g['costs']):g['localPayment']=False
    if g['id'] in [721562,721567]:
        equipment=g['id']==721567
        g.update(name='梦照飞光'+('装备' if equipment else '角色')+'礼包（Ⅰ）',
            rewards=[dict(kind='material',id=1102 if equipment else 1103,num=1)],
            costs=[dict(kind='mcoin',id=0,num=60)],maxBuyTimes=1,maxPerPurchase=1,receiptOnly=False,localPayment=False,
            sourceNote='Local definition: user 9.1 screenshot 60 Mcoin / 2.1 discount, captured sort 350/340, analogous single-ticket packs. Awaiting exact reward-detail confirmation.')
    if g['id'] in [721565,721566,721570,721571]:
        large=g['id'] in [721566,721571]
        equipment=g['id'] in [721570,721571]
        g.update(name='梦照飞光'+('装备' if equipment else '角色')+'礼包（'+('大' if large else '小')+'）',
            rewards=[dict(kind='hcoin',id=101,num=288 if large else 188),dict(kind='material',id=1102 if equipment else 1103,num=10 if large else 5)],
            costs=[dict(kind='mcoin',id=0,num=1980 if large else 980)],maxBuyTimes=3,receiptOnly=False,localPayment=False,
            sourceNote='User 9.1 screenshot + 6714 audit + captured sort order 280/278/271/266; contents/prices/limit verified.')
write(O/'shops.json',shops)
print('promotion rules',len(rules),'avatar 21101 restored; gift-coin costs repaired; 721565/721566/721570/721571 configured')
