"""Replay the 9.1 chapter-group clear/update/unlock rule on actual wire responses."""


def client_site_can_enter(data, fields):
    # MainStoryPart.Update clears every site's status to LOCKED=1 for IsAll.
    # The supported route is ChapterGroupConfig[1] -> site 1 -> chapter 1.
    # AGCEKIDPJLK.get_IsUnlocked tests status >= UNLOCKED=2; chapter 1 has no login-day lock.
    status = 1
    for raw_group in data.get(2, []):
        group = fields(raw_group)
        if group.get(1) != [1]:
            continue
        for raw_site in group.get(2, []):
            site = fields(raw_site)
            if site.get(1) == [1] and site.get(2) == [1]:
                status = site.get(3, [1])[0]
    return status in (2, 3), status


def observe(peer, framing, uid, fields, integer):
    results = []
    for label, body, is_all, wanted in [('default/full', b'', 1, True),
                                      ('zero/full', integer(1, 0), 1, True),
                                      ('selected/first', integer(1, 1), 0, True),
                                      ('selected/unsupported', integer(1, 17), 0, False)]:
        reply = peer.request(1660, body)
        data = reply[1] if reply else {}
        can_enter, status = client_site_can_enter(data, fields)
        selector = 0 if not body else fields(body)[1][0]
        passed = (reply is not None and reply[0] == 1661 and reply[2] == uid and data.get(1) == [0]
                  and data.get(3) == [is_all] and data.get(4) == [selector])
        if wanted:
            passed = passed and can_enter and status == 2 and (len(data.get(2, [])) >= 1 if is_all else len(data.get(2, [])) == 1)
        else:
            passed = passed and not can_enter and not data.get(2)
        results.append(dict(name=f'{framing}: chapter group {label}', passed=bool(passed), command=1660,
                            request_hex=body.hex(), response_body_hex=peer.last_response_body.hex() if reply else None,
                            client_first_chapter_can_enter=can_enter, site_status=status))
    return results
