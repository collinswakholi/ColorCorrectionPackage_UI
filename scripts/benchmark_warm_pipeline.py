"""Cold first operation versus warm reuse of two persistent, sequential workers."""
from pathlib import Path
from contextlib import ExitStack
import argparse, hashlib, json, os, statistics, sys, time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'api'))
from colorcorrect import Client, CalibrationOptions, ApplyOptions

class UncachedClient(Client):
    def _request(self, command, **parameters):
        if command=='detect': parameters['bypass_cache']=True
        return super()._request(command,**parameters)

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--engine',type=Path,required=True)
    p.add_argument('--samples',type=Path,default=ROOT/'test_images')
    args=p.parse_args();samples=args.samples.resolve()
    missing=[name for name in ('ColorCard_Big.JPG','Sample_3572.JPG') if not (samples/name).is_file()]
    if missing: p.error(f"Required public fixtures are missing from {samples}: {', '.join(missing)}")
    args.output.mkdir(parents=True,exist_ok=False)
    data={'engine':str(args.engine.resolve()),'engine_sha256':hashlib.sha256(args.engine.read_bytes()).hexdigest(),
          'scope':'persistent workers; uncached detection, fit without chart output, PNG apply; no FFC',
          'runs':[]}
    with ExitStack() as stack:
        clients={backend:stack.enter_context(UncachedClient(args.engine,root=ROOT,backend=backend)) for backend in ('cpu','auto')}
        for repetition in range(6):
            for backend in (('cpu','auto') if repetition%2==0 else ('auto','cpu')):
                client=clients[backend]
                start=time.perf_counter()
                detection=client.detect(samples/'ColorCard_Big.JPG')
                after_detect=time.perf_counter()
                assert detection.raw_reply.get('cached') is False
                assert detection.raw_reply['device'] in ('directml','webgpu','vulkan')
                model=client.calibrate(samples/'ColorCard_Big.JPG',detection=detection,output_dir=args.output,
                    options=CalibrationOptions(do_ffc=False,write_output=False))
                after_fit=time.perf_counter()
                result=model.apply(samples/'Sample_3572.JPG',output_dir=args.output,options=ApplyOptions(output_format='png'))
                end=time.perf_counter()
                actual=[r.backends.actual.get('apply','') for r in result.images]
                assert all(value=='cpu' if backend=='cpu' else value.startswith('wgpu:') and '+cpu' not in value for value in actual),actual
                row={'backend':backend,'repetition':repetition,'kind':'first' if repetition==0 else 'warm',
                     'actual_detect':detection.raw_reply['device'],'actual_apply':actual,
                     'detect_ms':(after_detect-start)*1000,'fit_ms':(after_fit-after_detect)*1000,
                     'apply_ms':(end-after_fit)*1000,'total_ms':(end-start)*1000,
                     'model':str(model.path),'outputs':[str(x) for x in result.outputs]}
                data['runs'].append(row)
                (args.output/'raw.json').write_text(json.dumps(data,indent=2),encoding='utf8')
                print(backend,repetition,round(row['total_ms'],2),flush=True)
    data['summary']={b:{stage:statistics.median(r[stage] for r in data['runs'] if r['backend']==b and r['kind']=='warm')
                         for stage in ('detect_ms','fit_ms','apply_ms','total_ms')} for b in clients}
    (args.output/'raw.json').write_text(json.dumps(data,indent=2),encoding='utf8')

if __name__=='__main__': main()
