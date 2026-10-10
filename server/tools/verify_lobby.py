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
parser.add_argument('--observe-campaign', action='store_true', help='Exercise chapter-one begin/end/rewards/restart using independent wire payloads.')
parser.add_argument('--observe-world-map', action='store_true', help='Observe main-story schedule, recommendation and chapter route prerequisites.')
parser.add_argument('--observe-chapter-unlock', action='store_true', help='Replay client chapter-group unlock and selected/full snapshot contracts.')
parser.add_argument('--observe-team-prepare', action='store_true', help='Replay client prepare-page member lookup, team updates and restart.')
parser.add_argument('--observe-settlement', action='store_true', help='Replay omitted-WIN, invalid enum errors, next-stage progression and post-battle callbacks.')
parser.add_argument('--observe-pjms-current', action='store_true', help='Check post-settlement current-world response, startup consistency and absence of progression side effects.')
parser.add_argument('--observe-act-reward', action='store_true', help='Replay single/batch challenge claims and 457-before-459 cache updates, retry and restart.')
parser.add_argument('--account-copy', help='Import this reviewed account copy into a fresh isolated test UID and check large snapshots on both KCP framings.')
parser.add_argument('--observe-gm', action='store_true')
parser.add_argument('--observe-shop', action='store_true')
parser.add_argument('--observe-mall', action='store_true')
parser.add_argument('--observe-gameplay', action='store_true')
parser.add_argument('--observe-companions', action='store_true')
parser.add_argument('--observe-economy', action='store_true')
parser.add_argument('--observe-systems', action='store_true')
args = parser.parse_args()
work = Path(tempfile.mkdtemp(prefix='bh3-lobby-'))
events, checks, client_contracts, overall_contracts = [], [], [], []
init_selector_contracts = []
world_map_contracts = []
chapter_unlock_contracts = []
team_prepare_contracts = []
from team_prepare_contract import observe as observe_team_prepare
from settlement_contract import observe as observe_settlement
from pjms_current_contract import observe as observe_pjms_current
pjms_current_contracts = []
from act_reward_contract import observe as observe_act_reward
act_reward_contracts, act_reward_contexts = [], {}
settlement_contracts, settlement_contexts = [], {}
from chapter_unlock_contract import observe as observe_chapter_unlock
from world_map_contract import observe as observe_world_map
campaign_contracts, campaign_contexts = [], {}
from gm_contract import observe as observe_gm
gm_contracts, gm_contexts = [], {}
from shop_contract import observe as observe_shop
shop_contracts, shop_contexts = [], {}
from mall_contract import observe as observe_mall
mall_contracts, mall_contexts = [], {}
from gameplay_contract import observe as observe_gameplay
gameplay_contracts, gameplay_contexts = [], {}
from companion_contract import observe as observe_companions
companion_contracts, companion_contexts = [], {}
from economy_contract import observe as observe_economy
economy_contracts, economy_contexts = [], {}
from systems_contract import observe as observe_systems
systems_contracts, systems_contexts = [], {}
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
        primary, snapshots = None, set()
        self.last_snapshots = {}
        self.last_response_ids = []
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
            while length >= 0:
                message = buffer.raw[:length]
                assert message[:4] == bytes.fromhex('01234567') and message[-4:] == bytes.fromhex('89abcdef')
                response_id, metadata_size, size = struct.unpack_from('>HHI', message, 26)
                raw = message[34+metadata_size:34+metadata_size+size]
                decoded = fields(raw)
                self.last_response_ids.append(response_id)
                if command in (531,5206) and response_id in (511,533,5203) and primary is None:
                    self.last_snapshots[response_id] = decoded
                    length = native.ikcp_recv(self.ptr, buffer, len(buffer))
                    continue
                if command == 458 and response_id == 457 and primary is None:
                    self.last_snapshots[457] = decoded
                    length = native.ikcp_recv(self.ptr, buffer, len(buffer))
                    continue
                if primary is None and response_id != command + 1 and response_id in (3751,969,507,1194,1198,6718,450,591):
                    self.last_snapshots[response_id] = decoded
                    length = native.ikcp_recv(self.ptr, buffer, len(buffer))
                    continue
                if (args.observe_gm or args.observe_shop or args.observe_mall or args.observe_gameplay or args.observe_companions or args.observe_economy or args.observe_systems) and primary is None and response_id != command + 1 and response_id in (11,25,27,113,2102,2103,4703,3808,6701,6703,6722,6707):
                    if response_id in (6722,6707):self.last_snapshots[response_id]=decoded
                    length = native.ikcp_recv(self.ptr, buffer, len(buffer))
                    continue
                if primary is None:
                    self.last_response_body = raw
                    primary = (response_id, decoded, struct.unpack_from('>I', message, 12)[0])
                    if command not in (6719,251,288,753,755,757,759,761,763,765,1195,3752,3754,3756,3758,29, 43, 45, 114, 458, 3802, 4700, 4704, 6714, 6731, 1494, 207, 6723, 6725, 6733, 531, 5206, 2105, 2107, 2121, 2123, 1742) or decoded.get(1) != [0]:
                        return primary
                    snapshots = {11, 6707, 6722, 6742} if command in (6731,1494,207) else {11, 42, 113, 25, 27}
                    if command == 531: snapshots = {11,27}
                    if command == 5206: snapshots = {113}
                    if command in (6723,6725,6733): snapshots.update({6722,6707}-self.last_snapshots.keys())
                elif response_id in snapshots:
                    self.last_snapshots[response_id] = decoded
                    snapshots.remove(response_id)
                else:
                    raise AssertionError(('unexpected queued response', command, response_id))
                if not snapshots:
                    return primary
                length = native.ikcp_recv(self.ptr, buffer, len(buffer))
        if primary is not None:
            raise TimeoutError(('missing committed snapshots', command, snapshots))
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


