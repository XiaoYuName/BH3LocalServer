"""Import local gameplay definitions; capture contributes IDs only, never player progress."""
import hashlib,json
from pathlib import Path
R=Path(__file__).resolve().parents[1];P=R.parent.parent/'LocalServer/千乐铃音_7.0/KianaBH/Resources/ExcelOutput';W=R/'evidence/lobby/gameplay-150';sources={}
def read(n):
 p=P/(n+'.json');b=p.read_bytes();sources[n]=hashlib.sha256(b).hexdigest();return json.loads(b.decode('utf-8-sig'))
old=json.loads((R/'src/BH3.Game/Campaign/chapter-one.json').read_text());raw=read('StageData_Main');stages=[s for s in raw if s['type']==1 and 0<s['chapterId']<100];ids={s['levelId'] for s in stages}
assert all(p==0 or p in ids for s in stages for p in s['preLevelID'])
trial={x['stageID']:x for x in read('LevelTrialData')};samples={x['ID']:x['avatarID'] for x in read('AvatarTrialData')}
acts=[a for a in read('ActChallengeData') if (a['actId'],a['difficulty']) in {(s['actId'],s['difficulty']) for s in stages}]
missions=[m for m in read('MissionData') if (m['type']==1 and m['finishWay']==10180 and m['finishParaInt'] in ids) or m['id'] in [97001,97002,97003]]
rewards=read('RewardData');reward_index={x['RewardID']:x for x in rewards}
def reward(r):
 items=[dict(id=r[f'RewardItem{i}ID'],level=max(1,r[f'RewardItem{i}Level']),num=r[f'RewardItem{i}Num']) for i in range(1,7) if r[f'RewardItem{i}ID'] and r[f'RewardItem{i}Num']]
 return dict(id=r['RewardID'],exp=r['RewardExp'],hcoin=r['RewardHCoin'],stamina=r['RewardStamina'],scoin=sum(i['num'] for i in items if i['id']==100),items=[i for i in items if i['id']!=100])
reward_ids={m['rewardId'] for m in missions}|{c['rewardId'] for s in stages for c in s['challengeList']}|{a[f'RewardID{i}'] for a in acts for i in range(1,4)}
assert reward_ids-{0}<=reward_index.keys()
cap=json.loads((W/'captured-decoded.json').read_text());groups=next(x['data']['chapterGroupList'] for x in cap if x['command']==1661);chapter_ids={s['chapterId'] for s in stages};chapter_ref={x['chapterId']:x for x in read('ChapterData')};group_ref={x['ID']:x for x in read('ChapterGroupConfig')}
chapters=[]
for g in groups:
 for site in g['siteList']:
  id=site['chapterId']
  if id not in chapter_ids:continue
  definitions=[s for s in stages if s['chapterId']==id and s['difficulty']==1]
  last=chapter_ref.get(id,{}).get('chapterMaxLevelID') or max(s['levelId'] for s in definitions if s['isTrunk'])
  chapters.append(dict(id=id,group=g['id'],site=site['siteId'],level=group_ref.get(g['id'],{}).get('UnlockLevel',1),lastStage=last))
