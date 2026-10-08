"""Run the exact published EXE against isolated ports/data; Python is test-only."""
import argparse
import hashlib
import json
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--exe', type=Path, required=True)
parser.add_argument('--report', type=Path, required=True)
args = parser.parse_args()
exe = args.exe.resolve()
root = Path(tempfile.mkdtemp(prefix='bh3-published-smoke-'))
events = []
checks = []
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

def free_port(kind):
    with socket.socket(socket.AF_INET, kind) as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]

config = {'clientVersion':'9.1.0', 'bindAddress':'127.0.0.1', 'gamePort':free_port(socket.SOCK_DGRAM),
          'healthPort':free_port(socket.SOCK_STREAM), 'maxSessions':8, 'sessionIdleSeconds':30,
          'databasePath':'data/bh3.db', 'logDirectory':'logs', 'transportMode':'handshake-only'}
config_path = root / 'server.json'
config_path.write_text(json.dumps(config),encoding='utf-8')
command = [str(exe), '--config', str(config_path)]

def check(name, condition):
    checks.append({'name':name, 'passed':bool(condition)})
    if not condition:
        raise AssertionError(name)
    print('PASS ' + name, flush=True)

def run_once(label, command, expected=0):
    p = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', timeout=20)
    events.append(dict(label=label, command=command, stdout=p.stdout, stderr=p.stderr, exit_status=p.returncode))
    check(label, p.returncode == expected)
    return p

def read_status():
    with opener.open(f'http://127.0.0.1:{config["healthPort"]}/health/ready',timeout=1) as r:
        return json.load(r)

def session(label, eof=False):
    p = subprocess.Popen(command+['--launcher-control'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8')
    recorded = False
    try:
        deadline = time.monotonic()+15
        while True:
            if p.poll() is not None:
                raise RuntimeError('Server exited before readiness')
            try:
                status = read_status()
                break
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                if time.monotonic() > deadline: raise
                time.sleep(.1)
        check(label+' readiness', status['hostReady'] and not status['gameplayReady'] and status['registeredCommands']==[])
        with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as udp:
            udp.settimeout(3)
            request = bytes.fromhex('000000ff00000000000000000000303900000000')
            udp.sendto(request,('127.0.0.1',config['gamePort']))
            first = udp.recv(256)
            udp.sendto(request,('127.0.0.1',config['gamePort']))
            second = udp.recv(256)
            check(label+' UDP duplicate handshake', first==second and first[:4]==bytes.fromhex('00000145') and first[12:16]==request[12:16])
        if eof:
            p.stdin.close(); p.stdin=None
            stdout,stderr=p.communicate(timeout=10)
        else:
            stdout,stderr=p.communicate('stop\n',timeout=10)
        events.append(dict(label=label, command=command+['--launcher-control'], stdin='EOF' if eof else 'stop\n', stdout=stdout,stderr=stderr,exit_status=p.returncode,status=status,handshake=first.hex()))
        recorded = True
        check(label+' graceful exit',p.returncode==0 and 'server.stopped' in stdout)
    finally:
        if p.poll() is None:
            p.kill()
        if not recorded:
            stdout,stderr=p.communicate(timeout=5)
            events.append(dict(label=label,command=command+['--launcher-control'],stdout=stdout,stderr=stderr,exit_status=p.returncode,incomplete=True))

try:
    run_once('preflight',command+['--check'])
    check('preflight creates no runtime state', not (root/'data').exists() and not (root/'logs').exists())
    session('start-stop')
    database=root/'data/bh3.db'
    with sqlite3.connect(database) as db:
        check('schema identity and no seeded players',db.execute('pragma user_version').fetchone()[0]==1 and db.execute('pragma application_id').fetchone()[0]==0x42483353 and db.execute('select count(*) from account').fetchone()[0]==0)
        # Isolated persistence fixture, not a client login claim.
        db.execute("insert into account values(99001,'2026-10-08T00:00:00Z')")
        db.execute("insert into player_profile values(99001,'restart-fixture',4)")
    session('restart-eof',eof=True)
    with sqlite3.connect(database) as db:
        check('restart preserves isolated fixture',db.execute('select nickname,revision from player_profile where uid=99001').fetchone()==('restart-fixture',4))
    with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as udp, socket.socket(socket.AF_INET,socket.SOCK_STREAM) as tcp:
        udp.bind(('127.0.0.1',config['gamePort'])); tcp.bind(('127.0.0.1',config['healthPort']))
        check('all ports released',True)
    bad=dict(config,transportMode='kcp'); bad_path=root/'invalid.json'; bad_path.write_text(json.dumps(bad),encoding='utf-8')
    run_once('unsupported transport rejected',[str(exe),'--config',str(bad_path),'--check'],expected=2)
except Exception as error:
    checks.append(dict(name='probe completed',passed=False,error=str(error)))
    raise
finally:
    args.report.parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps({'passed':all(c['passed'] for c in checks),'exe':str(exe),'sha256':hashlib.sha256(exe.read_bytes()).hexdigest(),'data_directory':str(root),'checks':checks,'events':events},ensure_ascii=False,indent=2),encoding='utf-8')
