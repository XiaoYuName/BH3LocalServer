"""Read-only, matching baseline/import/rollback database observation."""
import hashlib
import json
from pathlib import Path
import sqlite3
import sys

path = Path(sys.argv[1]).resolve()
assert sys.argv[2] == 'captured-account-import'
def fields(data):
    offset = 0; output = {}
    def varint():
        nonlocal offset
        value=0; shift=0
        while offset < len(data) and shift < 70:
            byte=data[offset]; offset+=1; value|=(byte&127)<<shift
            if byte<128:return value
            shift+=7
        raise ValueError('Invalid protobuf varint')
    while offset < len(data):
        tag=varint(); kind=tag&7
        if kind==0:value=varint()
        elif kind==2:
            size=varint();value=data[offset:offset+size];offset+=size
            if len(value)!=size:raise ValueError('Truncated protobuf field')
        elif kind in (1,5):
            size=8 if kind==1 else 4;value=data[offset:offset+size];offset+=size
        else:raise ValueError('Unsupported protobuf wire type')
        output.setdefault(tag>>3,[]).append(value)
    return output

with sqlite3.connect(path.as_uri()+'?mode=ro',uri=True) as connection:
    assert connection.execute('pragma integrity_check').fetchone()==('ok',)
    profile=connection.execute('select uid,data_json from player_lobby order by uid').fetchall()
    tables={x[0] for x in connection.execute('select name from sqlite_master where type="table"')}
    accounts=[]
    for uid,raw in profile:
        state=json.loads(raw)
        inventory=connection.execute('select avatars,equipment from player_inventory where uid=?',(uid,)).fetchone() if 'player_inventory' in tables else None
        avatars=fields(inventory[0]).get(2,[]) if inventory else []
        equipment=fields(inventory[1]) if inventory else {}
        accounts.append({'uid':uid,'level':state['Level'],'hcoin':state['Hcoin'],'scoin':state['Scoin'],'stamina':state['Stamina'],
                         'has_imported_inventory':inventory is not None,'avatars':len(avatars) if inventory else 1,
                         'weapons':len(equipment.get(2,[])) if inventory else 1,'stigmata':len(equipment.get(3,[])),
                         'settlement_receipts':connection.execute('select count(*) from stage_receipt where uid=?',(uid,)).fetchone()[0]})
    print(json.dumps({'input':'captured-account-import','observation':'SQLite state and stored protobuf lists; gameplay verified separately',
                      'schema':connection.execute('pragma user_version').fetchone()[0],'accounts':accounts,
                      'sha256':hashlib.sha256(path.read_bytes()).hexdigest()},ensure_ascii=False,sort_keys=True))
