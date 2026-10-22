"""Convert a binaural SOFA field into the evidenced DTS SPAC rendering layout.

Offline only. Keeps native RPAN metadata and device-profile processing separate.
The native panner interpolates the installed nodes; this is not a lossless
installation of every SOFA direction. No per-ear alignment or normalization.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct

from netCDF4 import Dataset
import numpy as np
from scipy.spatial import ConvexHull, QhullError
from scipy.signal import resample_poly, find_peaks, fftconvolve

from inspect_dts_spac import inspect


def file_sha(path):
    digest=hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda:stream.read(1024*1024),b''):
            digest.update(block)
    return digest.hexdigest()


def values(variable):
    data=variable[:]
    if np.ma.isMaskedArray(data) and np.any(np.ma.getmaskarray(data)):
        raise ValueError('Masked or incomplete SOFA values')
    data=np.asarray(data,dtype=np.float64)
    if not np.isfinite(data).all():raise ValueError('Nonfinite SOFA values')
    return data


def units(variable):
    text=str(getattr(variable,'Units','')).strip().lower()
    return [part.strip().replace('meters','metre').replace('metres','metre').replace('meter','metre').replace('degrees','degree')
            for part in text.split(',')]


def cartesian_metres(variable,optional=False):
    kind=str(getattr(variable,'Type','')).lower()
    if optional and not kind and units(variable)==['']:return
    if kind!='cartesian' or units(variable)!=['metre']:
        raise ValueError('Expected Cartesian coordinates in metres: '+variable.name)


def constant_pose(dataset,name,default):
    if name not in dataset.variables:return np.asarray(default,dtype=float)
    variable=dataset.variables[name]
    cartesian_metres(variable,optional=name=='ListenerUp')
    array=values(variable)
    if array.ndim not in (2,3) or array.shape[1]!=3 or (array.ndim==3 and array.shape[2]!=1):
        raise ValueError('Unsupported pose dimensions: '+name)
    array=array.reshape(-1,3)
    if not len(array):raise ValueError('Empty pose: '+name)
    if not np.allclose(array,array[0],rtol=0,atol=1e-9):
        raise ValueError('Changing listener/emitter poses are not supported')
    return array[0]


def source_directions(dataset):
    variable=dataset.variables['SourcePosition']
    positions=values(variable)
    if positions.ndim!=2 or positions.shape[1]!=3:raise ValueError('Expected SourcePosition M,C')
    kind=str(getattr(variable,'Type','')).lower()
    position_units=units(variable)
    if kind=='spherical' and position_units==['degree','degree','metre']:
        azimuth,elevation=np.deg2rad(positions[:,:2]).T
        if np.any(positions[:,2]<=0) or np.any(np.abs(positions[:,1])>90):raise ValueError('Invalid spherical positions')
        positions=np.column_stack((np.cos(elevation)*np.cos(azimuth),np.cos(elevation)*np.sin(azimuth),np.sin(elevation)))*positions[:,2,None]
    elif kind!='cartesian' or position_units!=['metre']:raise ValueError('Unsupported SOFA position type or units')
    listener=constant_pose(dataset,'ListenerPosition',[0,0,0])
    forward=constant_pose(dataset,'ListenerView',[1,0,0])
    up=constant_pose(dataset,'ListenerUp',[0,0,1])
    if np.linalg.norm(forward)<1e-9:raise ValueError('Invalid ListenerView')
    forward=forward/np.linalg.norm(forward)
    left=np.cross(up,forward)
    if np.linalg.norm(left)<1e-9:raise ValueError('Invalid ListenerUp')
    left=left/np.linalg.norm(left);up=np.cross(forward,left)
    if np.any(np.abs(constant_pose(dataset,'EmitterPosition',[0,0,0]))>1e-9):
        raise ValueError('Nonzero emitter offset is not supported')
    positions=(positions-listener)@np.column_stack((forward,left,up))
    radii=np.linalg.norm(positions,axis=1)
    if np.any(radii<1e-8) or not np.allclose(radii,radii[0],rtol=1e-5,atol=1e-7):
        raise ValueError('A single source-distance SOFA field is required')
    points=positions/radii[:,None]
    if len(np.unique(np.round(points,8),axis=0))!=len(points):raise ValueError('Duplicate source directions')
    return points,float(radii[0])


def interpolation(points,targets):
    try:hull=ConvexHull(points)
    except QhullError as exc:raise ValueError('A full-sphere nondegenerate direction grid is required') from exc
    if np.max(hull.equations[:,3])>=-1e-7:raise ValueError('SOFA directions do not surround the listener')
    faces=np.asarray(sorted(tuple(sorted(face)) for face in hull.simplices))
    matrices=points[faces].transpose(0,2,1)
    try:inverses=np.linalg.inv(matrices)
    except np.linalg.LinAlgError as exc:raise ValueError('Degenerate interpolation triangle') from exc
    output=[]
    for target in targets:
        nearest=int(np.argmax(points@target))
        if np.linalg.norm(points[nearest]-target)<1e-6:
            output.append(([nearest],[1.0]));continue
        raw=inverses@target
        valid=np.flatnonzero((raw.min(axis=1)>=-1e-8)&(raw.sum(axis=1)>1e-8))
        if not len(valid):raise ValueError('No interpolation triangle for native direction')
        index=int(valid[0]);weights=np.maximum(raw[index],0);weights/=weights.sum()
        output.append((faces[index].tolist(),weights.tolist()))
    return output


def arrival_sample(response):
    """First significant direct peak; never minimum-phase a room response."""
    magnitude=np.abs(response)
    peak=float(magnitude.max())
    if peak==0:return 0.0
    candidates,_=find_peaks(magnitude,height=peak*.1)
    if magnitude[0]>=peak*.1:index=0
    elif len(candidates):index=int(candidates[0])
    else:index=int(np.argmax(magnitude))
    fraction=0.0
    if 0<index<len(response)-1:
        a,b,c=magnitude[index-1:index+2];denominator=a-2*b+c
        if denominator<0:fraction=float(np.clip(.5*(a-c)/denominator,-.5,.5))
    return index+fraction


def alignment_plan(responses,mappings,method):
    if method not in ('linear','time-aligned'):raise ValueError('Unknown spatial interpolation method')
    plans=[];minimum=0.;maximum=0.;fallbacks=0
    arrivals={index:[arrival_sample(ear) for ear in response] for index,response in responses.items()}
    for indices,weights in mappings:
        shifts=np.zeros((len(indices),2))
        if method=='time-aligned' and len(indices)>1:
            times=np.asarray([arrivals[index] for index in indices])
            # A >2 ms change between adjacent directions is usually an ambiguous
            # direct/reflection peak. Preserve that blend instead of guessing.
            if np.max(np.ptp(times,axis=0))>96:fallbacks+=1
            elif np.max(np.ptp(times,axis=0))>=.1:
                shifts=np.asarray(weights)@times-times
        minimum=min(minimum,float(shifts.min()));maximum=max(maximum,float(shifts.max()));plans.append(shifts)
    guard=32 if minimum<0 or maximum>0 else 0
    common=int(math.ceil(-minimum))+guard
    extension=common+int(math.ceil(maximum))+guard
    return plans,common,extension,fallbacks


def shifted_response(response,shift,common,length):
    output=np.zeros(length)
    integer=int(math.floor(shift));fraction=shift-integer
    if fraction<1e-10 or 1-fraction<1e-10:
        start=common+int(round(shift));filtered=response
    else:
        n=np.arange(-32,33,dtype=float)
        kernel=np.sinc(n-fraction)*np.kaiser(65,8.6);kernel/=kernel.sum()
        filtered=fftconvolve(response,kernel);start=common+integer-32
    if start<0 or start+len(filtered)>length:raise ValueError('Fractional-delay padding does not contain the complete response')
    output[start:start+len(filtered)]=filtered
    return output


def convert(sofa_path,template_path,gain=1.0,room='off',room_template_path=None,interpolation_method='linear'):
    if not math.isfinite(gain) or gain<=0:raise ValueError('Gain must be finite and positive')
    if room not in ('off','preserve'):raise ValueError('Room must be off or preserve')
    if room_template_path is not None and room!='preserve':raise ValueError('A room template requires --room preserve')
    template_path,sofa_path=Path(template_path),Path(sofa_path)
    template=template_path.read_bytes();original=inspect(template)
    if original['sample_rate']!=48000 or len(original['tables'])!=1 or original['tables'][0]['index']!=0:
        raise ValueError('This converter requires the evidenced single-mode 48 kHz template')
    if not original['within_native_panner_init_position_limit']:raise ValueError('Native panner position limit exceeded')
    table=original['tables'][0];count=original['channel_count']
    room_template=template;room_table=table
    if room_template_path is not None:
        room_template_path=Path(room_template_path)
        room_template=room_template_path.read_bytes();room_info=inspect(room_template)
        if (room_info['sample_rate']!=original['sample_rate'] or room_info['channel_count']!=count or
            len(room_info['tables'])!=1 or room_info['tables'][0]['index']!=table['index'] or
            room_template[room_info['panner_offset']:]!=template[original['panner_offset']:]):
            raise ValueError('Room template must use the same native sample rate, mode and panner')
        room_table=room_info['tables'][0]
        if room_template[room_table['offset']+8:room_table['offset']+92]!=template[table['offset']+8:table['offset']+92]:
            raise ValueError('Room template parameters differ; only send changes are supported')
    if table['offset']!=36:raise ValueError('Unexpected response-table offset')
    native=np.asarray(original['panner_positions'],dtype=float)
    nonzero=np.linalg.norm(native,axis=1)>1e-7
    if not nonzero.any():raise ValueError('No directional native nodes')
    targets=np.column_stack((-native[nonzero,2],-native[nonzero,0],native[nonzero,1]))
    targets/=np.linalg.norm(targets,axis=1)[:,None]
    sofa_hash=file_sha(sofa_path)
    with Dataset(sofa_path,'r') as dataset:
        if getattr(dataset,'SOFAConventions','') not in ('SimpleFreeFieldHRIR','GeneralFIR'):
            raise ValueError('Unsupported SOFA convention')
        variable=dataset.variables['Data.IR']
        if variable.dimensions!=('M','R','N') or variable.shape[1]!=2 or not 1<=variable.shape[2]<=65536:
            raise ValueError('Expected binaural Data.IR M,R,N with at most 65536 samples')
        points,radius=source_directions(dataset)
        if len(points)!=variable.shape[0]:raise ValueError('Source position count differs from Data.IR')
        receiver_variable=dataset.variables['ReceiverPosition']
        cartesian_metres(receiver_variable)
        receivers=values(receiver_variable)
        if receivers.shape not in ((2,3,1),(2,3)):
            raise ValueError('Expected two constant receiver positions')
        receivers=receivers.reshape(2,3,1)
        if not (receivers[0,1,0]>0 and receivers[1,1,0]<0):
            raise ValueError('Receiver order must identify left then right ears')
        rate_variable=dataset.variables['Data.SamplingRate']
        if units(rate_variable)!=['hertz']:raise ValueError('Expected Data.SamplingRate in hertz')
        rates=values(rate_variable).reshape(-1)
        if not len(rates) or not np.all(rates==rates[0]) or rates[0]!=round(rates[0]) or not 8000<=rates[0]<=192000:
            raise ValueError('One integer SOFA sampling rate is required')
        source_rate=int(rates[0]);delay_variable=dataset.variables['Data.Delay']
        if units(delay_variable) not in ([''],['sample'],['samples']):raise ValueError('Data.Delay must use samples')
        delay=values(delay_variable)
        if np.any(np.abs(delay)>source_rate):raise ValueError('SOFA delay exceeds one second')
        if delay.shape==(1,2):delay=np.repeat(delay,len(points),axis=0)
        if delay.shape!=(len(points),2) or np.any(np.abs(delay-np.rint(delay))>1e-9):
            raise ValueError('Fractional Data.Delay needs a separately validated fractional-delay implementation')
        delay=np.rint(delay).astype(np.int64)
        common_delay=int(max(0,-delay.min()));delay=delay+common_delay
        source_length=int(variable.shape[2]+delay.max())
        response_length=max(table['samples'],(source_length*48000+source_rate-1)//source_rate)
        if response_length>65536:raise ValueError('Converted response exceeds supported length')
        legacy=np.frombuffer(template,dtype='<f4',count=count*2*table['samples'],offset=132).reshape(count,2,table['samples'])
        weights=interpolation(points,targets)
        divisor=math.gcd(48000,source_rate);resample_up,resample_down=48000//divisor,source_rate//divisor
        used=sorted(set(index for indices,_ in weights for index in indices));responses={}
        for index in used:
            response=values(variable[index:index+1])[0]
            delayed=np.zeros((2,source_length))
            for ear in range(2):
                start=int(delay[index,ear]);delayed[ear,start:start+response.shape[1]]=response[ear]
            if source_rate!=48000:delayed=resample_poly(delayed,resample_up,resample_down,axis=-1)
            responses[index]=delayed
        plans,alignment_delay,extension,alignment_fallbacks=alignment_plan(responses,weights,interpolation_method)
        response_length+=extension
        if response_length>65536:raise ValueError('Time alignment exceeds the native FIR length limit; select Linear interpolation')
        node_filters=np.zeros((count,2,response_length),dtype=np.float64)
        node_filters[~nonzero,:,alignment_delay:alignment_delay+table['samples']]=legacy[~nonzero]
        mappings=[]
        for node,(indices,coefficients),shifts in zip(np.flatnonzero(nonzero),weights,plans):
            for row,(index,weight) in enumerate(zip(indices,coefficients)):
                response=responses[index]
                for ear in range(2):node_filters[node,ear]+=shifted_response(response[ear],shifts[row,ear],alignment_delay,response_length)*(weight*gain)
            mappings.append({'native_node':int(node),'native_position':native[node].tolist(),
                             'source_indices':indices,'weights':coefficients,'alignment_shifts_native_samples':shifts.tolist()})
    if file_sha(sofa_path)!=sofa_hash or template_path.read_bytes()!=template:raise ValueError('Input changed during conversion')
    if not np.isfinite(node_filters).all() or np.max(np.abs(node_filters))>np.finfo(np.float32).max:
        raise ValueError('Converted FIR values exceed float32 range')
    coefficients=node_filters.astype('<f4')
    subnormal=(coefficients!=0)&(np.abs(coefficients)<np.finfo(np.float32).tiny)
    coefficients[subnormal]=0
    quantization_error=coefficients.astype(np.float64)-node_filters
    norm=float(np.linalg.norm(node_filters))
    quantization={'maximum_absolute_error':float(np.max(np.abs(quantization_error))),
                  'relative_l2_error':float(np.linalg.norm(quantization_error)/norm) if norm else 0.0}
    header=bytearray(template[36:132]);length=96+(response_length+2)*count*8
    struct.pack_into('<2I',header,0,length,response_length)
    # Native01846c feeds the first stereo vector set to recursive room01764c.
    # Preserve those sends on request; the second vector set remains unresolved.
    auxiliary=bytearray(count*16)
    if room=='preserve':
        start=room_table['auxiliary_offset']
        auxiliary[:count*8]=room_template[start:start+count*8]
    if room_template_path is not None and room_template_path.read_bytes()!=room_template:
        raise ValueError('Room template changed during conversion')
    auxiliary=bytes(auxiliary)
    response_table=bytes(header)+coefficients.tobytes()+auxiliary
    rchn=bytearray(template[12:36]);struct.pack_into('<I',rchn,0,24+length)
    panner=template[original['panner_offset']:]
    spac=bytearray(template[:12]);struct.pack_into('<I',spac,0,12+24+length+len(panner))
    output=bytes(spac)+bytes(rchn)+response_table+panner
    inspect(output)
    report={'sofa_sha256':sofa_hash,'template_sha256':hashlib.sha256(template).hexdigest(),
            'output_sha256':hashlib.sha256(output).hexdigest(),'output_bytes':len(output),
            'source_directions':len(points),'source_distance_metres':radius,'source_rate_hz':source_rate,
            'native_rate_hz':48000,'native_positions':count,'converted_directional_positions':int(nonzero.sum()),
            'preserved_origin_positions':np.flatnonzero(~nonzero).tolist(),'response_samples':response_length,
            'common_directional_gain':gain,'added_common_delay_source_samples':common_delay,
            'subnormal_coefficients_flushed':int(subnormal.sum()),'panner_bytes_preserved':True,
            'float32_quantization':quantization,
            'room':room,'room_input_vectors_preserved':room=='preserve','interpolation':mappings,
            'room_template_sha256':hashlib.sha256(room_template).hexdigest() if room=='preserve' else None,
            'all_auxiliary_vectors_zeroed':room=='off','verified_room_send_float_count':count*2,
            'additional_auxiliary_semantics_verified':False,
            'interpolation_method':interpolation_method,'added_interpolation_delay_native_samples':alignment_delay,
            'alignment_fallback_count':alignment_fallbacks,'fractional_delay_filter':'65-tap Kaiser-windowed sinc, beta 8.6' if alignment_delay else None,
            'scope':'Offline conversion; native initialization/use and held-out native directional error are unverified.'}
    return output,report


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('sofa',type=Path);parser.add_argument('--template',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--gain',type=float,default=1.0)
    parser.add_argument('--room',choices=('off','preserve'),default='off',help='Preserve the template recursive room response or mute its sends')
    parser.add_argument('--room-template',type=Path,help='Optional matching native carrier supplying room sends')
    parser.add_argument('--interpolation',choices=('linear','time-aligned'),default='linear')
    args=parser.parse_args()
    try:
        if args.output.exists():raise ValueError('Output directory already exists')
        output,report=convert(args.sofa,args.template,args.gain,args.room,args.room_template,args.interpolation)
        args.output.mkdir(parents=True)
        (args.output/'runtime_SPAC.bin').write_bytes(output)
        (args.output/'manifest.json').write_text(json.dumps(report,indent=2,allow_nan=False)+'\n',encoding='utf-8')
        print(json.dumps(report,indent=2,allow_nan=False))
    except (OSError,ValueError,KeyError,IndexError) as exc:
        parser.exit(2,'Conversion failed: %s\n' % exc)


if __name__=='__main__':main()
