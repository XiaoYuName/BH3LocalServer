"""Read-only source routing probe, distinct from runtime/real-client acceptance."""
import json
import sys
import xml.etree.ElementTree as ET
import zipfile

with zipfile.ZipFile(sys.argv[1]) as archive:
    assert archive.testzip() is None
    names=set(archive.namelist())
    native='desktop/src/BH3.Launcher/Program.cs' in names
    if 'server/BH3.slnx' not in names:
        result=dict(native_launcher=native, formal_game_server=False, server_layers=[])
    else:
        root=ET.fromstring(archive.read('server/BH3.slnx'))
        projects=[p.attrib['Path'] for p in root.iter('Project')]
        layers=['Protocol','Game','Persistence','Server']
        for layer in layers:
            assert f'src/BH3.{layer}/BH3.{layer}.csproj' in projects
        source=archive.read('server/src/BH3.Server/Program.cs').decode('utf-8-sig')
        assert 'new ServerRuntime(config)' in source
        assert 'Task.Run(() => WatchInput(stop))' in source
        assert json.loads(archive.read('server/config/server.json'))['transportMode']=='handshake-only'
        result=dict(native_launcher=native,formal_game_server=True,server_layers=layers,entry='BH3.Server.Program.Main -> ServerRuntime.StartAsync',transport='handshake-only')
    print(json.dumps(result,ensure_ascii=False,sort_keys=True))
