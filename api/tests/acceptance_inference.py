"""GPU-only inference equivalence on reviewed photographs in an installed guest."""
import argparse, hashlib, importlib.util, json, os, sys, traceback
from pathlib import Path

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('package','fixtures','output'):
        parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args(); args.output.mkdir(parents=True,exist_ok=False)
    args.package=args.package.resolve(); args.fixtures=args.fixtures.resolve()
    os.chdir(args.output)
    from colorcorrect import Client, CalibrationOptions
    # This audited, standalone harness helper contains no app/runtime imports.
    spec=importlib.util.spec_from_file_location('corpus_checks',Path(__file__).with_name('validate_detector_corpus.py'))
    checks=importlib.util.module_from_spec(spec); spec.loader.exec_module(checks)
    corpus=json.loads((args.fixtures/'reviewed-corpus.json').read_text())
    report={'status':'running','detections':[],'planes':[], 'gaps':[
        'Derived rotations and explicit detector-resolution timing remain in host corpus tests.',
        'Geometry equivalence is not independent detector ground truth.']}
    baselines={}; plane_baseline=None
    def save(): (args.output/'inference.json').write_text(json.dumps(report,indent=2))
    try:
        providers=('directml','webgpu','vulkan') if os.name=='nt' else ('webgpu','vulkan')
        for provider in providers:
            with Client(root=args.package,backend=provider,timeout=600) as client:
                for fixture in corpus['fixtures']:
                    image=args.fixtures/'corpus'/Path(fixture['path']).name
                    assert hashlib.sha256(image.read_bytes()).hexdigest()==fixture['sha256']
                    reply=client.engine.request('detect',image=image,bypass_cache=True,check=False)
                    observed={'charts':[],'size':1280}
                    if reply.get('ok'):
                        source=reply['handshake']
                        observed=source if isinstance(source,dict) else json.loads(Path(source).read_text())
                    # The worker handshake uses source-image pixel coordinates;
                    # all hash-verified public corpus photographs are 6000 wide.
                    observed.setdefault('size',6000)
                    match=checks.match_reviewed_charts(observed,fixture['required_charts'],0)
                    row={'image':image.name,'provider':provider,'actual':reply.get('device'),
                         'matching':match,'handshake':observed,'error':reply.get('error')}
                    report['detections'].append(row); save()
                    assert reply.get('ok') or 'no lattice' in reply.get('error','').lower(), reply
                    assert match['passed'], row
                    if image.name in baselines:
                        difference=checks.compare_cells(baselines[image.name],observed)
                        row['equivalence']=difference
                        assert difference['matched'] and difference['max_indexed_cell_center_delta_1280_canvas_px']<=0.01, row
                    else: baselines[image.name]=observed
                fitted=client.calibrate(args.fixtures/'chart.jpg',white=args.fixtures/'blank.jpg',
                    output_dir=args.output,options=CalibrationOptions(do_gc=False,do_wb=False,do_cc=False,
                                                                     write_output=False,cross_validate=False))
                rectangle=fitted.metrics['ffc_plane']
                assert fitted.metrics['ffc_plane_source']=='detected', fitted.metrics
                assert fitted.backends.actual.get('plane') and 'cpu' not in fitted.backends.actual['plane'].lower()
                row={'provider':provider,'actual':fitted.backends.actual,'rectangle':rectangle}
                if plane_baseline is not None:
                    row['max_coordinate_difference']=max(abs(a-b) for a,b in zip(plane_baseline,rectangle))
                    assert row['max_coordinate_difference']<=1, row
                else: plane_baseline=rectangle
                report['planes'].append(row); save()
        report['status']='passed'
    except BaseException:
        report['status']='failed'; report['error']=traceback.format_exc(); raise
    finally: save()

if __name__=='__main__': main()
