"""Independent protobuf wire scenarios over the original client KCP DLL; isolated accounts only."""
import uuid
import json
from pathlib import Path

def observe(peer,label,uid,fields,integer,blob,gm,contexts,restart=False):
    checks=[]
    def request(cmd,body=b''):
        reply=peer.request(cmd,body,timeout=10)
        return reply[1] if reply and reply[0]==cmd+1 and reply[2]==uid else {}
    def check(name,passed):
        checks.append(dict(name=f'{label}: {name}',passed=bool(passed),response_hex=peer.last_response_body.hex(),response_ids=list(peer.last_response_ids)))
    def score(data,number):return fields(data[number][0]).get(4,[0])[0] if data.get(number) else 0
    def arena_score():return score(request(527),2)
    def abyss_score():return score(request(5200,integer(1,1028)),3)
    if restart:
        before=contexts[uid]
        check('arena best score persists',arena_score()==before['arena'])
        check('abyss best score persists',abyss_score()==before['abyss'])
        wallet=request(10);check('challenge wallet persists',wallet.get(5)==before['wallet'].get(5) and wallet.get(6)==before['wallet'].get(6))
        result=request(114,b''.join(integer(1,i) for i in [86002,86003,86004]));check('period mission cannot be reclaimed after restart',result.get(1)!=[0])
        stages=request(41,integer(1,10201));check('later chapter completion persists',len(stages.get(2,[]))==1 and fields(stages[2][0]).get(16)==[1])
        check('plot bookmark persists',20001 in request(1382).get(2,[]))
        return checks
    gm(uid,dict(requestId=str(uuid.uuid4()),action='captain',level=88))
    gm(uid,dict(requestId=str(uuid.uuid4()),action='grant',items=[dict(kind='stamina',id=0,num=999)]))
    main=request(5202)
    check('abyss entry contains settlement object for RefreshCupLevel',main.get(1)==[0] and len(main.get(7,[]))==1)
    settlement=fields(main[7][0]) if main.get(7) else {}
    check('initial settlement has no historical schedule',settlement.get(1,[0])==[0])
    rank_cache={}
    for schedule in [1028,0,999999]:
        rank=request(5200,integer(1,schedule) if schedule else b'')
        check(f'abyss rank echoes requested schedule {schedule}',rank.get(1)==[0] and rank.get(2,[0])==[schedule] and bool(rank.get(3)))
        rank_cache[rank.get(2,[0])[0]]=rank.get(3)
    check('current and previous rank caches both resolve',all(rank_cache.get(i) for i in [1028,0]))
    catalog=request(41);check('expanded catalog crosses KCP fragments',len(catalog.get(2,[]))==1314)
    groups=request(1660);check('all 15 ordinary chapter groups present',len(groups.get(2,[]))==15)
    ok=True
    for stage in list(range(10101,10116))+[10201]:
        ok &= request(43,integer(1,stage)+integer(2,101)).get(1)==[0]
        ok &= request(45,blob(1,integer(1,stage))+blob(2,f'gameplay-{uid}-{stage}')).get(1)==[0]
    check('full first chapter unlocks second chapter and settles',ok)
    plot=integer(3,20001)+integer(4,100)
    check('known story plot can finish',request(1378,plot).get(1)==[0])
    check('story plot retry is idempotent',request(1378,plot).get(1)==[0])
    check('story plot query returns bookmark',20001 in request(1382).get(2,[]))
    schedule=request(508);check('arena rank and schedule open',schedule.get(1)==[0] and schedule.get(7)==[104])
    bosses=request(510);check('three bosses available',len(fields(bosses[2][0]).get(4,[]))==3 if bosses.get(2) else False)
    begin_body=integer(1,101)+integer(2,49016)
    begin=request(529,begin_body);check('arena battle begins',begin.get(1)==[0] and bool(begin.get(2)))
    check('arena begin retry preserves reservation',request(529,begin_body)==begin)
    end_body=integer(4,49016)+integer(5,4294967295)
    end=request(531,end_body);check('omitted WIN settles arena and notifies rewards',end.get(1)==[0] and 533 in peer.last_response_ids and peer.last_response_ids.index(511)<peer.last_response_ids.index(532))
    best=arena_score();check('arena score bounded by boss maximum',best==2000)
    wallet=request(10);end=request(531,end_body);check('arena retry sends no duplicate reward notify',end.get(1)==[0] and 533 not in peer.last_response_ids)
    check('arena retry preserves wallet',request(10).get(6)==wallet.get(6))
    check('locked avatar cannot fight another boss',request(529,integer(1,101)+integer(2,36021)).get(1)!=[0])
    check('abyss rejects locked later area',request(5211,integer(1,1012)).get(1)!=[0])
    check('abyss missions cannot be claimed early',request(114,integer(1,86002)).get(1)!=[0])
    # Floor IDs and ceilings are checked-in reference input, not copied from server responses.
    data=json.loads((Path(__file__).resolve().parents[1]/'src/BH3.Game/Operations/gameplay.json').read_text())
    for site in data['sites']:
        check(f"abyss area {site['id']} opens",request(5211,integer(1,site['id'])).get(1)==[0])
        for floor in site['floors']:
            body=integer(1,site['id'])+integer(2,floor['id'])+integer(3,4294967295)+integer(4,60)+integer(5,180)+integer(6,101)
            check(f"abyss floor {site['id']}/{floor['id']} settles",request(5206,body).get(1)==[0] and 113 in peer.last_response_ids)
    total=abyss_score();check('all floor scores capped and retained',total==sum(f['maxScore'] for s in data['sites'] for f in s['floors']))
    claim=b''.join(integer(1,i) for i in [86002,86003,86004]);result=request(114,claim)
    check('three abyss missions grant through normal reward API',result.get(1)==[0] and len(result.get(3,[]))==3)
    check('mission retry cannot grant twice',request(114,claim).get(1)!=[0])
    contexts[uid]=dict(arena=best,abyss=total,wallet=request(10))
    return checks
