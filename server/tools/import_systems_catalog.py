"""Build local gameplay definitions from the supplied reference tables and 9.1 evidence."""
import json
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
REF = ROOT.parents[1] / 'LocalServer/千乐铃音_7.0/KianaBH/Resources/ExcelOutput'
def table(name):
    return json.loads((REF / (name + '.json')).read_text(encoding='utf8'))
out = {n: table(n) for n in ['GrandKey','GrandKeyLevel','GrandKeyBuff','GrandKeyBuffActiveInfo','GrandKeyWeaponContrast','WikiCollectionRank','DutyDailyData','DutyWeeklyData','PlayerLevelShopGoods']}
out['Weapons'] = [dict(id=r['ID'], main=r['FFEKPEHADAK'], level=r['maxLv'], rarity=r['rarity'], maxRarity=r['maxRarity']) for r in table('WeaponData')]
out['Stigmata'] = [dict(id=r['ID'],main=r['NJOPKLNCIBM'],set=r['DJLKGGKHPAH'],part=r['baseType'],rarity=r['rarity'],maxRarity=r['maxRarity']) for r in table('StigmataData')]
out['WikiDresses'] = [dict(id=r['dressID'],rarity=r['rarity'],show=r['show']) for r in table('DressData')]
out['PhonePendants'] = table('PhonePendantData')
out['BpLevels'] = [r for r in table('BattlePassSeasonLevelConfig') if r['SeasonLevelComfigID'] == 34]
out['BpTypes'] = [r for r in table('BattlePassType') if r['TypeID'] in [10034,20034,30034]]
out['Missions'] = [r for r in table('MissionData') if r['type'] == 3]
reward_ids={r['Reward'] for r in out['WikiCollectionRank']}
reward_ids.update(r['rewardId'] for r in out['Missions']+out['DutyDailyData']+out['DutyWeeklyData'])
reward_ids.update(r[k] for r in out['BpLevels'] for k in ['BasicRewardID','AdvancedRewardID','EliteRewardID'])
out['Rewards'] = [r for r in table('RewardData') if r['RewardID'] in reward_ids]
# Probabilities for the treasure are server-side and absent from the capture.
# This local pool uses the guaranteed drop ID 249069 (1110 x1), not fabricated official odds.
out['Treasure'] = [dict(id=1110,num=1,weight=1),dict(id=3000,num=10,weight=3),dict(id=100,num=20000,weight=3),dict(id=8004,num=10,weight=3)]
out['Provenance'] = dict(reference=str(REF),battlePassTemplate=34,treasureOdds='local',gift65='client 9.1 screenshot; Source Prism 6004 / Ether Fuel 915')
(ROOT/'src/BH3.Game/Operations/systems.json').write_text(json.dumps(out,ensure_ascii=False,separators=(',',':')),encoding='utf8')
companions=ROOT/'src/BH3.Game/Operations/companions.json'
c=json.loads(companions.read_text(encoding='utf8'))
for r in c['companions']:
    if r['id']==180:
        # Both S -> SS and SS -> SSS costs verified from 9.1 UI.
        for step in r['stars']:
            if step['star'] in [1,2]: step['fragments']=1
companions.write_text(json.dumps(c,ensure_ascii=False,separators=(',',':')),encoding='utf8')
print('Imported systems; source 9.1 SS promotion requires one 370180 part.')
