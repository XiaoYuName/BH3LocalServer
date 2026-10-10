"""Wire requirements for 9.1's ordinary-stage return task; no game execution."""


def observe(peer, label, uid, fields):
    checks = []

    def record(name, passed, body):
        checks.append(dict(name=f'{label}: {name}', command=7702, request_hex='',
                           response_body_hex=body.hex(), passed=bool(passed)))

    peer.request(10)
    wallet = peer.last_response_body
    peer.request(41)
    stages = peer.last_response_body
    main = peer.request(7706)
    response = peer.request(7702)
    body = peer.last_response_body
    success = response is not None and response[0] == 7703 and response[2] == uid and response[1].get(1) == [0]
    world = response[1].get(2, []) if response else []
    decoded = fields(world[0]) if len(world) == 1 else None
    record('return query succeeds instead of failing the client task', success, body)
    record('inactive world object is present with explicit world_id zero', decoded == {1: [0]}, body)
    record('current world agrees with startup snapshot', success and len(world) == 1
           and main is not None and main[1].get(2) == world, body)
    peer.request(7702)
    record('repeated query is identical and remains usable', success and peer.last_response_body == body, body)
    peer.request(10)
    wallet_unchanged = peer.last_response_body == wallet
    peer.request(41)
    record('query preserves earned wallet and stage progress', wallet_unchanged and peer.last_response_body == stages, body)
    return checks
