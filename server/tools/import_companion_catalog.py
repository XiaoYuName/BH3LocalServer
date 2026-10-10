"""Generate the local companion catalog; reference files and capture are read-only.
Missing official progression rows are explicit local rules, never fabricated provenance.
"""
import json, hashlib
from pathlib import Path
S=Path(__file__).resolve().parents[1]
R=S.parents[1]/'LocalServer/千乐铃音_7.0/KianaBH/Resources'
sources={}
def read(p):
    b=p.read_bytes();sources[str(p.resolve())]=hashlib.sha256(b).hexdigest();return json.loads(b.decode('utf-8-sig'))
def rows(n):return read(R/'ExcelOutput'/f'{n}.json')
text={x['value']['hash']:x['text'] for x in read(R/'TextMap/TextMapEN.json')}
defs=rows('Elf_AstraMate_Data'); skills=rows('ElfSkillData'); trees=rows('ElfSkillTreeData'); stars=rows('ElfStarData'); fragments=rows('ElfFragmentData')
capture=read(Path('D:/Game/BH3/capture-20261009-131730-89d321/account-copy.json'))
owned=next(x['Parsed']['elfList'] for x in capture['Accounts'][0]['Responses'] if x['CommandId']==2101)
known={x['ElfID'] for x in defs}
for e in owned:
    if e['elfId'] not in known:
        defs.append(dict(ElfID=e['elfId'],PACCHBJEPIA=2,FullName={},MaxLevel=80,NOEIIKFKPCJ=1,MaxRarity=3,elfCardID=380000+e['elfId'],elfFragmentID=370000+e['elfId']))
out=[]
for d in defs:
    id=d['ElfID']; kind=d['PACCHBJEPIA']; refskills=[x for x in skills if id in x['ElfIDList']]
    captured=next((e for e in owned if e['elfId']==id),{})
    knownskills={x['ElfSkillID'] for x in refskills}
    for s in captured.get('skillList',[]):
        if s['skillId'] not in knownskills:refskills.append(dict(ElfSkillID=s['skillId'],MaxLv=s['skillLevel'],UnlockStar=1))
    spec=dict(id=id,name=text.get(d['FullName'].get('Hash'),f'协同者 #{id} · 9.1'),type=kind,maxLevel=d['MaxLevel'],initialStar=d['NOEIIKFKPCJ'],maxStar=d['MaxRarity'],card=d['elfCardID'],fragment=d['elfFragmentID'],duplicateFragments=30,skills=[],stars=[])
    for s in refskills:
        costs=[dict(level=t['elfSkillLv'],star=t['LevelUpStar'],elfLevel=t['NeedElfLevel'],prerequisites=[dict(id=p['SkillID'],level=p['Lv']) for p in t['LevelUpPreSkill']],materials=[dict(id=m['MaterialID'],num=m['Number']) for m in t['LevelUpMaterialList']]) for t in trees if t['ElfSkillID']==s['ElfSkillID']]
        local=not costs
        if local:
            costs=[dict(level=l,star=s['UnlockStar'],elfLevel=0,prerequisites=[],materials=[] if kind!=1 or l==1 else [dict(id=100,num=1000*l)]) for l in range(1,s['MaxLv']+1)]
        spec['skills'].append(dict(id=s['ElfSkillID'],maxLevel=s['MaxLv'],unlockStar=s['UnlockStar'],localRules=local,costs=costs))
    for star in range(spec['maxStar']+1):
        row=next((x for x in stars if x['ElfID']==id and x['Star']==star),None)
        spec['stars'].append(dict(star=star,fragments=row['UpgradeFragment'] if row else (100 if star==0 else 50*star),level=row['UpgradeElfLevel'] if row else 1,localRules=row is None))
    f=next((x for x in fragments if x['ElfID']==id),{})
    spec['returnMaterial']=f.get('ReturnMaterialID',3129);spec['returnNum']=f.get('ReturnNum',20);spec['returnMinStar']=f.get('ReturnMinStar',spec['maxStar']);spec['localFragmentRule']=not bool(f)
    if id==180:
        for step in spec['stars']:
            if step['star'] in [1,2]: step['fragments']=1 # 9.1 screenshots: S -> SS and SS -> SSS each cost one part.
    out.append(spec)
data=dict(companions=out,levels=[dict(level=x['Level'],exp=x['Exp']) for x in rows('ElfLevelData')],expMaterials=[dict(id=x['MaterialID'],exp=x['ProvideExp'],scoin=x['ScoinCost']) for x in rows('MaterialProvideElfExpData')])
(S/'src/BH3.Game/Operations/companions.json').write_text(json.dumps(data,ensure_ascii=False,separators=(',',':')),encoding='utf8')
(S/'src/BH3.Game/Operations/companion-sources.json').write_text(json.dumps(sources,ensure_ascii=False,indent=2),encoding='utf8')
print(f'{len(out)} companion definitions, {sum(len(x["skills"]) for x in out)} skills; capture account data excluded.')
