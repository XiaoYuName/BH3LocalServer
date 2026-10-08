"""Independent native KCP peer against a real server process, using isolated data."""
import argparse
import base64
import ctypes
import hashlib
import hmac
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import tempfile
import threading
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--server', required=True)
parser.add_argument('--native-kcp', required=True)
parser.add_argument('--report', required=True)
parser.add_argument('--baseline', action='store_true')
parser.add_argument('--startup-fixture', help='Known request/response IDs from the observed startup trace; request payloads default empty.')
parser.add_argument('--observe-client-contracts', action='store_true', help='Record four client 9.1 response requirements for baseline/modified comparison.')
parser.add_argument('--observe-overall', action='store_true', help='Compare 7842 full/selected queries and 7843 snapshot flags on both KCP framings.')
parser.add_argument('--observe-init-selectors', action='store_true', help='Observe client 9.1 zero-ID initialization selectors and selected queries.')
args = parser.parse_args()
work = Path(tempfile.mkdtemp(prefix='bh3-lobby-'))
events, checks, client_contracts, overall_contracts = [], [], [], []
init_selector_contracts = []
retired_sockets = []
completed = False
key = os.urandom(32)


def free(kind):
    with socket.socket(socket.AF_INET, kind) as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


port, health = free(socket.SOCK_DGRAM), free(socket.SOCK_STREAM)
config = work / 'server.json'
config.write_text(json.dumps(dict(clientVersion='9.1.0', bindAddress='127.0.0.1', gamePort=port,
    healthPort=health, databasePath='players.db', logDirectory='logs')), encoding='utf-8')
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def check(name, value):
    checks.append(dict(name=name, passed=bool(value)))
    if not value:
        raise AssertionError(name)
    print('PASS', name)


def start():
    env = dict(os.environ, BH3_LOCAL_AUTH_KEY=base64.b64encode(key).decode())
    command = [str(Path(args.server).resolve()), '--config', str(config), '--launcher-control']
    proc = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
    event = dict(command=command, stdout='', stderr='', exit_status=None)
    events.append(event)
    def drain(pipe, label):
        event[label] = pipe.read().decode('utf-8', errors='replace')
    workers = [threading.Thread(target=drain, args=(proc.stdout, 'stdout')), threading.Thread(target=drain, args=(proc.stderr, 'stderr'))]
    for worker in workers:
        worker.start()
    proc.evidence = (event, workers)
    deadline = time.monotonic() + 12
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            stop(proc)
            raise RuntimeError(event)
        try:
            with opener.open(f'http://127.0.0.1:{health}/health/ready', timeout=0.5) as response:
                status = json.load(response)
            if status['hostReady'] and status['processId'] == proc.pid:
                return proc
        except OSError:
            pass
        time.sleep(0.05)
    stop(proc)
    raise TimeoutError('server readiness')


def stop(proc):
    if proc.poll() is None:
        proc.stdin.write(b'stop\n'); proc.stdin.flush(); proc.stdin.close()
        try:
            proc.wait(8)
        except subprocess.TimeoutExpired:
            proc.kill(); proc.wait()
    event, workers = proc.evidence
    for worker in workers:
        worker.join(3)
    event['exit_status'] = proc.returncode


def varint(value):
    out = bytearray()
    while value > 127:
        out.append((value & 127) | 128); value >>= 7
    out.append(value)
    return bytes(out)


def integer(field, value):
    return varint(field << 3) + varint(value)


def blob(field, value):
    if isinstance(value, str):
        value = value.encode()
    return varint((field << 3) | 2) + varint(len(value)) + value


def fields(data):
    offset, result = 0, {}
    def read():
        nonlocal offset
        value = 0
        for shift in range(0, 70, 7):
            byte = data[offset]; offset += 1; value |= (byte & 127) << shift
            if byte < 128:
                return value
        raise ValueError('varint overflow')
    while offset < len(data):
        tag = read(); field, wire = tag >> 3, tag & 7
        if wire == 0:
            value = read()
        elif wire == 2:
            length = read(); value = data[offset:offset+length]; offset += length
        elif wire in (1, 5):
            length = 8 if wire == 1 else 4; value = data[offset:offset+length]; offset += length
        else:
            raise ValueError('wire type')
        result.setdefault(field, []).append(value)
    return result


