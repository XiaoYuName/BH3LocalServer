"""Independent wire-level checks of local 9.1 systems; never starts the game."""
import uuid

def observe(peer,label,uid,fields,integer,blob,gm,contexts,restart=False):
    checks=[]
    def query(cmd,body=b''):
        reply=peer.request(cmd,body,timeout=10)
        assert reply and reply[0]==cmd+1 and reply[2]==uid,(cmd,reply)
        return reply[1]
    def check(name,passed):
        checks.append(dict(name=label+': '+name,passed=bool(passed),response_ids=list(peer.last_response_ids),response_hex=peer.last_response_body.hex()))
        assert passed,(name,peer.last_response_ids,peer.last_response_body.hex())
    def grant(items):
        gm(uid,dict(requestId=str(uuid.uuid4()),action='grant',items=items));query(500)
    def bag():return {fields(m)[1][0]:fields(m)[2][0] for m in query(26).get(4,[])}
    def keys():return {fields(m)[1][0]:fields(m) for m in query(506).get(2,[])}
    if restart:
        vip=query(6717)
        check('VIP claim survives restart',fields(vip[3][0]).get(7)==[2101,2201])
        check('VIP duplicate stays rejected',query(6719,integer(1,1)).get(1)==[3])
        luna=next(fields(a) for a in query(24).get(2,[]) if fields(a).get(1)==[507])
        check('Luna SS promotion survives restart',luna.get(2)==[4] and luna.get(19,[0])==[0])
        check('75 gift claim persists',query(6714,integer(1,27)+integer(2,700598)).get(1)!=[0])
        check('mirage schedule survives restart',fields(query(5790)[2][0]).get(1)==[5])
        check('god war lobby survives restart',fields(query(6150)[2][0]).get(9)==[2])
        check('collection claim persists',1 in query(1193).get(2,[]))
        check('treasure cannot be spent again',query(251,integer(1,3509)+integer(2,1)).get(1)==[2])
        check('grand key level persists',keys()[201].get(5)==[2])
        check('battle pass level persists',query(3750).get(3)==[3])
        check('daily claim stays closed',38169 in query(112).get(10,[]))
        check('GM activity persists',any(fields(x).get(1)==[17001] for x in query(110).get(50,[])))
        check('SSS promotion persists',any(fields(x).get(1)==[180] and fields(x).get(2)==[3] for x in query(2100).get(2,[])))
        check('captured activity disable persists',next(fields(x) for x in query(110).get(50,[]) if fields(x).get(1)==[6408]).get(4)==[1])
        check('single-ticket purchase persists',query(6714,integer(1,27)+integer(2,721562)).get(1)!=[0])
        return checks
    gm(uid,dict(requestId=str(uuid.uuid4()),action='captain',level=88));query(500)
    # 1.6.3: actual 9.1 Luna state, native hall artwork, cumulative local payment rewards.
    banners=[fields(b) for b in query(137).get(2,[]) if fields(b).get(2)==[2]]
    check('hall has exactly two native banners',len(banners)==2)
    check('hall banners have artwork and jump targets',all(b.get(6,[b''])[0].startswith(b'event/MainMenu/9.1_') and b'|' in b.get(8,[b''])[0] for b in banners))
    grant([dict(kind='avatar',id=507,num=1),dict(kind='material',id=10507,num=140)])
    for _ in range(3):assert query(29,integer(1,507)).get(1)==[0]
    luna=next(fields(a) for a in query(24).get(2,[]) if fields(a).get(1)==[507])
    check('Luna screenshot S3 fixture is prepared',luna.get(2)==[3] and luna.get(19)==[3])
    fragments=luna.get(5,[0])[0]
    check('Luna S3 to SS succeeds',query(29,integer(1,507)).get(1)==[0])
    luna=next(fields(a) for a in query(24).get(2,[]) if fields(a).get(1)==[507])
    check('Luna SS update debits exactly 25 fragments',luna.get(2)==[4] and luna.get(19,[0])==[0] and luna.get(5)==[fragments-25])
    vip=query(6717);total=vip.get(2,[0])[0]
    check('all thirteen native recharge tiers visible',len(vip.get(3,[]))==13)
    check('captured VIP claims were not imported',all(not fields(t).get(7) for t in vip[3]))
    query(6731,blob(1,b'Bh3MCoinTier1'))
    check('local payment publishes VIP data before callback',6718 in peer.last_response_ids and peer.last_response_ids.index(6718)<peer.last_response_ids.index(6732))
    check('cash equivalent increments by sixty',query(6717).get(2)==[total+60])
    coins=query(10).get(6,[0])[0]
    claim=query(6719,integer(1,1))
    check('VIP claim grants nonempty native rewards',claim.get(1)==[0] and len(claim.get(2,[]))==2)
    check('claim flags arrive before reward callback',6718 in peer.last_response_ids and peer.last_response_ids.index(6718)<peer.last_response_ids.index(6720))
    check('VIP first tier grants exact gold',query(10).get(6)==[coins+740000])
    check('VIP claimed rewards persisted',fields(query(6717)[3][0]).get(7)==[2101,2201])
    check('VIP repeat cannot grant twice',query(6719,integer(1,1)).get(1)==[3] and query(10).get(6)==[coins+740000])
    # Regression inputs from the user's 9.1 session: free 75 gift, 170199 lobby,
    # 6150/5790/5794. These exercise the published KCP dispatcher, not helpers.
    before_bag=bag();wallet=query(10)
    state=gm(uid);shop=next(s for s in state['shops'] if 700598 in s['goods'])
    claim=query(6714,integer(1,shop['id'])+integer(2,700598))
    check('75 gift returns quantity and committed limit',claim.get(1)==[0] and claim.get(7)==[1] and claim.get(4)==[1])
    after_bag=bag()
    check('75 gift grants exact screenshot rewards',after_bag.get(6004,0)-before_bag.get(6004,0)==2 and after_bag.get(915,0)-before_bag.get(915,0)==600)
    after=query(10)
    check('free 75 gift preserves currencies',all(wallet.get(f)==after.get(f) for f in [5,6,29]))
    check('75 gift repeat cannot grant',query(6714,integer(1,shop['id'])+integer(2,700598)).get(1)!=[0] and bag()==after_bag)
    god=query(6150)
    check('god war returns one mode with lobby and chapters',god.get(1)==[0] and len(god.get(2,[]))==1 and fields(god[2][0]).get(9)==[2] and len(fields(god[2][0]).get(14,[]))==3)
    check('god war rejects unknown selector',query(6150,integer(1,999)).get(1)!=[0])
    check('god war lobby request completes',query(6201,integer(1,1)+integer(2,2)).get(1)==[0])
    entry=query(43,integer(1,170199))
    check('god war 170199 lobby reservation succeeds',entry.get(1)==[0] and bool(entry.get(6)) and bool(entry.get(13)))
    check('lobby retry uses original reservation',query(43,integer(1,170199))==entry)
    end=blob(1,integer(1,170199)+integer(2,4))+blob(2,b'mode-entry-162')
    result=query(45,end)
    check('lobby exit succeeds without rewards',result.get(1)==[0] and not result.get(5) and not result.get(6))
    check('lobby exit retry is idempotent',query(45,end)==result)
    view=query(5790);activity=fields(view.get(2,[b''])[0])
    check('mirage native schedule 5 is open',view.get(1)==[0] and activity.get(1)==[5] and activity.get(2)==[11105])
    check('mirage has four current groups',activity.get(3)==[17,18,19,20] and len(activity.get(4,[]))==4)
    check('mirage does not import captured completion',all(fields(x).get(2,[0])==[0] for x in activity.get(4,[])))
    check('mirage refresh request succeeds',query(5794).get(1)==[0])
    missions=query(112)
    check('pass and duty arrive before mission callback',3751 in peer.last_response_ids and 969 in peer.last_response_ids and peer.last_response_ids.index(3751)<peer.last_response_ids.index(969)<peer.last_response_ids.index(113))
    check('daily login has FINISH status 3',any(fields(m).get(1)==[38169] and fields(m).get(2)==[3] for m in missions.get(2,[])))
    check('claim daily login',query(114,integer(1,38169)).get(1)==[0])
    check('daily reward cannot be claimed twice',query(114,integer(1,38169)).get(1)!=[0])
    grant([dict(kind='material',id=3509,num=4),dict(kind='elf',id=180,num=1),dict(kind='material',id=370180,num=30)])
    result=query(251,integer(1,3509)+integer(2,4))
    check('four treasure rewards returned',result.get(1)==[0] and len(result.get(2,[]))==4)
    check('four-use guarantee and inventory consumption',bag().get(1110,0)>=1 and bag().get(3509,0)==0)
    check('S to SS costs one part',query(2105,integer(1,180)).get(1)==[0] and bag().get(370180)==29)
    check('SS to SSS costs one part',query(2105,integer(1,180)).get(1)==[0] and bag().get(370180)==28)
    check('SSS cap does not debit another part',query(2105,integer(1,180)).get(1)!=[0] and bag().get(370180)==28)
    configs=[fields(x) for x in query(110).get(50,[])]
    check('all 30 captured activities advertised',len([a for a in configs if a[1][0]<10000])==30)
    check('mission activity mode is a parseable integer',all(a.get(8,[b''])[0].decode().isdigit() for a in configs if a.get(2)==[4]))
    check('native activity artwork retained',any(a[1]==[6386] and a.get(14)==[b'event/Immediately/ActivityPage/9.1_Character_banner_truecolor'] for a in configs))
    mission_ids={fields(x)[1][0] for x in query(112).get(2,[])}
    check('future captured activity tasks are now available',697249 in mission_ids and 697210 in mission_ids)
    check('no obsolete global trial samples sent to 9.1',not query(585).get(2))
    check('recommendation banner has an activity',bool(query(1713).get(2)))
    wallet=query(10).get(29,[0])[0];tickets=bag().get(1103,0)
    query(6731,blob(1,b'Bh3MCoinTier1'))
    purchase=query(6714,integer(1,27)+integer(2,721562))
    check('60-coin character bundle returns a nonempty reward quantity',purchase.get(1)==[0] and purchase.get(7)==[1])
    check('gift currency debited and actual ticket added',query(10).get(29,[0])[0]==wallet and bag().get(1103,0)==tickets+1)
    check('single-ticket bundle limit enforced',query(6714,integer(1,27)+integer(2,721562)).get(1)!=[0])
    # Level-gift shop identity is taken from the public GM catalogue.
    state=gm(uid);shop=next(s for s in state['shops'] if 700597 in s['goods'])
    before65=bag()
    check('free 65 gift has nonempty popup quantity',query(6714,integer(1,shop['id'])+integer(2,700597)).get(7)==[1])
    check('free gift delivered both items',bag().get(6004,0)-before65.get(6004,0)==2 and bag().get(915,0)-before65.get(915,0)==600)
    check('free gift repeat rejected',query(6714,integer(1,shop['id'])+integer(2,700597)).get(1)!=[0])
    check('collection reward granted',query(1195,integer(1,1)+integer(2,1)).get(1)==[0])
    check('collection and pendant caches precede reward',peer.last_response_ids.index(1194)<peer.last_response_ids.index(1196) and 1198 in peer.last_response_ids)
    check('collection repeat rejected',query(1195,integer(1,1)+integer(2,1)).get(1)==[3])
    grant([dict(kind='weapon',id=20590,num=1,level=30),dict(kind='material',id=2008,num=1),dict(kind='scoin',num=100000,id=0)])
    check('owned weapon unlocks key',201 in keys())
    check('key levels with materials',query(753,integer(1,201)).get(1)==[0])
    check('key cache precedes mutation callback',peer.last_response_ids.index(507)<peer.last_response_ids.index(754))
    grant([dict(kind='hcoin',id=0,num=1000)])
    check('phase exp granted',query(3758).get(1)==[0])
    bp=query(3750);cost=(1000-bp.get(4,[0])[0]+4)//5
    check('pass purchase checks exact price',query(3756,integer(1,3)+integer(2,cost-1)).get(1)!=[0])
    check('pass level purchase succeeds',query(3756,integer(1,3)+integer(2,cost)).get(1)==[0])
    check('pass returns level rewards',bool(query(3754).get(2)))
    check('pass does not duplicate rewards',not query(3754).get(2))
    state=gm(uid);gm(uid,dict(requestId=str(uuid.uuid4()),action='activity',revision=state['revision'],activity=dict(id=17001,title='本地协议活动',content='任务奖励',enabled=True,beginTime=1,endTime=2145916800,missions=[38169])));query(500)
    check('activity exposed in config field 50',any(fields(x).get(1)==[17001] for x in query(110).get(50,[])))
    groups=query(4321,integer(1,17001)).get(2,[])
    check('activity mission group responds',len(groups)==1 and fields(groups[0]).get(1)==[17001])
    state=gm(uid);a=next(a for a in state['systems']['activities'] if a['id']==6408);a['enabled']=False
    gm(uid,dict(requestId=str(uuid.uuid4()),action='activity',revision=state['revision'],activity=a));query(500)
    check('captured activity can be disabled through GM',next(fields(x) for x in query(110).get(50,[]) if fields(x).get(1)==[6408]).get(4)==[1])
    check('disabled activity stops sending mission cycles',not query(4321,integer(1,6408)).get(2))
    contexts[uid]={}
    return checks