payload=dict(old,stages=[dict(id=s['levelId'],chapter=s['chapterId'],act=s['actId'],difficulty=s['difficulty'],level=s['unlockPlayerLevel'],cost=s['staminaCost'],exp=s['staminaCost'],avatarExp=s['avatarExpReward'],scoin=s['scoinReward'],previous=[p for p in s['preLevelID'] if p],challenges=[c['rewardId'] for c in s['challengeList']],lua=s['luaFile'],trials=[i for i in trial.get(s['levelId'],{}).get('avatarList',[]) if i in samples]) for s in stages],missions=[dict(id=m['id'],way=m['finishWay'],target=m['finishParaInt'],total=m['totalProgress'],reward=m['rewardId'],step=1 if m['id']==97001 else 2 if m['id'] in [97002,97003] else 0) for m in missions],acts=[dict(id=a['actId'],difficulty=a['difficulty'],thresholds=[a[f'challengeNum{i}'] for i in range(1,4)],rewards=[a[f'RewardID{i}'] for i in range(1,4)]) for a in acts],rewards=[reward(reward_index[i]) for i in sorted(reward_ids) if i],chapters=chapters,trials=[dict(id=k,avatar=v) for k,v in samples.items()])
payload['sourceFiles']=sources.copy();(R/'src/BH3.Game/Campaign/story.json').write_text(json.dumps(payload,ensure_ascii=False,separators=(',',':')),encoding='utf8')
plots={x['plotID']:dict(id=x['plotID'],stage=x['levelID'],firstDialog=x['startDialogID'],lastDialog=x['endDialogID']) for x in read('PlotData') if x['levelID'] in ids}
payload['plots']=list(plots.values())
# Captured 9.1 boss families with reference difficulty/score definitions.
boss_rows=read('ExBossMonsterData');bosses=[]
tiers=[dict(id=x['ID'],min=x['levelLimitMin'],max=x['levelLimitMax'],dailyEntries=x['challengeTimes']) for x in read('ExBossConfig')]
for tier in tiers:
 for family in [490,360,90]:
  rows=[x for x in boss_rows if x['ConfigID']==tier['id'] and x['BossGroupId']==family]
  for level in sorted({x['MonsterLevel'] for x in rows}):
   x=min((x for x in rows if x['MonsterLevel']==level),key=lambda x:x['BossId'])
   bosses.append(dict(id=x['BossId'],family=family,rank=tier['id'],level=level,maxScore=x['MonsterBaseScore']+x['ExtraTimeScore']))
score_rewards=[dict(id=x['ID'],score=x['score'],reward=x['rewardId'],rank=x['configId']) for x in read('ExBossScoreReward') if x['configId'] in [101,102,103,104] and x['scheduleId']==0]
site_rows=[s for s in read('UltraEndlessSite') if 1011<=s['SiteID']<=1014];floors=read('UltraEndlessFloor')
sites=[dict(id=s['SiteID'],stage=s['StageID'],previous=s['PreSiteList'],floors=[dict(id=f['FloorID'],need=f['NeedScore'],maxScore=f['MaxScore']) for f in floors if f['StageID']==s['StageID']]) for s in site_rows]
assert len(sites)==4 and all(s['floors'] for s in sites)
mode_rewards={r['reward'] for r in score_rewards}|{15399,15400,15401}
modes=dict(bossSchedule=10477,rank=104,tiers=tiers,bosses=bosses,bossRewards=score_rewards,abyssSchedule=1028,sites=sites,abyssRewards=[dict(id=86002,score=0,reward=15399),dict(id=86003,score=12000,reward=15400),dict(id=86004,score=15000,reward=15401)],rewards=[reward(reward_index[i]) for i in sorted(mode_rewards)],sourceFiles=sources)
payload['extraStages']=[dict(id=s['levelId'],chapter=0,act=0,difficulty=1,level=81,cost=0,exp=0,avatarExp=0,scoin=0,previous=[],challenges=[],lua=s['luaFile'],trials=[]) for s in raw if s['levelId'] in {x['stage'] for x in sites}]
payload['sourceFiles']=sources.copy()
(R/'src/BH3.Game/Campaign/story.json').write_text(json.dumps(payload,ensure_ascii=False,separators=(',',':')),encoding='utf8')
(R/'src/BH3.Game/Operations/gameplay.json').write_text(json.dumps(modes,ensure_ascii=False,separators=(',',':')),encoding='utf8')
coverage=dict(stages=len(stages),chapters=len(chapters),acts=len(acts),missions=len(missions),bosses=len(bosses),abyssSites=len(sites),missingModernChapterIds=sorted({s['chapterId'] for g in groups for s in g['siteList']}-chapter_ids),scope='Reference-defined ordinary stages; modern PJMS/open-world chapter systems require separate state machines. Static local abyss schedule; not a live official season.')
blocked={s['levelId'] for s in stages if s['unlockPlayerLevel']>88};old_count=-1
while old_count!=len(blocked):
 old_count=len(blocked);blocked.update(s['levelId'] for s in stages if any(p in blocked for p in s['preLevelID']))
coverage.update(retiredLevel99=sum(s['unlockPlayerLevel']>88 for s in stages),blockedByRetiredPrerequisites=len(blocked),reachableStages=len(stages)-len(blocked),normalStages=sum(s['difficulty']==1 for s in stages),plots=len(plots))
(W/'catalog-coverage.json').write_text(json.dumps(coverage,indent=2),encoding='utf8');print(coverage)