def ticket(uid):
    payload = json.dumps(dict(Uid=uid, Name='native-captain', Expires=int(time.time()) + 3590), separators=(',', ':')).encode()
    body = base64.b64encode(payload)
    return (body + b'.' + base64.b64encode(hmac.new(key, body, hashlib.sha256).digest())).decode()


native = ctypes.CDLL(str(Path(args.native_kcp).resolve()))
callback_type = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)
for name, parameters, result in [
    ('ikcp_create', [ctypes.c_uint32, ctypes.c_void_p], ctypes.c_void_p),
    ('ikcp_release', [ctypes.c_void_p], None), ('ikcp_setoutput', [ctypes.c_void_p, callback_type], None),
    ('ikcp_nodelay', [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int], ctypes.c_int),
    ('ikcp_wndsize', [ctypes.c_void_p, ctypes.c_int, ctypes.c_int], ctypes.c_int),
    ('ikcp_setmtu', [ctypes.c_void_p, ctypes.c_int], ctypes.c_int),
    ('ikcp_send', [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int], ctypes.c_int),
    # This client's plugin adds an offset argument (verified at ikcp_input RVA 0x1790).
    ('ikcp_input', [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int], ctypes.c_int),
    ('ikcp_update', [ctypes.c_void_p, ctypes.c_uint32], None),
    ('ikcp_recv', [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int], ctypes.c_int)]:
    method = getattr(native, name); method.argtypes = parameters; method.restype = result


class Peer:
    def __init__(self, wide=False):
        self.wide = wide; self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.connect(('127.0.0.1', port)); self.sock.settimeout(2)
        self.sock.send(struct.pack('>IIIII', 255, 0, 0, 91, 0xffffffff))
        response = self.sock.recv(2048); self.conv = struct.unpack_from('<I', response, 8)[0]
        self.ptr = native.ikcp_create(self.conv, None)
        self.callback = callback_type(self.output); native.ikcp_setoutput(self.ptr, self.callback)
        native.ikcp_nodelay(self.ptr, 1, 10, 2, 1); native.ikcp_wndsize(self.ptr, 256, 256)
        native.ikcp_setmtu(self.ptr, 1396 if wide else 1400); self.sock.settimeout(0.01)
    def convert(self, data, sending):
        if not self.wide:
            return data
        output = bytearray(); header, convsize = (24, 4) if sending else (28, 8)
        while data:
            length = struct.unpack_from('<I', data, convsize + 16)[0]
            output += struct.pack('>Q' if sending else '<I', self.conv) + data[convsize:header+length]
            data = data[header+length:]
        return bytes(output)
    def output(self, pointer, length, _kcp, _user):
        self.sock.send(self.convert(ctypes.string_at(pointer, length), True)); return 0
    def request(self, command, body=b'', timeout=3):
        prefix = struct.pack('>IHHIIIIH', 0x01234567, 1, 0, 0, 999999, 0, 0, 0)
        data = prefix + struct.pack('>HHI', command, 0, len(body)) + body + struct.pack('>I', 0x89abcdef)
        assert native.ikcp_send(self.ptr, data, len(data)) == 0
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            native.ikcp_update(self.ptr, int(time.monotonic()*1000) & 0xffffffff)
            try:
                packet = self.convert(self.sock.recv(65535), False)
                result = native.ikcp_input(self.ptr, packet, 0, len(packet))
                if result < 0:
                    raise ValueError(f'native input={result}; header={packet[:28].hex()} length={len(packet)}')
            except TimeoutError:
                pass
            buffer = ctypes.create_string_buffer(200000)
            length = native.ikcp_recv(self.ptr, buffer, len(buffer))
            if length >= 0:
                message = buffer.raw[:length]
                assert message[:4] == bytes.fromhex('01234567') and message[-4:] == bytes.fromhex('89abcdef')
                response_id, metadata_size, size = struct.unpack_from('>HHI', message, 26)
                self.last_response_body = message[34+metadata_size:34+metadata_size+size]
                return response_id, fields(message[34+metadata_size:34+metadata_size+size]), struct.unpack_from('>I', message, 12)[0]
        return None
    def login(self, uid):
        result = self.request(4, integer(1, 1) + blob(2, str(uid)) + blob(6, '9.1.0') + blob(23, ticket(uid)))
        if result is None:
            return False
        assert result[0] == 5 and result[1][1] == [0] and result[1][2] == [uid]
        result = self.request(6, blob(12, str(uid)) + blob(17, '9.1.0'))
        assert result[0] == 7 and result[1][1] == [0]
        return True
    def close(self):
        # Close the server session explicitly and retain the UDP endpoint until shutdown:
        # closing it with an outstanding KCP ACK can send Windows ICMP resets to the host.
        disconnect = bytearray(20)
        struct.pack_into('>I', disconnect, 0, 404)
        struct.pack_into('<I', disconnect, 8, self.conv)
        self.sock.send(disconnect)
        native.ikcp_release(self.ptr)
        retired_sockets.append(self.sock)


