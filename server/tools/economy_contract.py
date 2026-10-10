"""Independent protobuf/KCP checks for the five reported 9.1 economy defects."""
import uuid

def observe(peer,label,uid,fields,integer,blob,gm,contexts,restart=False):
    checks=[]
    def query(cmd,body=b''):
        reply=peer.request(cmd,body,timeout=10)
        assert reply and reply[0]==cmd+1 and reply[2]==uid,(cmd,reply)
        return reply[1]
    def check(name,passed):
        checks.append(dict(name=label+': '+name,passed=bool(passed),response_ids=list(peer.last_response_ids),response_hex=peer.last_response_body.hex()))
        assert passed,name
    def grant(items):gm(uid,dict(requestId=str(uuid.uuid4()),action='grant',items=items))
    def avatars():return {fields(a)[1][0]:fields(a) for a in query(24).get(2,[])}
    def text(field,value):return blob(field,value.encode())
    if restart:
        a=avatars();check('S1 and spent fragments persist',a[20201].get(19)==[1] and a[20201].get(5)==[5])
        check('featured avatar duplicate fragments persist',a[21101].get(5)==[30])
        check('wallet persists',query(10).get(29)==[1060])
        check('monthly claim remains disabled after restart',fields(query(6721)[2][0]).get(4)==[0])
        check('gacha receipt persists',query(4700,contexts[uid]['request'])==contexts[uid]['response'])
        return checks
    check('unowned monthly card offers no daily crystals',fields(query(6721)[2][0]).get(4)==[0])
    check('unowned daily claim is not a fake success',query(6723).get(1)==[2])
    query(6731,text(1,'Bh3GiftHardCoinTier5'))
    check('purchased monthly card enables claim',fields(query(6721)[2][0]).get(4)==[60])
    query(6723)
    check('monthly cache precedes reward callback',peer.last_response_ids.index(6722)<peer.last_response_ids.index(6724))
    check('claimed card button has zero available crystals',fields(query(6721)[2][0]).get(4)==[0])
    check('duplicate daily claim rejected',query(6723).get(1)!=[0])
    for name in ['Bh3MCoinTier30','Bh3MCoinTier30','Bh3MCoinTier15','Bh3MCoinTier1']:
        query(6731,text(1,name))
    check('gift wallet funded to 5000',query(10).get(29)==[5000])
    before=query(10)[5][0]
    purchase=query(6714,integer(1,27)+integer(2,721565))
    check('bundle has positive popup quantity',purchase.get(1)==[0] and purchase.get(7)==[1])
    main=query(10);check('bundle credits 188 crystals and debits 980 gift coins',main.get(5)==[before+188] and main.get(29)==[4020])
    materials={fields(m)[1][0]:fields(m)[2][0] for m in query(26).get(4,[])}
    check('bundle gives five character tickets',materials.get(1103)==5)
    for goods_id in [721570,721571]:
        check('equipment bundle '+str(goods_id)+' has popup quantity',query(6714,integer(1,27)+integer(2,goods_id)).get(7)==[1])
    main=query(10);check('equipment bundles debit 2960 gift coins and grant 476 crystals',main.get(5)==[before+664] and main.get(29)==[1060])
    materials={fields(m)[1][0]:fields(m)[2][0] for m in query(26).get(4,[])}
    check('equipment bundles give fifteen equipment tickets',materials.get(1102)==15)
    shops=[fields(s) for s in query(6700).get(2,[])]
    check('every ordinary shop has localization key',all(s.get(3,[b''])[0].startswith(b'ShopName') for s in shops))
    grant([dict(kind='avatar',id=20201,num=1),dict(kind='material',id=2020201,num=30),dict(kind='avatar',id=21101,num=1)])
    check('Sena promotion succeeds',query(29,integer(1,20201)).get(1)==[0])
    a=avatars()[20201];check('Sena consumes 25 and enters S1',a.get(2)==[3] and a.get(19)==[1] and a.get(5)==[5])
    check('insufficient fragments cannot promote again',query(29,integer(1,20201)).get(1)==[3])
    state=gm(uid);pool=next(p for p in state['pools'] if p['type']==44)
    check('default pool targets captured current avatar',pool['entries'][0]['item']['id']==21101)
    pool['pity']=1
    gm(uid,dict(requestId=str(uuid.uuid4()),action='pool',revision=state['revision'],pool=pool))
    display=query(4702);request=integer(1,44)+integer(2,1)+integer(4,display[5][0])
    response=query(4700,request);item=fields(response[2][0])
    check('guaranteed draw grants featured avatar card',item.get(1)==[3021101])
    check('duplicate is saved to actual avatar fragments',avatars()[21101].get(5)==[30])
    check('receipt replay does not duplicate fragments',query(4700,request)==response and avatars()[21101].get(5)==[30])
    state=gm(uid);check('pity resets and total draws increments',state['pity'].get('44')==0 and state['draws'].get('44')==1)
    contexts[uid]=dict(request=request,response=response)
    return checks
