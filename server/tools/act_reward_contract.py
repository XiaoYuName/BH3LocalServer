"""Independent wire replay of the reward cache consumed by the 9.1 UI."""


def observe(peer, label, uid, fields, integer, blob, contexts, batch=True, restart=False):
    checks = []

    def record(name, passed, request=b''):
        checks.append(dict(name=f'{label}: {name}', passed=bool(passed), request_hex=request.hex(),
                           response_body_hex=peer.last_response_body.hex(), response_ids=list(peer.last_response_ids)))

    def claimed(snapshot):
        for raw in (snapshot or {}).get(2, []):
            act = fields(raw)
            if act.get(1) == [101] and act.get(2) == [1]:
                return act.get(3, [])
        return []

    def success(reply, command):
        return reply is not None and reply[0] == command and reply[2] == uid and reply[1].get(1) == [0]

    def inventories():
        peer.request(10)
        wallet = peer.last_response_body
        peer.request(26)
        return wallet, peer.last_response_body

    def retry(request, indices, expected_inventory):
        result = peer.request(458, request)
        record('duplicate refuses a second grant', result is not None and result[0] == 459
               and result[1].get(1) == [2] and not result[1].get(5), request)
        record('duplicate repairs claimed cache before callback', peer.last_response_ids == [457, 459]
               and claimed(peer.last_snapshots.get(457)) == indices, request)
        record('duplicate preserves wallet and material counts', inventories() == expected_inventory, request)

    if restart:
        request, indices, inventory = contexts[uid]
        reply = peer.request(456)
        record('claimed flags survive restart', claimed(reply[1]) == indices)
        retry(request, indices, inventory)
        return checks

    enough_stars = True
    for stage in [10101, 10102, 10105]:
        begin = peer.request(43, integer(1, stage) + integer(2, 101))
        enough_stars &= success(begin, 44)
        end = blob(1, integer(1, stage) + b''.join(integer(6, i) for i in range(3))) + blob(2, f'act-reward-{uid}-{stage}')
        enough_stars &= success(peer.request(45, end), 46)
    record('three stages supply nine challenge stars', enough_stars)
    reply = peer.request(456)
    record('reward cache initially unclaimed', success(reply, 457) and claimed(reply[1]) == [])
    indices = [1, 2, 3] if batch else [1]
    request = integer(1, 101) + integer(2, 1) + b''.join(integer(4 if batch else 3, i) for i in indices)
    result = peer.request(458, request)
    record('eligible reward granted with successful indices', success(result, 459) and result[1].get(6) == indices, request)
    record('claim snapshot arrives before UI callback', peer.last_response_ids[:2] == [457, 459], request)
    record('UI callback sees all requested tiers claimed', claimed(peer.last_snapshots.get(457)) == indices, request)
    inventory = inventories()
    query = peer.request(456)
    record('fresh query matches committed claim flags', claimed(query[1]) == indices)
    retry(request, indices, inventory)
    # Invalid difficulty must not masquerade as a successful claim or send a claim update.
    invalid = integer(1, 101) + integer(2, 99) + integer(3, 1)
    result = peer.request(458, invalid)
    record('invalid request keeps failure response and no claim snapshot', result is not None
           and result[1].get(1) == [1] and peer.last_response_ids == [459], invalid)
    contexts[uid] = request, indices, inventory
    return checks
