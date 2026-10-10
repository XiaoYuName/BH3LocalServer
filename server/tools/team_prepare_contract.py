"""Replay 9.1's prepare-page member lookup on independent wire responses.

PlayerModule copies every avatar_id_list entry, including zero; SetupView retries
47 when IsTeamMemberValid cannot resolve any entry in the player's avatar data.
See docs/TEAM_PREPARE_20261009.md for the static call chain and its limits.
"""


def client_members_valid(members, owned):
    return all(member in owned for member in members)


def observe(peer, framing, uid, fields, integer, blob):
    results = []
    avatars = peer.request(24, integer(1, 0))
    owned = {fields(raw)[1][0] for raw in (avatars[1].get(2, []) if avatars else [])}

    def query(label):
        reply = peer.request(47)
        teams = [fields(raw) for raw in (reply[1].get(2, []) if reply else [])]
        normal = [team for team in teams if team.get(1) == [1]]
        members = normal[0].get(2, []) if len(normal) == 1 else []
        valid = client_members_valid(members, owned)
        ready = (reply is not None and reply[0] == 48 and reply[2] == uid and reply[1].get(1) == [0]
                 and len(normal) == 1 and 1 <= len(members) <= 3 and valid)
        row = dict(name=f'{framing}: {label}', passed=bool(ready), command=47, request_hex='',
                   response_body_hex=peer.last_response_body.hex() if reply else None,
                   members=members, owned=sorted(owned), client_members_valid=valid,
                   client_would_request_team_again=not valid)
        results.append(row)
        return row

    initial = query('preparation roster resolves to owned avatars')
    repeated = query('repeated query remains ready')
    repeated['passed'] &= repeated['response_body_hex'] == initial['response_body_hex']
    results.append(dict(name=f'{framing}: client lookup rejects zero and unknown members',
                        passed=bool(owned) and not client_members_valid([min(owned), 0, 0], owned)
                        and not client_members_valid([999999], owned),
                        negative_control_members=[[min(owned) if owned else None, 0, 0], [999999]]))
    if owned:
        # UpdateAvatarTeamNotify has no reply. The following ordered query is the barrier.
        padded = blob(1, integer(1, 1) + integer(2, min(owned)) + integer(2, 0) + integer(2, 0))
        assert peer.request(49, padded, timeout=0.15) is None
        updated = query('saved team omits placeholders')
        updated['update_request_hex'] = padded.hex()
        updated['passed'] &= updated['members'] == [min(owned)]
        invalid = blob(1, integer(1, 1) + integer(2, 999999))
        assert peer.request(49, invalid, timeout=0.15) is None
        rejected = query('unowned team update preserves playable roster')
        rejected['update_request_hex'] = invalid.hex()
        rejected['passed'] &= rejected['response_body_hex'] == updated['response_body_hex']
    return results
