# usage: python src/tools/mutate.py spec.json [parallel]
# spec: [{"name":..., "file":"Connect.cs", "old":"...", "new":"..."}]  (file relative to src/app/; several edits: "edits":[{file,old,new},...]; "tier": E2E tier, default 8)
# Each mutant: an isolated app/tests/tools copy in src/.work/mut/<name>, the edit, a build, the self-test and the tier. Logs: src/.work/logs/mutants.
import json, sys, os, shutil, subprocess, re, concurrent.futures as cf
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # src
dn = os.environ.get('AGDNS_DOTNET') or 'dotnet'
logs = os.path.join(root, '.work', 'logs', 'mutants')
def e2e_result(process, receipt_path):
    try:
        with open(receipt_path, encoding='utf-8-sig') as stream:
            receipt = json.load(stream)
        if receipt.get('Completed') is not True or receipt.get('FatalError') or receipt.get('HostUnchanged') is not True:
            return 'T8-ERROR: incomplete run or host invariant failed'
        results = receipt['Results']
        total, passed = receipt['TotalTests'], receipt['PassCount']
        if not isinstance(results, list) or type(total) is not int or type(passed) is not int or total <= 0 or total != len(results):
            return 'T8-ERROR: invalid test receipt'
        reds = [r['TestId'] for r in results if r.get('Passed') is False]
        if passed != total - len(reds) or any(type(r.get('Passed')) is not bool for r in results):
            return 'T8-ERROR: inconsistent test receipt'
        if receipt.get('SkipCount') != 0 or receipt.get('DefectCount', 0) + receipt.get('FailOtherCount', 0) != len(reds):
            return 'T8-ERROR: inconsistent failure counters'
        if reds:
            return 'T8-RED: ' + ','.join(reds) if process.returncode != 0 else 'T8-ERROR: failed assertions with exit zero'
        return 't8-green' if process.returncode == 0 else 'T8-ERROR: nonzero exit without failed assertions'
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        return 'T8-ERROR: missing or invalid test receipt'

def validate_mutant(m):
    # A spec controls paths used by rmtree and writes. Validate before touching any workspace.
    name = m['name']
    if not isinstance(name, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]*', name):
        raise ValueError('Unsafe mutant name')
    app_root = os.path.realpath(os.path.join(root, 'app'))
    for edit in m.get('edits') or [m]:
        path = os.path.realpath(os.path.join(app_root, edit['file']))
        if os.path.commonpath([app_root, path]) != app_root or path == app_root:
            raise ValueError('Edit outside app source')

def run(m):
    validate_mutant(m)
    name = m['name']; d = os.path.join(root, '.work', 'mut', name)
    shutil.rmtree(d, ignore_errors=True); os.makedirs(d)
    tier = m.get('tier', '8')
    shutil.copytree(os.path.join(root, 'app'), os.path.join(d, 'app'))
    # Historical script fixtures are used by tiers 1-4.
    big = tier in ('1', '2', '3', '4', 'All')
    shutil.copytree(os.path.join(root, 'tests'), os.path.join(d, 'tests'), ignore=None if big else shutil.ignore_patterns('baseline-1.1.0'))
    shutil.copytree(os.path.join(root, 'tools'), os.path.join(d, 'tools'), ignore=shutil.ignore_patterns('__pycache__'))
    edits = m.get('edits') or [m]
    for e in edits:
        fp = os.path.join(d, 'app', e['file'])
        t = open(fp, encoding='utf-8-sig', newline='').read()
        if t.count(e['old']) != 1:
            return (name, 'EDIT-NOT-UNIQUE(%d): %s' % (t.count(e['old']), e['old'][:60]), '', '')
        open(fp, 'w', encoding='utf-8-sig', newline='').write(t.replace(e['old'], e['new']))
    out = os.path.join(d, 'out')
    b = subprocess.run(['powershell.exe', '-NoProfile', '-File', os.path.join(d, 'app', 'Build.ps1'), '-Dotnet', dn, '-OutputDirectory', out], capture_output=True, text=True)
    open(os.path.join(logs, name + '.build.txt'), 'w', encoding='utf-8').write(b.stdout + b.stderr)
    exe = os.path.join(out, 'AntigravityGeminiDns.exe')
    if b.returncode != 0 or not os.path.exists(exe):
        return (name, 'BUILD-FAILED', '', '')
    try:
        st = subprocess.run([exe, '--self-test'], capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=150)
        fail = [l for l in (st.stdout + st.stderr).splitlines() if 'FAIL' in l][:2]
        selftest = 'SELFTEST-RED: ' + ' | '.join(fail) if st.returncode != 0 else 'selftest-green'
    except subprocess.TimeoutExpired:
        selftest = 'SELFTEST-RED: TIMEOUT(hang)'
    env = dict(os.environ, AGDNS_EXE=exe)
    receipt = os.path.join(d, 'offline_e2e_receipt.json')
    t8 = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(d, 'tests', 'run_offline_e2e_tests.ps1'), '-Tier', tier, '-ReceiptPath', receipt], capture_output=True, text=True, encoding='utf-8', errors='replace', env=env)
    open(os.path.join(logs, name + '.t8.txt'), 'w', encoding='utf-8').write(t8.stdout + t8.stderr)
    return (name, 'ok', selftest, e2e_result(t8, receipt))
if __name__ == '__main__':
    spec = json.load(open(sys.argv[1], encoding='utf-8'))
    for mutant in spec:
        validate_mutant(mutant)
    if len({m['name'].casefold() for m in spec}) != len(spec):
        raise ValueError('Duplicate mutant names would share a workspace')
    os.makedirs(logs, exist_ok=True)
    par = int(sys.argv[2]) if len(sys.argv) > 2 else 3
    with cf.ThreadPoolExecutor(par) as ex:
        res = list(ex.map(run, spec))
    lines = ['%-14s %s | %s | %s' % r for r in res]
    open(os.path.join(logs, 'summary_%s.txt' % os.path.basename(sys.argv[1])), 'w', encoding='utf-8').write('\n'.join(lines))
    print('\n'.join(lines))
    if any(r[1] != 'ok' or 'T8-ERROR:' in r[3] for r in res):
        sys.exit(2)
