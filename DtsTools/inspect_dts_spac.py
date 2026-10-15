"""Read-only structural inspector for the observed DTS SPAC version-2 carrier."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct

def check_float_words(data,offset,count):
    """Native b8b8/b86c reject NaN, infinity and nonzero subnormal floats."""
    words=struct.unpack_from('<'+str(count)+'I',data,offset)
    for word in words:
        exponent=word & 0x7f800000
        if exponent==0x7f800000 or (exponent==0 and word & 0x7fffff):
            raise ValueError('Nonfinite or subnormal floating-point data')

def inspect(data):
    if len(data)<36 or len(data)>16*1024*1024:raise ValueError('Unsupported SPAC extent')
    total,tag,version=struct.unpack_from('<I4sI',data)
    if (total,tag,version)!=(len(data),b'SPAC',2):raise ValueError('SPAC extent, tag, or version mismatch')
    size,tag,version,rate,count,mask=struct.unpack_from('<I4s4I',data,12)
    if tag!=b'RCHN' or version!=2 or not 24000<=rate<=192000 or not 1<=count<=999 or not 0<mask<=3:
        raise ValueError('Unsupported RCHN header')
    end=12+size
    if end+24>len(data):raise ValueError('RCHN exceeds carrier')
    offset=36;tables=[]
    for index in range(2):
        if not (mask>>index)&1:continue
        if offset+96>end:raise ValueError('Truncated response table header')
        length,samples,responses=struct.unpack_from('<3I',data,offset)
        if responses!=count or not 1<=samples<=65536 or length!=96+(samples+2)*responses*8 or offset+length>end:
            raise ValueError('Unsupported response table dimensions')
        check_float_words(data,offset+96,(samples+2)*responses*2)
        values=struct.unpack_from('<'+str(samples*responses*2)+'f',data,offset+96)
        if not all(math.isfinite(value) for value in values):raise ValueError('Nonfinite response coefficients')
        auxiliary_offset=offset+96+len(values)*4
        auxiliary=struct.unpack_from('<'+str(responses*4)+'f',data,auxiliary_offset)
        tables.append(dict(index=index,offset=offset,bytes=length,samples=samples,response_count=responses*2,
            minimum=min(values),maximum=max(values),coefficient_sha256=hashlib.sha256(data[offset+96:offset+96+len(values)*4]).hexdigest(),
            auxiliary_offset=auxiliary_offset,auxiliary_values=list(auxiliary),auxiliary_semantics_verified=False))
        offset+=length
    if offset!=end:raise ValueError('Unparsed RCHN data')
    panner_size,panner_tag=struct.unpack_from('<I4s',data,end)
    if panner_tag!=b'RPAN' or end+panner_size!=len(data):raise ValueError('Unsupported panner block')
    groups=struct.unpack_from('<4I',data,end+8)
    if any(n>100 for n in groups) or sum(groups)!=count or panner_size!=24+count*16:
        raise ValueError('Unsupported panner dimensions')
    check_float_words(data,end+24,count*3)
    positions=list(struct.iter_unpack('<3f',data[end+24:end+24+count*12]))
    identifiers=struct.unpack_from('<'+str(count)+'I',data,end+24+count*12)
    return dict(bytes=len(data),sha256=hashlib.sha256(data).hexdigest(),sample_rate=rate,channel_count=count,
        tables=tables,panner_offset=end,panner_bytes=panner_size,panner_group_counts=list(groups),
        panner_positions=positions,panner_identifiers=list(identifiers),
        native_panner_init_position_limit=32,within_native_panner_init_position_limit=count<=32,
        ear_order_verified=False,direction_order_verified=False,
        native_acceptance_verified=False)

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('blob',type=Path);args=parser.parse_args()
    try:
        print(json.dumps(inspect(args.blob.read_bytes()),indent=2,allow_nan=False))
    except (OSError,ValueError,struct.error) as exc:
        parser.exit(2,'Inspection failed: %s\n' % exc)
