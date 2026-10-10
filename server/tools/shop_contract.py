"""Independent game-wire checks for GM-configured shops and current supply display."""
import uuid

def observe(peer,label,uid,fields,integer,blob,http,contexts,restart=False):
 checks=[]
 def record(name,passed):
  checks.append(dict(name=label+': '+name,passed=bool(passed),response_ids=peer.last_response_ids,response_body_hex=peer.last_response_body.hex()))
  assert passed,name
 def query(command,body=b''):
  r=peer.request(command,body);assert r is not None and r[0]==command+1,(command,r)
  return r[1]
 def post(data):return http(uid,dict(data,requestId=str(uuid.uuid4())))
 def configure(shop):
  state=http(uid);return post(dict(action='shop',revision=state['revision'],shop=shop))
 def shop1():return fields(query(6704,integer(1,1))[2][0])
 def goods(s,id):return next(fields(g) for g in s.get(5,[]) if fields(g)[1]==[id])
 if restart:
  state=http(uid);record('GM shop name/order persists',next(s for s in state['shops'] if s['id']==1)['name']=='KCP shop')
  record('stock survives restart',goods(shop1(),100302)[2]==[1])
  record('wallet survives restart',query(10)[6]==[71112])
  record('purchase limit still enforced',query(6714,integer(1,1)+integer(2,100302))[1]==[3])
  record('explicit pool disable survives restart',len(query(4702)[6])==3)
  return checks
 display=query(4702,integer(1,1));record('four current supply types advertised',sorted(fields(d)[1][0] for d in display[6])==[20,44,46,48])
 record('current supply retains captured backgrounds',all(b'common_gacha_bg' in d for d in display[6]))
 r=query(6700);record('ordinary shops populated',len(r.get(2,[]))>=1 and r[3]==[1])
 record('mall populated',len(query(6702).get(2,[]))>=1)
 record('unknown shop fails explicitly',query(6704,integer(1,999999))[1]==[2])
 record('manual refresh cannot charge',query(6708,integer(1,1))[1]==[6])
 record('no money gives no reward',query(6714,integer(1,1)+integer(2,100302))[1]==[4])
 post(dict(action='grant',items=[dict(kind='scoin',id=0,num=100000)]));query(3800)
 state=http(uid);shop=next(s for s in state['shops'] if s['id']==1);shop.update(name='KCP shop',goods=[100302],dailyRefresh=False)
 configure(shop);query(3800);record('GM updates both shop lists online',{6701,6703}.issubset(peer.last_response_ids))
 record('saved assortment visible in game',len(shop1()[5])==1)
 req=integer(1,1)+integer(2,100302)
 record('purchase delivers captured item and quantity',query(6714,req).get(5)==[10201] and peer.last_response_ids[0:2]==[6701,6703])
 record('exact catalog price deducted',query(10)[6]==[71112])
 record('stock increments once',goods(shop1(),100302)[2]==[1])
 record('limit blocks repeated purchase',query(6714,req)[1]==[3])
 record('limit failure leaves balance intact',query(10)[6]==[71112])
 shop['enabled']=False;configure(shop);query(3800)
 record('GM closed shop cannot sell',query(6714,req)[1]==[6])
 shop['enabled']=True;configure(shop);query(3800)
 state=http(uid);pool=next(p for p in state['pools'] if p['type']==44);pool['enabled']=False
 post(dict(action='pool',revision=state['revision'],pool=pool));query(3800)
 record('GM pool switch changes game list',sorted(fields(d)[1][0] for d in query(4702)[6])==[20,46,48])
 contexts[uid]=True
 return checks
