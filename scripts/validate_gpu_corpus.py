"""Real photographed-chart validation with GPU-only production inference."""
from pathlib import Path
import argparse, hashlib, json, os, subprocess
from PIL import Image
from validate_detector_corpus import match_reviewed_charts, compare_cells

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--engine',type=Path,required=True)
    parser.add_argument('--runtime',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();root=Path(__file__).resolve().parents[1]
    args.output.mkdir(parents=True,exist_ok=False)
    reviewed=json.loads((root/'docs/compute/reviewed-corpus.json').read_text())
    env=dict(os.environ,CCP_NEW_UI_ROOT=str(root),ORT_DYLIB_PATH=str(args.runtime.resolve()/'onnxruntime.dll'))
    env['PATH']=str(args.runtime.resolve())+os.pathsep+env['PATH']
    rows=[]
    for fixture in reviewed['fixtures']:
        source=root/fixture['path']
        assert hashlib.sha256(source.read_bytes()).hexdigest()==fixture['sha256']
        rotations=fixture.get('derived_rotations_clockwise',[0,90,180,270]) if fixture['required_charts'] else [0]
        for rotation in rotations:
            path=source
            if rotation:
                path=args.output/f'{source.stem}-{rotation}.png'
                with Image.open(source) as im: im.rotate(-rotation,expand=True).save(path)
            baseline=None
            for provider in ('directml','webgpu'):
                request={'cmd':'detect','image':str(path.resolve()),'policy':provider,'bypass_cache':True}
                proc=subprocess.run([str(args.engine.resolve())],input=json.dumps(request)+'\n',env=env,text=True,capture_output=True,timeout=90)
                replies=[json.loads(line) for line in proc.stdout.splitlines() if line.startswith('{')]
                reply=replies[-1] if replies else {'ok':False,'error':proc.stderr}
                observed={'charts':[],'size':1280}
                if reply.get('ok'):
                    observed=json.loads(Path(reply['handshake']).read_text());observed.setdefault('size',1280)
                valid=reply.get('ok') or 'no lattice' in reply.get('error','')
                match=match_reviewed_charts(observed,fixture['required_charts'],rotation)
                row={'fixture':fixture['path'],'rotation':rotation,'provider':provider,'reply':reply,'matching':match,'passed':bool(valid and match['passed']),'handshake':observed}
                if baseline is not None:row['equivalence']=compare_cells(baseline,observed)
                else:baseline=observed
                rows.append(row)
                (args.output/'results.json').write_text(json.dumps({'rows':rows,'missing_coverage':reviewed['missing_required_coverage']},indent=2))
                print(source.name,rotation,provider,row['passed'],flush=True)
    if not all(row['passed'] for row in rows):raise SystemExit(1)
if __name__=='__main__':main()
