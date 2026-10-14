"""Public-corpus CPU sweep: every fit family, all stages, saved-model application."""
import argparse
import json
from pathlib import Path
import sys
import tempfile
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from colorcorrect import Engine, EngineError

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
parser.add_argument('--engine', type=Path)
args = parser.parse_args()
samples = args.root / 'test_images'
out = Path(tempfile.mkdtemp(prefix='colorcorrect-six-methods-'))
rows = []
with Engine(args.engine, root=args.root, policy='cpu') as engine:
    detected = engine.detect(samples/'ColorCard_Big.JPG', handshake_out=out/'chart.json')
    for method in ['ccm','rpcc2','rpcc3','linear','pls','nn']:
        start = time.perf_counter()
        model = out / f'{method}.json'
        result = engine.fit(samples/'ColorCard_Big.JPG', handshake=detected['handshake'],
            output=out/f'{method}.png', report=out/f'{method}.report.json', model_out=model,
            model=method, degree=2, white=samples/'Blank.JPG', do_ffc=True,
            do_gc=True, do_wb=True, do_cc=True, cross_validate=True, phase2_teacher=False,
            nn={'hidden_layers':[64,32], 'optimizer':'adam','epochs':200,'patience':50})
        report = result['report_json']
        assert all(report[k] for k in ('do_ffc','do_gc','do_wb','do_cc'))
        assert 'validation_delta_e_2000' in report
        applied = engine.apply(model, [samples/'Sample_3572.JPG',samples/'Sample_3573.JPG'],
            out/method, do_ffc=True, white=samples/'Blank.JPG', output_format='png')
        assert all(Path(r['output']).is_file() and not r.get('ffc_skipped') for r in applied['results'])
        # Actual native rejection, not just Python pre-validation.
        try:
            engine.request('apply', check=True, model=model, images=[str(samples/'Sample_3572.JPG')],
                out_dir=out/'invalid', do_ffc=True, white=out/'missing-white.jpg')
        except EngineError:
            pass
        else:
            raise AssertionError('Missing FFC reference silently accepted')
        rows.append({'method':method,'seconds':round(time.perf_counter()-start,3),
                     'training':report['delta_e_2000'],'validation':report['validation_delta_e_2000'],
                     'apply_backend':applied.get('apply_backend')})
print(json.dumps({'ok':True,'chart_type':detected['chart_type'],'results':rows},indent=2),flush=True)
