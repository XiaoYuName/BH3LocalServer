"""Extract the supported chapter-one subset from the local reference ExcelOutput.

Does not invent missing drop-table rules. Random/legacy drop groups remain disabled.
Usage: python import_campaign.py <ExcelOutput directory>
"""
import hashlib
import json
from pathlib import Path
import sys

source = Path(sys.argv[1])
files = {}
def read(name):
    raw = (source / (name + '.json')).read_bytes()
    files[name] = hashlib.sha256(raw).hexdigest()
    return json.loads(raw.decode('utf-8-sig'))

stages = [s for s in read('StageData_Main') if 10101 <= s['levelId'] <= 10115 and s['type'] == 1 and s['difficulty'] == 1]
assert len(stages) == 15
stage_ids = {s['levelId'] for s in stages}
missions = [m for m in read('MissionData') if
            (m['type'] == 1 and m['finishWay'] == 10180 and m['finishParaInt'] in stage_ids)
            or m['id'] in [97001, 97002, 97003]]
acts = [a for a in read('ActChallengeData') if a['actId'] in {s['actId'] for s in stages} and a['difficulty'] == 1]
reward_ids = {m['rewardId'] for m in missions} | {c['rewardId'] for s in stages for c in s['challengeList']} | {a[f'RewardID{i}'] for a in acts for i in range(1,4)}
rewards = []
for r in read('RewardData'):
    if r['RewardID'] not in reward_ids: continue
    items = [dict(id=r[f'RewardItem{i}ID'], level=r[f'RewardItem{i}Level'], num=r[f'RewardItem{i}Num']) for i in range(1,7) if r[f'RewardItem{i}ID'] and r[f'RewardItem{i}Num']]
    rewards.append(dict(id=r['RewardID'], exp=r['RewardExp'], hcoin=r['RewardHCoin'], stamina=r['RewardStamina'],
                        scoin=sum(i['num'] for i in items if i['id'] == 100), items=[i for i in items if i['id'] != 100]))
assert len(rewards) == len(reward_ids)
payload = dict(schema=1, sourceFiles=files,
    stages=[dict(id=s['levelId'], chapter=s['chapterId'], act=s['actId'], level=s['unlockPlayerLevel'], cost=s['staminaCost'],
                 exp=s['staminaCost'], avatarExp=s['avatarExpReward'], scoin=s['scoinReward'],
                 previous=[p for p in s['preLevelID'] if p], challenges=[c['rewardId'] for c in s['challengeList']], lua=s['luaFile']) for s in stages],
    missions=[dict(id=m['id'], way=m['finishWay'], target=m['finishParaInt'], total=m['totalProgress'], reward=m['rewardId'],
                   step=(1 if m['id']==97001 else 2 if m['id'] in [97002,97003] else 0)) for m in missions],
    acts=[dict(id=a['actId'], thresholds=[a[f'challengeNum{i}'] for i in range(1,4)], rewards=[a[f'RewardID{i}'] for i in range(1,4)]) for a in acts],
    rewards=rewards,
    levels=[dict(level=r['level'], exp=r['exp'], stamina=r['stamina'], bonus=r['staminaBonus'], avatarLimit=r['avatarLevelLimit']) for r in read('PlayerLevelData')],
    avatarLevels=[dict(level=r['level'], exp=r['exp']) for r in read('AvatarLevelData')])
payload['sourceFiles'] = files
target=Path(__file__).resolve().parents[1]/'src/BH3.Game/Campaign/chapter-one.json'
target.parent.mkdir(parents=True,exist_ok=True)
target.write_text(json.dumps(payload,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(f'{target}: {len(stages)} stages, {len(missions)} missions, {len(acts)} acts')
