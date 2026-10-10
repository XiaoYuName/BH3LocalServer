"""9.1 wire contracts, encoded independently from the C# protobuf generator."""
import uuid

def observe(peer,label,uid,fields,integer,blob,gm,contexts,restart=False):
    checks=[]
    def request(cmd,body=b''):
        reply=peer.request(cmd,body,timeout=10)
        return reply[1] if reply and reply[0]==cmd+1 and reply[2]==uid else {}
    def check(name,passed):
        checks.append(dict(name=f'{label}: {name}',passed=bool(passed),response_ids=list(peer.last_response_ids),response_hex=peer.last_response_body.hex()))
    def grant(items):gm(uid,dict(requestId=str(uuid.uuid4()),action='grant',items=items))
    def elves():return {fields(x).get(1,[0])[0]:fields(x) for x in request(2100).get(2,[])}
    def skill(elf,id):return next((fields(x) for x in elf.get(6,[]) if fields(x).get(1)==[id]),{})
    def mission():return next((fields(x) for x in request(112).get(2,[]) if fields(x).get(1)==[10009]),{})
    if restart:
        current=request(2100);check('companion levels, ranks, masks and fragments persist',current==contexts[uid]['elves'])
        check('supply mission still FINISH=3 after restart',mission().get(2)==[3])
        wallet=request(10);check('companion wallet persists',wallet.get(5)==contexts[uid]['wallet'].get(5) and wallet.get(6)==contexts[uid]['wallet'].get(6))
        check('compensation cannot be claimed repeatedly',request(2125).get(1)==[2])
        return checks
    gm(uid,dict(requestId=str(uuid.uuid4()),action='captain',level=88))
    grant([dict(kind='stamina',id=0,num=100),dict(kind='scoin',id=0,num=100000),dict(kind='material',id=370101,num=150),dict(kind='material',id=1002,num=2)])
    result=request(2100);check('empty account receives explicit compensation state',result.get(1)==[0] and result.get(5)==[1] and not result.get(2))
    check('unknown companion is rejected',request(2105,integer(1,999)).get(1)==[2])
    check('fragments unlock companion',request(2105,integer(1,101)).get(3)==[1])
    check('upgrade notifies before callback',peer.last_response_ids.index(2102)<peer.last_response_ids.index(2106) and 2103 in peer.last_response_ids)
    check('remaining fragments upgrade rank',request(2105,integer(1,101)).get(1)==[0] and elves()[101].get(2)==[2])
    check('insufficient fragments reject without upgrade',request(2105,integer(1,101)).get(1)==[3] and elves()[101].get(2)==[2])
    exp=integer(1,101)+integer(2,1002)+integer(3,1)
    check('experience returns old level and consumes material',request(2107,exp).get(2)==[1])
    check('experience advances level and remainder',elves()[101].get(3)==[3] and elves()[101].get(4)==[70])
    check('unknown skill fails',request(2123,integer(1,101)+integer(2,99999)).get(1)!=[0])
    check('skill mask uses new Partthree request',request(1742,integer(1,101)+integer(2,10101)+integer(3,1)).get(1)==[0])
    check('skill mask serialized on field 3',skill(elves()[101],10101).get(3)==[1])
    grant([dict(kind='elf',id=140,num=1),dict(kind='material',id=380101,num=1)])
    check('GM grants AstralOp and converts duplicate ELF card',140 in elves() and len(elves())==2)
    check('fragment conversion returns actual materials',request(2121,integer(1,370101)+integer(2,2)).get(3)==[150])
    check('no fictitious compensation reward',request(2125).get(1)==[2])
    for stage in [10101,10102,10105,10106,10109]:
        result=request(43,integer(1,stage)+integer(2,101)+integer(6,101))
        check(f'owned ELF can enter stage {stage}',result.get(1)==[0])
        check(f'stage {stage} settles',request(45,blob(1,integer(1,stage))+blob(2,f'companion-{uid}-{stage}')).get(1)==[0])
    check('completed unlock mission uses client FINISH=3',mission().get(2)==[3])
    display=request(4702,integer(1,1));check('open display has four gacha pools',display.get(1)==[0] and len(display.get(6,[]))==4)
    grant([dict(kind='hcoin',id=0,num=2800)])
    # The GM edit and the game draw must use the same type-48 pool.
    draw_body=integer(1,48)+integer(2,10)+integer(3,1)+integer(4,display[5][0])
    draw=request(4700,draw_body);check('AstralOp ten pull returns ten rewards',draw.get(1)==[0] and len(draw.get(2,[]))==10)
    wallet=request(10);again=request(4700,draw_body)
    check('AstralOp draw replay returns identical receipt',again==draw)
    check('AstralOp draw replay does not charge twice',request(10).get(5)==wallet.get(5))
    contexts[uid]=dict(elves=request(2100),wallet=request(10))
    return checks
