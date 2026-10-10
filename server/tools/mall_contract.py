"""Independent wire checks: full mall, local RMB success, monthly card and restart."""
def observe(peer,label,fields,integer,blob,contexts,restart=False):
    checks=[]
    def query(cmd,body=b''):
        r=peer.request(cmd,body);assert r and r[0]==cmd+1,(cmd,r);return r[1]
    def record(name,ok):
        checks.append(dict(name=label+': '+name,passed=bool(ok),response_ids=peer.last_response_ids,response_body_hex=peer.last_response_body.hex()))
        assert ok,name
    def text(n,s):return blob(n,s.encode())
    if restart:
        main=query(10);record('purchased wallets survive restart',main[5]==contexts[label][0] and main[29]==contexts[label][1])
        record('monthly claim survives restart',query(6723).get(2,[])==[])
        mall=fields(query(6702)[2][0]);record('mall purchase count survives restart',next(fields(x) for x in mall[5] if fields(x)[1]==[710055])[2]==[1])
        return checks
    mall=fields(query(6702)[2][0]);record('all 179 captured mall goods advertised',len(mall[5])==179)
    products=query(6706);record('13 recharge products are available',len(products[2])==13)
    names=[fields(x)[1][0].decode() for x in products[2]];record('monthly card and gift currency included','Bh3GiftHardCoinTier5' in names and 'Bh3MCoinTier60' in names)
    before=query(10)[5][0]
    record('unknown payment fails',query(6731,text(1,'invalid'))[1]!=[0])
    record('unknown payment leaves wallet intact',query(10)[5]==[before])
    r=query(6731,text(1,'Bh3FirstHardCoinTier1'));record('buy product returns success and recharge notice',r[1]==[0] and 6742 in peer.last_snapshots)
    record('payment notification names the product',peer.last_snapshots[6742][2]==[b'Bh3FirstHardCoinTier1'])
    record('exact first purchase crystals credited',query(10)[5]==[before+120])
    r=query(1494,text(1,'Bh3MCoinTier1'));record('Alipay local adapter returns success',r[1]==[0] and 6742 in peer.last_snapshots)
    record('gift currency reaches main data',query(10)[29]==[60])
    r=query(207,text(2,'Bh3MCoinTier1'));record('WeChat local adapter returns success',r[1]==[0] and 6742 in peer.last_snapshots)
    record('second gift currency grant',query(10)[29]==[120])
    record('monthly purchase succeeds',query(6731,text(1,'Bh3GiftHardCoinTier5'))[1]==[0])
    card=fields(query(6721)[2][0]);record('monthly card active for 30 days',card[10]==[30])
    record('daily reward granted',len(query(6723).get(2,[]))==1)
    record('daily reward cannot be claimed twice',len(query(6723).get(2,[]))==0)
    record('monthly purchase and daily crystals credited',query(10)[5]==[before+480])
    record('gift-coin purchase succeeds with local delivery',query(6714,integer(1,27)+integer(2,710055))[1]==[0])
    record('gift-coin purchase debits exactly 100',query(10)[29]==[20])
    main=query(10);contexts[label]=(main[5],main[29])
    return checks
