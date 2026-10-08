"""Extend the previous desktop delivery with the server source and evidence."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

root=Path(__file__).resolve().parents[1]
evidence=root/'evidence/skeleton'
previous=root.parent/'desktop/verification'
git=Path(r'D:\Program Files\Git\cmd\git.exe')
bash=Path(r'D:\Program Files\Git\bin\bash.exe')
events=[]
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def run(label,args,cwd=None,expected=0):
    result=subprocess.run([str(a) for a in args],cwd=cwd,capture_output=True,encoding='utf-8',errors='replace')
    event=dict(label=label,command=[str(a) for a in args],cwd=str(cwd or root),stdout=result.stdout,stderr=result.stderr,exit_status=result.returncode)
    events.append(event)
    assert result.returncode==expected,event
    return result.stdout

baseline=evidence/'BASELINE.zip'
modified=evidence/'MODIFIED_FILE.zip'
diff=evidence/'DIFF_FILE.patch'
verification=evidence/'VERIFICATION.txt'
rollback=evidence/'ROLLBACK.sh'
shutil.copyfile(previous/'MODIFIED_FILE.zip',baseline)
with zipfile.ZipFile(baseline) as archive:
    entries={name:archive.read(name) for name in archive.namelist()}
for p in root.rglob('*'):
    if not p.is_file(): continue
    relative=p.relative_to(root)
    if set(relative.parts).intersection({'bin','obj','dist','data','logs','evidence','__pycache__'}): continue
    entries['server/'+relative.as_posix()]=p.read_bytes()
entries['PROJECT_INDEX.md']=(root.parent/'PROJECT_INDEX.md').read_bytes()
with zipfile.ZipFile(modified,'w') as archive:
    for name,data in sorted(entries.items()):
        info=zipfile.ZipInfo(name,(2026,10,9,0,0,0)); info.compress_type=zipfile.ZIP_DEFLATED; info.external_attr=0o100644<<16
        archive.writestr(info,data)
probe=[sys.executable,'-X','utf8','-B',root/'tools/verify_source_package.py']
run('BASELINE',[*probe,baseline]); run('MODIFIED',[*probe,modified])
work=evidence/'work'; before=work/'before'; after=work/'after'
before.mkdir(parents=True,exist_ok=True); after.mkdir(parents=True,exist_ok=True)
shutil.copyfile(baseline,before/'source.zip'); shutil.copyfile(modified,after/'source.zip')
patch=run('GENERATE_DIFF',[git,'diff','--no-index','--binary','--','before/source.zip','after/source.zip'],work,1)
diff.write_text(patch.replace('a/before/source.zip','a/source.zip').replace('b/after/source.zip','b/source.zip'),encoding='utf-8',newline='\n')
run('ISOLATE_RECONSTRUCTION',[git,'init','--quiet'],before)
run('RECONSTRUCT',[git,'apply','--binary',diff],before)
assert sha(before/'source.zip')==sha(modified)
# Same portable, copy-only rollback contract as the desktop delivery.
rollback.write_bytes((previous/'ROLLBACK.sh').read_bytes()); rollback.chmod(0o755)
restored=work/'restored-copy.zip'; shutil.copyfile(modified,restored)
run('EXECUTE_ROLLBACK',[bash,rollback.as_posix(),restored.as_posix()])
run('ROLLBACK',[*probe,restored]); assert sha(restored)==sha(baseline)
shutil.copyfile(baseline,before/'source.zip')
run('REAPPLY',[git,'apply','--binary',diff],before)
assert sha(before/'source.zip')==sha(modified)
manifest=json.loads((evidence/'baseline-manifest.json').read_text('utf-8-sig'))
assert all(sha(Path(p['path']))==p['sha256'] for p in manifest)
old_roles=[previous/name for name in ['MODIFIED_FILE.zip','DIFF_FILE.patch','VERIFICATION.txt','ROLLBACK.sh']]
previous_roles=[dict(path=str(p),sha256=sha(p),bytes=len(p.read_bytes())) for p in old_roles]
runtime=json.loads((evidence/'published-smoke.json').read_text('utf-8'))
assert runtime['passed'] and len(runtime['checks'])==12
release_manifest=[dict(path=p.relative_to(root/'dist/server').as_posix(),sha256=sha(p),bytes=p.stat().st_size) for p in sorted((root/'dist/server').rglob('*')) if p.is_file()]
record=dict(target=str(root),change='Add BH3.Server.Program.Main -> ServerRuntime.StartAsync and four-layer game-server skeleton',
    scope='Source package extends the prior desktop artifact; existing desktop four-role set is reopened and preserved. BASELINE/MODIFIED/ROLLBACK are ZIP integrity and source-entry observations, not game client acceptance.',
    previous_roles=previous_roles,roles={p.stem:str(p) for p in [modified,diff,verification,rollback]},
    events=events,originals=manifest,baseline_sha256=sha(baseline),restored_sha256=sha(restored),modified_sha256=sha(modified),
    build_and_unit_tests=dict(command='test.bat',exit_status=0,passed=33,stdout=(evidence/'test-output.txt').read_text('utf-8-sig'),stderr=''),
    publish=dict(command='publish.bat',exit_status=0,stdout=(evidence/'publish-output.txt').read_text('utf-8-sig'),stderr=''),
    published_probe=runtime,published_files=release_manifest,
    corrections=[dict(source='attempt-01-analyzer.txt',exit_status=1,fix='Use the xUnit Assert.Single predicate overload'),dict(source='attempt-02-path-assertion.txt',exit_status=1,fix='Use native Path.Combine components in the Windows path expectation'),dict(source='attempt-03-stdin-startup.txt',exit_status=1,fix='Run Console stdin watcher on a background task to prevent synchronous reader blocking startup')],
    gameplay_implemented=False,real_client_tested=False)
verification.write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
for p in [modified,diff,verification,rollback]:
    data=p.read_bytes(); assert data
    print(json.dumps(dict(path=str(p),sha256=sha(p),bytes=len(data)),ensure_ascii=False))
for event in events:
    if event['label'] in {'BASELINE','MODIFIED','ROLLBACK'}: print(event['label'],event['stdout'].strip(),'exit='+str(event['exit_status']))