def observe_init_selectors(peer, framing, uid):
    # Startup senders in the local 9.1 binary explicitly append zero to these ID lists.
    # These are static-derived inputs, not a replay of payloads from the user log.
    for command, response, flag_field, list_fields, data_field in [
        (24, 25, 3, [1], 2), (26, 27, 5, [1, 2, 3, 4], 2),
        (449, 450, 3, [1], None), (506, 507, 3, [1], None),
        (643, 644, 3, [1], None), (1197, 1198, 2, [1], None)]:
        for selected in [False, True]:
            body = b''.join(integer(field, 999999 if selected else 0) for field in list_fields)
            reply = peer.request(command, body)
            data = reply[1] if reply else {}
            count = len(data.get(data_field, [])) if data_field else 0
            passed = (reply is not None and reply[0] == response and reply[2] == uid
                      and data.get(1) == [0] and data.get(flag_field) == [int(not selected)]
                      and count == (1 if data_field and not selected else 0))
            init_selector_contracts.append(dict(name=f'{framing}: {command} ' + ('selected' if selected else 'zero/full'),
                request_hex=body.hex(), response_body_hex=peer.last_response_body.hex() if reply else None,
                is_all=data.get(flag_field), item_count=count, passed=passed))


proc = None
try:
    proc = start()
    peer = Peer()
    try:
        logged_in = peer.login(10001)
        if args.baseline:
            check('BASELINE: handshake works; no login response', not logged_in)
        else:
            check('Native KCP login', logged_in)
            main = peer.request(10)
            check('Main data uses authenticated UID, not packet UID', main[0] == 11 and main[2] == 10001 and main[1][2] == [b'native-captain'])
            avatar = peer.request(24); equipment = peer.request(26)
            check('Starter avatar and weapon', fields(avatar[1][2][0])[1] == [101] and fields(equipment[1][2][0])[2] == [20001])
            if args.observe_init_selectors:
                observe_init_selectors(peer, '32-bit', 10001)
            guides = peer.request(127)
            check('Fragmented guide list arrives through native KCP', guides[0] == 128 and len(guides[1][2]) > 100)
            saved = peer.request(1588, blob(1, integer(1, 1) + integer(2, 42) + blob(3, b'persist-after-restart')))
            check('Client settings committed', saved[0] == 1589 and saved[1][1] == [0])
            if args.observe_client_contracts:
                card, gacha, boss, mecha = peer.request(480), peer.request(4702, integer(1, 1)), peer.request(510), peer.request(4514)
                client_contracts.extend([
                    dict(name='Player card fixed slots and message data', passed=card is not None and len(card[1].get(3, [])) == 3 and len(card[1].get(12, [])) == 2 and len(card[1].get(10, [])) == 1 and 6 in card[1]),
                    dict(name='All-pools gacha omits unmapped enum zero', passed=gacha is not None and 3 not in gacha[1] and gacha[1].get(2) == [1]),
                    dict(name='Inactive ExBoss response contains BossInfo', passed=boss is not None and 2 in boss[1]),
                    dict(name='Mecha defense response contains MechaDefense', passed=mecha is not None and 2 in mecha[1])])
                for contract in client_contracts:
                    print('CLIENT_CONTRACT', json.dumps(contract), flush=True)
            if args.startup_fixture:
                fixture = json.loads(Path(args.startup_fixture).read_text(encoding='utf-8-sig'))
                for item in fixture:
                    reply = peer.request(item['request'])
                    check('Startup response ' + str(item['request']) + ' -> ' + str(item['response']),
                          reply is not None and reply[0] == item['response'] and reply[2] == 10001)
                finished = peer.request(129, integer(1, 999001))
                check('Guide completion acknowledged', finished[0] == 130 and finished[1][2] == [999001] and finished[1][3] == [1])
            if args.observe_overall:
                for name, body, flag in [('Full snapshot', b'\x10\x01', 1), ('Selected snapshot', b'\x08\x7b', 0)]:
                    reply = peer.request(7842, body)
                    overall_contracts.append(dict(name=name, request_hex=body.hex(), response=reply,
                        passed=reply is not None and reply[0] == 7843 and reply[2] == 10001 and reply[1] == {1: [0], 3: [flag]}))
    finally:
        peer.close()
    if not args.baseline:
        peer = Peer(True)
        try:
            bad = peer.request(4, blob(2, '10002') + blob(23, 'forged'))
            check('Forged local ticket rejected', bad[1][1] == [3])
            check('64-bit BH3 KCP framing login', peer.login(10002))
            if args.observe_init_selectors:
                observe_init_selectors(peer, '64-bit', 10002)
            if args.observe_overall:
                reply = peer.request(7842, b'\x10\x01')
                overall_contracts.append(dict(name='64-bit full snapshot', request_hex='1001', response=reply,
                    passed=reply is not None and reply[0] == 7843 and reply[2] == 10002 and reply[1] == {1: [0], 3: [1]}))
            other = peer.request(1586, integer(1, 1) + integer(2, 42))
            check('Other account cannot read client settings', 4 not in other[1])
        finally:
            peer.close()
        stop(proc); proc = start(); peer = Peer()
        try:
            check('Reconnect after restart', peer.login(10001))
            loaded = peer.request(1586, integer(1, 1) + integer(2, 42))
            check('Client settings survive restart', fields(loaded[1][4][0])[3] == [b'persist-after-restart'])
            if args.startup_fixture:
                check('Completed guide survives restart', 999001 in peer.request(127)[1][2])
        finally:
            peer.close()
    completed = True
finally:
    if proc is not None:
        stop(proc)
    for sock in retired_sockets:
        sock.close()
    record = dict(passed=completed and bool(checks) and all(c['passed'] for c in checks), baseline=args.baseline,
        checks=checks, events=events, data_directory=str(work),
        server_sha256=hashlib.sha256(Path(args.server).read_bytes()).hexdigest(),
        native_kcp_sha256=hashlib.sha256(Path(args.native_kcp).read_bytes()).hexdigest(), real_game_client=False,
        client_contracts=client_contracts, overall_contracts=overall_contracts, init_selector_contracts=init_selector_contracts)
    for contract in init_selector_contracts:
        print('INIT_SELECTOR_CONTRACT', json.dumps(contract), flush=True)
    for contract in overall_contracts:
        print('OVERALL_CONTRACT', json.dumps(contract), flush=True)
    Path(args.report).write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding='utf-8')
