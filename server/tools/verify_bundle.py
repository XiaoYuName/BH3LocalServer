"""Test the distributable ZIP after relocation, without touching player data."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import zipfile

parser=argparse.ArgumentParser()
parser.add_argument('--archive',required=True,type=Path)
parser.add_argument('--report',required=True,type=Path)
args=parser.parse_args()
temp=Path(tempfile.mkdtemp(prefix='bh3-relocated-package-'))
events=[]
with zipfile.ZipFile(args.archive) as archive:
    assert archive.testzip() is None
    names=[n.replace('\\','/') for n in archive.namelist()]
    assert all('..' not in Path(n).parts and not Path(n).is_absolute() for n in names)
    assert all('data' not in [p.lower() for p in Path(n).parts] and 'logs' not in [p.lower() for p in Path(n).parts] for n in names)
    for required in ['Launcher/BH3.Launcher.exe','Server/BH3.Server.exe','Server/config/server.json','Start.bat','BH3.package.json']:
        assert required in names,required
    archive.extractall(temp)
exe=temp/'Launcher/BH3.Launcher.exe'

def decode(raw):
    try: return raw.decode('utf-8')
    except UnicodeDecodeError: return raw.decode('gb18030')

passed=True
for flag,filename in [('--self-test','launcher.json'),('--bundle-test','bundle.json')]:
    report=temp/filename
    command=[str(exe),flag,'--report',str(report)]
    result=subprocess.run(command,capture_output=True,timeout=90)
    body=json.loads(report.read_text('utf-8')) if report.exists() else None
    events.append(dict(command=command,stdout=decode(result.stdout),stderr=decode(result.stderr),exit_status=result.returncode,report=body))
    print(events[-1]['stdout'],flush=True)
    passed=passed and result.returncode==0 and body is not None and body['passed']
    if not passed: break
record=dict(passed=passed,archive=str(args.archive.resolve()),archive_sha256=hashlib.sha256(args.archive.read_bytes()).hexdigest(),
    extracted_directory=str(temp),archive_has_player_data=False,events=events)
args.report.parent.mkdir(parents=True,exist_ok=True)
args.report.write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
print('RELOCATED_PACKAGE', 'PASS' if passed else 'FAIL')
raise SystemExit(0 if passed else 1)
