"""Independent wire checks for the 9.1 omitted-WIN settlement and its next stage."""


def observe(peer, label, uid, fields, integer, blob, contexts, restart=False):
    checks = []

    def record(name, passed, body=b''):
        checks.append(dict(name=f'{label}: {name}', passed=bool(passed), request_hex=body.hex(),
                           response_body_hex=peer.last_response_body.hex()))

    def success(reply, command):
        return reply is not None and reply[0] == command and reply[2] == uid and reply[1].get(1) == [0]

    def readable(reply):
        # The 9.1 enum reader accepts only 1..4 when field 22 is present.
        return reply is not None and all(x in (1, 2, 3, 4) for x in reply[1].get(22, []))

    if restart:
        body, saved, wallet = contexts[uid]
        reply = peer.request(45, body)
        record('omitted-win receipt survives restart', success(reply, 46) and peer.last_response_body.hex() == saved, body)
        record('restarted receipt omits nonexistent line-enhance bonus', success(reply, 46) and 19 not in reply[1], body)
        reply = peer.request(41)
        stages = {fields(s)[1][0]: fields(s) for s in reply[1].get(2, [])}
        record('only the won first stage is complete after restart', stages.get(10101, {}).get(16) == [1]
               and stages.get(10102, {}).get(16) == [0])
        reply = peer.request(10)
        record('replayed settlement preserves wallet', reply[1].get(6) == wallet)
        return checks

    for command, body, expected in [(131, bytes.fromhex('0a02086528f54e'), 0),
                                     (154, bytes.fromhex('0865100a1801'), 1), (7702, b'', 0)]:
        reply = peer.request(command, body, timeout=1)
        record(f'post-battle {command} callback receives declared result', reply is not None
               and reply[0] == command + 1 and reply[2] == uid and reply[1].get(1) == [expected], body)
    begin_body = integer(1, 10101) + integer(2, 101)
    begin = peer.request(43, begin_body)
    record('first stage can begin', success(begin, 44), begin_body)
    next_body = integer(1, 10102) + integer(2, 101)
    reply = peer.request(43, next_body)
    record('second stage remains locked before first win', reply is not None and reply[1].get(1, [0])[0] != 0, next_body)
    invalid = blob(1, integer(1, 10101) + integer(2, 0)) + blob(2, 'client91-fixture')
    reply = peer.request(45, invalid)
    record('explicit zero rejected with decodable error response', reply is not None
           and reply[1].get(1, [0])[0] != 0 and readable(reply), invalid)
    # Client writer at 0xf7bc337 skips field 2 for WIN=1. This is a derived fixture, not a capture.
    body = blob(1, bytes.fromhex('08f54e30003001300250d8ad03')) + blob(2, 'client91-fixture')
    reply = peer.request(45, body)
    saved = peer.last_response_body.hex()
    record('absent end_status settles as win', success(reply, 46) and readable(reply)
           and reply[1].get(22) == [1] and reply[1].get(37) == [1], body)
    record('win omits nonexistent bonus while retaining earned rewards', success(reply, 46)
           and 19 not in reply[1] and reply[1].get(5) == [750] and len(reply[1].get(6, [])) == 3, body)
    reply = peer.request(41, integer(1, 10101))
    first = fields(reply[1][2][0]) if reply and reply[1].get(2) else {}
    record('successful settlement publishes completed first stage', first.get(16) == [1] and first.get(2) == [1])
    reply = peer.request(43, next_body)
    record('next stage can begin immediately after settlement', success(reply, 44), next_body)
    wallet = peer.request(10)[1].get(6)
    reply = peer.request(45, body)
    same_receipt = success(reply, 46) and peer.last_response_body.hex() == saved
    same_wallet = peer.request(10)[1].get(6) == wallet
    record('retry does not reward twice or replace next run', same_receipt and same_wallet, body)
    quit_body = blob(1, integer(1, 10102) + integer(2, 4)) + blob(2, 'exit-second-stage')
    reply = peer.request(45, quit_body)
    record('explicit exit retains loss semantics', success(reply, 46) and reply[1].get(22) == [4]
           and reply[1].get(8) == [0], quit_body)
    record('exit does not advertise a reward dialog', success(reply, 46) and 19 not in reply[1], quit_body)
    malformed = blob(1, b'\xff')
    reply = peer.request(45, malformed)
    record('malformed body returns a decodable error', reply is not None and readable(reply)
           and reply[1].get(1, [0])[0] != 0, malformed)
    contexts[uid] = body, saved, wallet
    return checks
