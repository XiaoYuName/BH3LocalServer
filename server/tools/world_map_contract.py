"""Independent wire checks for the scheduled main-story container consumed by 9.1.

The route IDs come from the reference tables, not the response implementation.
This checks response prerequisites; it does not run or emulate the game's UI.
"""
import json
from pathlib import Path
import time


def observe(peer, framing, uid, fields, integer):
    fixture = json.loads((Path(__file__).resolve().parents[1] /
        'tests/BH3.Game.Tests/Fixtures/world-map-main-story.json').read_text('utf-8'))
    observations = []
    def request(command, body=b''):
        reply = peer.request(command, body)
        wire = dict(command=command, request_hex=body.hex(),
                    response_body_hex=peer.last_response_body.hex() if reply else None)
        ok = reply is not None and reply[0] == command + 1 and reply[2] == uid and reply[1].get(1) == [0]
        return (reply[1] if ok else {}), wire
    def record(name, passed, wire, **detail):
        observations.append(dict(name=f'{framing}: {name}', passed=bool(passed), **wire, **detail))

    data, wire = request(1012)
    maps = [fields(item) for item in data.get(2, [])]
    main = [m for m in maps if m.get(1) == [fixture['worldMapId']] and m.get(8) == [fixture['scheduleId']]]
    now = int(time.time())
    opened = [m for m in main if m.get(7, [0])[0] <= m.get(2, [0])[0] <= now < m.get(3, [0])[0]]
    record('main-story container has a current schedule', len(opened) == 1, wire,
           expected_world_map=fixture['worldMapId'], resolved_entry=fixture['entryId'] if opened else None,
           map_count=len(maps), observed_at=now)
    repeated, repeat_wire = request(1012)
    record('repeated schedule query preserves container', bool(opened) and repeated == data, repeat_wire)
    rec, rec_wire = request(1713)
    permanent = [fields(item) for item in rec.get(3, [])]
    record('permanent recommendation resolves to an open main-story container', bool(opened) and
           any(item.get(1) == [fixture['worldMapId']] and not item.get(3) for item in permanent), rec_wire)
    chapters, chapter_wire = request(965)
    record('chapter-one metadata is available for the main-story entry',
           any(fields(item).get(1) == [fixture['chapterId']] for item in chapters.get(2, [])), chapter_wire)
    stages, stage_wire = request(41, integer(1, fixture['firstStageId']))
    record('first chapter stage can be selected',
           len(stages.get(2, [])) == 1 and fields(stages[2][0]).get(1) == [fixture['firstStageId']], stage_wire)
    return observations
