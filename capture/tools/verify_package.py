"""Replay independently encoded UDP/KCP captures through a relocated single EXE."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--archive', required=True, type=Path)
parser.add_argument('--report', required=True, type=Path)
args = parser.parse_args()
work = Path(tempfile.mkdtemp(prefix='bh3-capture-package-')).resolve()
checks, events = [], []


def check(name, ok):
    checks.append(dict(name=name, passed=bool(ok)))
    assert ok, name


def varint(value):
    result = bytearray()
    while value > 127:
        result.append((value & 127) | 128)
        value >>= 7
    return bytes(result + bytes([value]))


def integer(field, value): return varint(field << 3) + varint(value)
def blob(field, body): return varint((field << 3) | 2) + varint(len(body)) + body


def game(command, body):
    prefix = struct.pack('>IHHIIIIH', 0x01234567, 1, 0, 0, 10001, 0, 0, 0)
    return prefix + struct.pack('>HHI', command, 0, len(body)) + body + bytes.fromhex('89abcdef')


def kcp(sequence, body):
    return struct.pack('>Q', 1234) + struct.pack('<BBHIIII', 81, 0, 256, 0, sequence, 0, len(body)) + body


avatar_body = integer(1, 0) + blob(2, integer(1, 101) + integer(3, 80) + integer(6, 71)) + integer(3, 1)
equipment_body = integer(1, 0) + blob(2, integer(1, 71) + integer(2, 20001) + integer(3, 50)) + integer(5, 1)
main_body = integer(1, 0) + integer(3, 88) + integer(28, 1)
handshake = struct.pack('>IIIII', 255, 0, 0, 91, 0xffffffff)
response = struct.pack('>II', 325, 0) + struct.pack('<I', 1234) + struct.pack('>II', 91, 0x14514545)
packets = [(False, handshake), (True, response), (False, kcp(0, game(4, blob(3, b'fixture-secret-token')))),
           (True, kcp(0, game(11, main_body))), (True, kcp(1, game(25, avatar_body))), (True, kcp(2, game(27, equipment_body)))]


def pcap(path, values):
    def block(kind, body):
        length = len(body) + 12
        return struct.pack('<II', kind, length) + body + struct.pack('<I', length)
    output = block(0x0a0d0d0a, struct.pack('<IHHq', 0x1a2b3c4d, 1, 0, -1)) + block(1, struct.pack('<HHI', 101, 0, 65535))
    for server, body in values:
        source, destination = (b'\x0a\0\0\2', b'\x0a\0\0\1') if server else (b'\x0a\0\0\1', b'\x0a\0\0\2')
        udp = struct.pack('>HHHH', 16100 if server else 55000, 55000 if server else 16100, len(body) + 8, 0) + body
        raw = struct.pack('>BBHHHBBH4s4s', 0x45, 0, 20 + len(udp), 0, 0, 64, 17, 0, source, destination) + udp
        stamp = 1700000000000000
        packet = struct.pack('<IIIII', 0, stamp >> 32, stamp & 0xffffffff, len(raw), len(raw)) + raw
        packet += b'\0' * (-len(packet) % 4)
        output += block(6, packet)
    path.write_bytes(output)


passed = False
try:
    with zipfile.ZipFile(args.archive) as archive:
        check('Distribution contains only the standalone executable', archive.namelist() == ['BH3Capture.exe'])
        archive.extractall(work / 'portable')
    exe = work / 'portable/BH3Capture.exe'
    for name, values, expected in [('complete', packets, 0), ('missing-equipment', packets[:-1], 2),
                                    ('unrecognized', packets[:2] + [(True, kcp(0, b'unknown-wire-format'))], 2)]:
        source = work / (name + '.pcapng'); pcap(source, values)
        output = work / name
        command = [str(exe), '--inspect', str(source), '--output', str(output)]
        result = subprocess.run(command, capture_output=True, timeout=45)
        events.append(dict(command=command, stdout=result.stdout.decode('utf-8', errors='replace'),
                           stderr=result.stderr.decode('utf-8', errors='replace'), exit_status=result.returncode))
        account = json.loads((output / 'account-copy.json').read_text('utf-8'))
        check(name + ' exit/status reflect captured completeness', result.returncode == expected and account['CoreSnapshotComplete'] == (expected == 0))
        if name == 'complete':
            records = {r['CommandId']: r for r in account['Accounts'][0]['Responses']}
            check('Independent response bytes copied without mutation', base64.b64decode(records[25]['BodyBase64']) == avatar_body
                  and base64.b64decode(records[27]['BodyBase64']) == equipment_body)
            check('Authentication response/request excluded from account copy', set(records) == {11, 25, 27}
                  and 'fixture-secret-token' not in (output / 'account-copy.json').read_text('utf-8'))
            previous = hashlib.sha256((output / 'account-copy.json').read_bytes()).hexdigest()
            repeated = subprocess.run(command, capture_output=True, timeout=45)
            events.append(dict(command=command, stdout=repeated.stdout.decode('utf-8', errors='replace'),
                               stderr=repeated.stderr.decode('utf-8', errors='replace'), exit_status=repeated.returncode))
            check('Offline analysis refuses to overwrite previous export', repeated.returncode == 1
                  and previous == hashlib.sha256((output / 'account-copy.json').read_bytes()).hexdigest())
    passed = True
finally:
    report = dict(passed=passed, checks=checks, events=events, work=str(work), archive=str(args.archive.resolve()),
                  archive_sha256=hashlib.sha256(args.archive.read_bytes()).hexdigest(), real_capture_started=False, game_operated=False)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')
print(f'{len(checks)} package checks passed. No game or real capture started.')
