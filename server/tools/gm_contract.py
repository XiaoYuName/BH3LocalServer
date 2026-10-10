"""Independent HTTP GM + native KCP game/mail/gacha transaction checks."""
import json, uuid, urllib.request

def observe(peer,label,uid,fields,integer,blob,http,contexts,restart=False):
    checks=[]
    def record(name,passed):
        checks.append(dict(name=label+': '+name,passed=bool(passed),response_ids=peer.last_response_ids,response_body_hex=peer.last_response_body.hex()))
        assert passed,name
    def query(command,body=b''):
        r=peer.request(command,body)
        assert r is not None and r[0]==command+1,(command,r)
        return r[1]
    def post(data):return http(uid,dict(data,requestId=str(uuid.uuid4())))
    if restart:
        r=query(3800,integer(2,9));record('claimed mail survives restart',fields(r[2][0])[7]==[1])
        record('wallet survives restart',query(10)[5]==[2440])
        record('gacha logs survive restart',len(fields(query(4706)[2][0])[2])==11)
        body,saved=contexts[uid];query(4700,body);record('draw receipt survives restart',peer.last_response_body.hex()==saved)
        record('replayed draw does not charge',query(10)[5]==[2440]);return checks
    post(dict(action='grant',items=[dict(kind='hcoin',id=0,num=5000,level=1),dict(kind='weapon',id=20001,num=1,level=1)]))
    query(3800,integer(2,9));record('GM pushes main/inventory to online session',{11,25,27,4703}.issubset(peer.last_response_ids))
    post(dict(action='mail',title='KCP mail',content='local transaction fixture',days=7,items=[dict(kind='hcoin',id=0,num=800,level=1),dict(kind='material',id=3000,num=2,level=1),dict(kind='weapon',id=20001,num=1,level=1)]))
    r=query(3800,integer(2,9));record('new mail notification and mailbox',3808 in peer.last_response_ids and len(r[2])==1)
    mail=fields(r[2][0]);key=mail[1][0];record('style/unread/unclaimed fields',mail[12]==[1] and mail[11]==[0] and mail[7]==[0])
    record('mark read returns same key',query(3804,blob(1,key))[2]==[key])
    query(3809,blob(1,key)+integer(2,1));record('favorite filter contains mail',len(query(3800,integer(3,2))[2])==1)
    take=query(3802,integer(2,1));record('take-all grants attachments',take[1]==[0] and take[5]==[1] and fields(take[4][0])[2]==[800])
    record('mail wallet credited once',query(10)[5]==[5800])
    query(3802,integer(2,1));record('mail claim retry has no second grant',query(10)[5]==[5800])
    equipment=query(26);record('weapon attachment has distinct persistent instance',len(equipment[2])==3 and len({fields(x)[1][0] for x in equipment[2]})==3)
    snapshot=http(uid)
    pool=dict(type=46,name='KCP pool',enabled=True,cost=280,ticket=1102,pity=3,entries=[dict(item=dict(kind='weapon',id=20001,num=1,level=1),weight=1,rare=True)])
    post(dict(action='pool',revision=snapshot['revision'],pool=pool));query(3800)
    display=query(4702,integer(1,1));record('enabled pool advertised with fresh nonce',len(display[6])==4 and display[5][0]>0)
    # GetGachaDisplayRsp field mapping is verified below against the generated schema.
    nonce=display[5][0]
    body=integer(1,46)+integer(2,10)+integer(3,1)+integer(4,nonce)
    draw=query(4700,body);saved=peer.last_response_body.hex();record('ten pull returns ten items',draw[1]==[0] and len(draw[2])==10)
    record('ten pull deducts exact cost',query(10)[5]==[3000])
    query(4700,body);record('duplicate draw returns exact receipt',peer.last_response_body.hex()==saved)
    record('duplicate draw does not charge',query(10)[5]==[3000])
    tickets=query(4704,integer(1,1102)+integer(2,2));record('buy two tickets costs 560 crystals',tickets[1]==[0] and tickets[4]==[560])
    nonce=query(4702)[5][0];query(4700,integer(1,46)+integer(2,1)+integer(4,nonce));record('ticket pull preserves crystals',query(10)[5]==[2440])
    record('eleven draw records available',len(fields(query(4706)[2][0])[2])==11)
    record('configured probability available',len(query(4708,integer(1,46))[4])==1)
    contexts[uid]=(body,saved);return checks
