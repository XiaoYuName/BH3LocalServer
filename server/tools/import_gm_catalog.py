"""Build the local GM catalog from the read-only reference and captured inventory.
Names are reference EN text where CN resources are unavailable; IDs remain visible.
"""
import json, hashlib, shutil
from pathlib import Path
P = Path(__file__).resolve().parents[2]
R = P.parent / 'LocalServer/千乐铃音_7.0/KianaBH/Resources'
O = P/'server/src/BH3.Game/Operations'
O.mkdir(exist_ok=True)
files = {}
def read(path):
    b=path.read_bytes(); files[str(path.resolve())]=hashlib.sha256(b).hexdigest()
    return json.loads(b.decode('utf-8-sig'))
text = {r['value']['hash']:r['text'] for r in read(R/'TextMap/TextMapEN.json')}
def label(v, fallback):
    return text.get(v.get('Hash',v.get('hash')),fallback) if isinstance(v,dict) else fallback
items=[]
for kind,filename in [('weapon','WeaponData'),('stigmata','StigmataData'),('material','MaterialData'),('avatar','AvatarData')]:
    for r in read(R/'ExcelOutput'/f'{filename}.json'):
        id=r.get('ID',r.get('avatarID')); title=r.get('shortName') if kind=='avatar' else r.get('displayTitle')
        items.append(dict(kind=kind,id=id,name=label(title,f'{kind} #{id}'),maxLevel=r.get('maxLv',80) or 1,
            rarity=r.get('rarity',r.get('unlockStar',1)),weapon=r.get('initialWeapon',0),dress=r.get('DefaultDressId',0),
            fragment=r.get('avatarFragmentID',0),card=r.get('avatarCardID',0),skills=r.get('skillList',[])))
# Capture only avatar templates/IDs, never account identity, wallet or private mail content.
capture=read(Path('D:/Game/BH3/capture-20261009-131730-89d321/account-copy.json'))
responses=capture['Accounts'][0]['Responses']; avatars={}
for r in responses:
    if r['CommandId']==25:
        for a in r['Parsed'].get('avatarList',[]): avatars[a['avatarId']]=a
known={(x['kind'],x['id']) for x in items}
for r in responses:
    if r['CommandId']==27:
        for kind,key in [('weapon','weaponList'),('stigmata','stigmataList')]:
            for x in r['Parsed'].get(key,[]):
                if (kind,x['id']) not in known:
                    known.add((kind,x['id']));items.append(dict(kind=kind,id=x['id'],name=f'{kind} #{x["id"]} · 9.1',maxLevel=max(1,x.get('level',1)),rarity=0,weapon=0,dress=0,fragment=0,card=0,skills=[]))
sub=read(R/'ExcelOutput/AvatarSubSkillData.json')
captured_display=json.loads((P/'server/evidence/lobby/gm-140/captured-display.json').read_text(encoding='utf8'))
for pool in captured_display['gachaDisplayInfoList']:
    for kind,key in [('weapon','upWeaponList'),('stigmata','upStigmataList')]:
        for x in pool['commonData'].get(key,[]):
            if (kind,x['id']) not in known:
                known.add((kind,x['id']))
                items.append(dict(kind=kind,id=x['id'],name=f'{"武器" if kind=="weapon" else "圣痕"} #{x["id"]} · 9.1',maxLevel=max(1,x.get('level',1)),rarity=0,weapon=0,dress=0,fragment=0,card=0,skills=[]))
material_ids={x['id'] for x in items if x['kind']=='material'}
for avatar in list(items):
    if avatar['kind']=='avatar' and avatar['fragment'] and avatar['fragment'] not in material_ids:
        material_ids.add(avatar['fragment'])
        items.append(dict(kind='material',id=avatar['fragment'],name=avatar['name']+' · 碎片',maxLevel=1,rarity=avatar['rarity'],weapon=0,dress=0,fragment=0,card=0,skills=[]))
(O/'catalog.json').write_text(json.dumps(dict(items=items,subskills=[dict(id=x['avatarSubSkillId'],skill=x['skillId'],maxLevel=x['maxLv']) for x in sub]),ensure_ascii=False,separators=(',',':')),encoding='utf8')
display=captured_display
display['gachaDisplayInfoList']=[x for x in display['gachaDisplayInfoList'] if x['gachaType'] in ['GACHA_ADVENTURE','GACHA_PJMS_AVATAR_1','GACHA_PJMS_EQUIP_1']]
for x in display['gachaDisplayInfoList']:
    c=x['commonData'];c.pop('freeInfoList',None);c['contentUrl']='';ext=json.loads(c.get('displayExt') or '{}');c['displayExt']=json.dumps({k:v for k,v in ext.items() if 'link' not in k.lower() and 'url' not in k.lower() and not str(v).startswith(('http:','https:'))},separators=(',',':'));c['title']='本地补给';c['content']='本地自定义奖池，概率与保底以本地配置为准。'
    c['dataBeginTime']=1;c['dataEndTime']=2147483647
(O/'display.json').write_text(json.dumps(display,ensure_ascii=False),encoding='utf8')
web=P/'server/src/BH3.Server/wwwroot/gm';web.mkdir(parents=True,exist_ok=True)
ref=Path('D:/AssetsStudio/DNF_115/Tools/GM/wwwroot')
for name in ['style.css','gm110.css','css/workspace.css']:
    src=ref/name; dst=web/name;dst.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(src,dst);files[str(src)]=hashlib.sha256(src.read_bytes()).hexdigest()
(O/'sources.json').write_text(json.dumps(files,ensure_ascii=False,indent=2),encoding='utf8')
print(f'Catalog: {len(items)} items; three captured display templates; 115CN styles copied.')