def observe_settlement_session(wide, uid, restart=False):
    peer = Peer(wide)
    try:
        assert peer.login(uid)
        label = 'restart' if restart else ('64-bit' if wide else '32-bit')
        settlement_contracts.extend(observe_settlement(peer, label, uid, fields, integer, blob, settlement_contexts, restart))
    finally:
        peer.close()


def observe_act_reward_session(wide, uid, restart=False):
    peer = Peer(wide)
    try:
        assert peer.login(uid)
        label = 'restart' if restart else ('64-bit single' if wide else '32-bit batch')
        act_reward_contracts.extend(observe_act_reward(peer, label, uid, fields, integer, blob,
                                                      act_reward_contexts, batch=not wide, restart=restart))
    finally:
        peer.close()


def observe_campaign(peer, framing, uid, restart=False):
    def observe(name, passed, request=b''):
        campaign_contracts.append(dict(name=f'{framing}: {name}', passed=bool(passed), request_hex=request.hex(),
            response_body_hex=peer.last_response_body.hex()))
    if restart:
        end_request, end_response, expected_wallet = campaign_contexts[uid]
        result = peer.request(45, end_request)
        observe('settlement receipt survives process restart', result is not None and peer.last_response_body == end_response, end_request)
        main = peer.request(10)
        observe('wallet and completed stage survive process restart', main[1].get(6) == [expected_wallet]
                and fields(peer.request(41, integer(1, 10101))[1][2][0]).get(2) == [1])
        return
    stage_list = peer.request(41, integer(1, 0))
    observe('chapter-one stage catalog', stage_list is not None and {fields(s)[1][0] for s in stage_list[1].get(2, [])}.issuperset(range(10101,10116)))
    missions = peer.request(112)
    observe('mainline missions available', missions is not None and len(missions[1].get(2, [])) > 0)
    for cmd in (121, 476, 502, 813, 6706, 4167, 3460):
        reply = peer.request(cmd)
        observe(f'logged unsupported request {cmd} answered', reply is not None and reply[0] == cmd + 1)
    payload = integer(1, 10101) + integer(2, 101) + integer(2, 0) + integer(2, 0)
    begin = peer.request(43, payload)
    observe('enter stage 10101', begin is not None and begin[0] == 44 and begin[1].get(1) == [0], payload)
    if begin is None or begin[1].get(1) != [0]:
        return
    initial_begin = peer.last_response_body
    observe('begin pushes stamina reservation', peer.last_snapshots[11].get(7) == [74])
    retry = peer.request(43, payload)
    observe('begin retry does not charge twice', peer.last_response_body == initial_begin and peer.last_snapshots[11].get(7) == [74], payload)
    body = integer(1, 10101) + integer(2, 1) + integer(4, 999999) + integer(5, 999999) + b''.join(integer(6, i) for i in range(3)) + integer(10, 55000) + integer(12, 1234)
    end_request = blob(1, body) + blob(2, begin[1][6][0])
    end = peer.request(45, end_request); end_response = peer.last_response_body
    observe('win settles server rewards and first clear', end is not None and end[1].get(3) == [6] and end[1].get(4) == [25]
            and end[1].get(5) == [750] and end[1].get(37) == [1] and len(end[1].get(6, [])) == 3, end_request)
    observe('wallet snapshots reflect settlement', peer.last_snapshots[11].get(6) == [750] and peer.last_snapshots[11].get(5) == [15])
    peer.request(45, end_request)
    observe('end retry returns exact receipt and same wallet', peer.last_response_body == end_response and peer.last_snapshots[11].get(6) == [750], end_request)
    claim_request = integer(1, 10001)
    claim = peer.request(114, claim_request)
    observe('completed mainline mission can be claimed', claim is not None and claim[1].get(1) == [0] and fields(claim[1][2][0]).get(1) == [12], claim_request)
    duplicate = peer.request(114, claim_request)
    observe('mission reward cannot be claimed twice', duplicate is not None and duplicate[1].get(1) == [3], claim_request)
    next_begin = peer.request(43, integer(1, 10102) + integer(2, 101))
    observe('completion unlocks next stage', next_begin is not None and next_begin[1].get(1) == [0])
    exit_request = blob(1, integer(1, 10102) + integer(2, 4)) + blob(2, next_begin[1][6][0])
    exit_result = peer.request(45, exit_request)
    observe('exit closes run without rewards or completion', exit_result[1].get(8) == [0] and peer.last_snapshots[11].get(6) == [750], exit_request)
    campaign_contexts[uid] = (end_request, end_response, 750)


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
            if args.observe_world_map:
                world_map_contracts.extend(observe_world_map(peer, '32-bit', 10001, fields, integer))
            if args.observe_chapter_unlock:
                chapter_unlock_contracts.extend(observe_chapter_unlock(peer, '32-bit', 10001, fields, integer))
            if args.observe_campaign:
                observe_campaign(peer, '32-bit', 10001)
            if args.observe_team_prepare:
                team_prepare_contracts.extend(observe_team_prepare(peer, '32-bit', 10001, fields, integer, blob))
            if args.observe_settlement:
                observe_settlement_session(False, 11001)
            if args.observe_pjms_current:
                pjms_current_contracts.extend(observe_pjms_current(peer, '32-bit after settlement', 10001, fields))
            if args.observe_act_reward:
                observe_act_reward_session(False, 12001)
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
            if args.observe_world_map:
                world_map_contracts.extend(observe_world_map(peer, '64-bit', 10002, fields, integer))
            if args.observe_chapter_unlock:
                chapter_unlock_contracts.extend(observe_chapter_unlock(peer, '64-bit', 10002, fields, integer))
            if args.observe_campaign:
                observe_campaign(peer, '64-bit', 10002)
            if args.observe_team_prepare:
                team_prepare_contracts.extend(observe_team_prepare(peer, '64-bit', 10002, fields, integer, blob))
            if args.observe_settlement:
                observe_settlement_session(True, 11002)
            if args.observe_pjms_current:
                pjms_current_contracts.extend(observe_pjms_current(peer, '64-bit after settlement', 10002, fields))
            if args.observe_act_reward:
                observe_act_reward_session(True, 12002)
        finally:
            peer.close()
        stop(proc); proc = start(); peer = Peer()
        try:
            check('Reconnect after restart', peer.login(10001))
            loaded = peer.request(1586, integer(1, 1) + integer(2, 42))
            check('Client settings survive restart', fields(loaded[1][4][0])[3] == [b'persist-after-restart'])
            if args.startup_fixture:
                check('Completed guide survives restart', 999001 in peer.request(127)[1][2])
            if args.observe_world_map:
                world_map_contracts.extend(observe_world_map(peer, 'restart', 10001, fields, integer))
            if args.observe_chapter_unlock:
                chapter_unlock_contracts.extend(observe_chapter_unlock(peer, 'restart', 10001, fields, integer))
            if args.observe_campaign and 10001 in campaign_contexts:
                observe_campaign(peer, 'restart', 10001, restart=True)
            if args.observe_team_prepare:
                team_prepare_contracts.extend(observe_team_prepare(peer, 'restart', 10001, fields, integer, blob))
            if args.observe_settlement:
                observe_settlement_session(False, 11001, restart=True)
            if args.observe_pjms_current:
                pjms_current_contracts.extend(observe_pjms_current(peer, 'restart', 10001, fields))
            if args.observe_act_reward:
                observe_act_reward_session(False, 12001, restart=True)
        finally:
            peer.close()
    if args.account_copy and not args.baseline:
        peer = Peer()
        try:
            check('Import fixture local account initialized', peer.login(13001))
        finally:
            peer.close()
        stop(proc); proc = None
        command = [str(Path(args.server).resolve()), '--config', str(config), '--import-account-copy', str(Path(args.account_copy).resolve()), '--uid', '13001', '--allow-partial']
        imported = subprocess.run(command, capture_output=True, encoding='utf-8', errors='replace')
        events.append(dict(command=command, stdout=imported.stdout, stderr=imported.stderr, exit_status=imported.returncode))
        check('Published offline importer succeeds', imported.returncode == 0)
        summary = json.loads(imported.stdout)['result']
        proc = start()
        for wide in [False, True]:
            label = '64-bit imported' if wide else '32-bit imported'
            peer = Peer(wide)
            try:
                check(label + ' login', peer.login(13001))
                a = peer.request(24, integer(1, 0), timeout=10)
                eq = peer.request(26, b''.join(integer(i, 0) for i in range(1, 5)), timeout=10)
                check(label + ' complete fragmented roster', a is not None and a[1].get(3) == [1] and len(a[1].get(2, [])) == summary['avatars'])
                check(label + ' complete fragmented equipment', eq is not None and eq[1].get(5) == [1] and len(eq[1].get(2, [])) == summary['weapons'] and len(eq[1].get(3, [])) == summary['stigmata'])
                ids = [fields(raw)[1][0] for raw in a[1][2]]
                selected = peer.request(24, integer(1, ids[-1]))
                check(label + ' selected imported avatar', selected is not None and selected[1].get(3) == [0] and len(selected[1].get(2, [])) == 1 and fields(selected[1][2][0])[1] == [ids[-1]])
                team = ids[:2] + [ids[-1]]
                begin = peer.request(43, integer(1, 10101) + b''.join(integer(2, i) for i in team), timeout=10)
                check(label + ' imported three-member stage begin', begin is not None and begin[1].get(1) == [0])
                end = peer.request(45, blob(1, integer(1, 10101)) + blob(2, label), timeout=10)
                check(label + ' imported team settles with full roster push', end is not None and end[1].get(1) == [0] and len(peer.last_snapshots.get(25, {}).get(2, [])) == summary['avatars'])
            finally:
                peer.close()
        stop(proc); proc = start(); peer = Peer()
        try:
            check('Imported inventory survives host restart', peer.login(13001) and len(peer.request(24, timeout=10)[1][2]) == summary['avatars'])
        finally:
            peer.close()
        peer = Peer()
        try:
            check('Imported inventory remains account isolated', peer.login(10002) and len(peer.request(24)[1][2]) == 1)
        finally:
            peer.close()
    if args.observe_gm or args.observe_shop or args.observe_gameplay or args.observe_companions or args.observe_economy or args.observe_systems:
        def gm_http(uid,body=None):
            req=urllib.request.Request(f'http://127.0.0.1:{health}/api/gm/player/{uid}', data=json.dumps(body).encode() if body else None,headers={'Content-Type':'application/json','X-BH3-GM':'1'})
            with opener.open(req,timeout=5) as r:return json.load(r)
        for wide,uid in ([(False,14001),(True,14002)] if args.observe_gm else []):
            p=Peer(wide)
            try:
                check(f'GM {uid} login',p.login(uid))
                gm_contracts.extend(observe_gm(p,str(uid),uid,fields,integer,blob,gm_http,gm_contexts))
            finally:p.close()
        stop(proc);proc=start()
        for wide,uid in ([(False,14001),(True,14002)] if args.observe_gm else []):
            p=Peer(wide)
            try:
                check(f'GM {uid} restart login',p.login(uid))
                gm_contracts.extend(observe_gm(p,str(uid)+' restart',uid,fields,integer,blob,gm_http,gm_contexts,True))
            finally:p.close()
    if args.observe_shop:
        for wide,uid in [(False,15001),(True,15002)]:
            p=Peer(wide)
            try:
                check(f'Shop {uid} login',p.login(uid))
                shop_contracts.extend(observe_shop(p,str(uid),uid,fields,integer,blob,gm_http,shop_contexts))
            finally:p.close()
        stop(proc);proc=start()
        for wide,uid in [(False,15001),(True,15002)]:
            p=Peer(wide)
            try:
                check(f'Shop {uid} restart login',p.login(uid))
                shop_contracts.extend(observe_shop(p,str(uid)+' restart',uid,fields,integer,blob,gm_http,shop_contexts,True))
            finally:p.close()
    if args.observe_mall:
        for wide,uid in [(False,16001),(True,16002)]:
            p=Peer(wide)
            try:
                check(f'Mall {uid} login',p.login(uid))
                mall_contracts.extend(observe_mall(p,str(uid),fields,integer,blob,mall_contexts))
            finally:p.close()
        stop(proc);proc=start()
        for wide,uid in [(False,16001),(True,16002)]:
            p=Peer(wide)
            try:
                check(f'Mall {uid} restart login',p.login(uid))
                mall_contracts.extend(observe_mall(p,str(uid),fields,integer,blob,mall_contexts,True))
            finally:p.close()
    if args.observe_gameplay:
        for wide,uid in [(False,17001),(True,17002)]:
            p=Peer(wide)
            try:
                check(f'Gameplay {uid} login',p.login(uid))
                gameplay_contracts.extend(observe_gameplay(p,str(uid),uid,fields,integer,blob,gm_http,gameplay_contexts))
            finally:p.close()
        stop(proc);proc=start()
        for wide,uid in [(False,17001),(True,17002)]:
            p=Peer(wide)
            try:
                check(f'Gameplay {uid} restart login',p.login(uid))
                gameplay_contracts.extend(observe_gameplay(p,str(uid)+' restart',uid,fields,integer,blob,gm_http,gameplay_contexts,True))
            finally:p.close()
    if args.observe_companions:
        for wide,uid in [(False,18001),(True,18002)]:
            p=Peer(wide)
            try:
                check(f'Companions {uid} login',p.login(uid))
                companion_contracts.extend(observe_companions(p,str(uid),uid,fields,integer,blob,gm_http,companion_contexts))
            finally:p.close()
        stop(proc);proc=start()
        for wide,uid in [(False,18001),(True,18002)]:
            p=Peer(wide)
            try:
                check(f'Companions {uid} restart login',p.login(uid))
                companion_contracts.extend(observe_companions(p,str(uid)+' restart',uid,fields,integer,blob,gm_http,companion_contexts,True))
            finally:p.close()
    if args.observe_economy:
        for wide,uid in [(False,19001),(True,19002)]:
            p=Peer(wide)
            try:
                check(f'Economy {uid} login',p.login(uid))
                economy_contracts.extend(observe_economy(p,str(uid),uid,fields,integer,blob,gm_http,economy_contexts))
            finally:p.close()
        stop(proc);proc=start()
        for wide,uid in [(False,19001),(True,19002)]:
            p=Peer(wide)
            try:
                check(f'Economy {uid} restart login',p.login(uid))
                economy_contracts.extend(observe_economy(p,str(uid)+' restart',uid,fields,integer,blob,gm_http,economy_contexts,True))
            finally:p.close()
    if args.observe_systems:
        for wide,uid in [(False,20001),(True,20002)]:
            p=Peer(wide)
            try:
                check(f'Systems {uid} login',p.login(uid))
                systems_contracts.extend(observe_systems(p,str(uid),uid,fields,integer,blob,gm_http,systems_contexts))
            finally:p.close()
        stop(proc);proc=start()
        for wide,uid in [(False,20001),(True,20002)]:
            p=Peer(wide)
            try:
                check(f'Systems {uid} restart login',p.login(uid))
                systems_contracts.extend(observe_systems(p,str(uid)+' restart',uid,fields,integer,blob,gm_http,systems_contexts,True))
            finally:p.close()
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
        client_contracts=client_contracts, overall_contracts=overall_contracts, init_selector_contracts=init_selector_contracts,
        campaign_contracts=campaign_contracts, world_map_contracts=world_map_contracts, chapter_unlock_contracts=chapter_unlock_contracts,
        team_prepare_contracts=team_prepare_contracts, settlement_contracts=settlement_contracts,
        pjms_current_contracts=pjms_current_contracts, act_reward_contracts=act_reward_contracts, gm_contracts=gm_contracts,shop_contracts=shop_contracts,mall_contracts=mall_contracts,gameplay_contracts=gameplay_contracts,companion_contracts=companion_contracts,economy_contracts=economy_contracts,systems_contracts=systems_contracts)
    record['passed'] &= all(c['passed'] for k,v in record.items() if k.endswith('contracts') for c in v)
    for contract in act_reward_contracts:
        print('ACT_REWARD_CONTRACT', json.dumps(contract), flush=True)
    for contract in pjms_current_contracts:
        print('PJMS_CURRENT_CONTRACT', json.dumps(contract), flush=True)
    for contract in settlement_contracts:
        print('SETTLEMENT_CONTRACT', json.dumps(contract), flush=True)
    for contract in team_prepare_contracts:
        print('TEAM_PREPARE_CONTRACT', json.dumps(contract), flush=True)
    for contract in chapter_unlock_contracts:
        print('CHAPTER_UNLOCK_CONTRACT', json.dumps(contract), flush=True)
    for contract in world_map_contracts:
        print('WORLD_MAP_CONTRACT', json.dumps(contract), flush=True)
    for contract in campaign_contracts:
        print('CAMPAIGN_CONTRACT', json.dumps(contract), flush=True)
    for contract in init_selector_contracts:
        print('INIT_SELECTOR_CONTRACT', json.dumps(contract), flush=True)
    for contract in overall_contracts:
        print('OVERALL_CONTRACT', json.dumps(contract), flush=True)
    Path(args.report).write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding='utf-8')
