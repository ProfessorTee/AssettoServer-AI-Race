import struct,sys,os
sig=bytes([0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae])
src,dst=sys.argv[1],sys.argv[2]; repl=dict(a.split('=',1) for a in sys.argv[3:])
d=bytearray(open(src,'rb').read())
si=d.find(sig); hdr=struct.unpack_from('<q',d,si-8)[0]
p=hdr
major,minor,count=struct.unpack_from('<IIi',d,p); p+=12
def rstr():
    global p
    n=0;shift=0
    while True:
        b=d[p];p+=1;n|=(b&0x7f)<<shift;shift+=7
        if b<0x80:break
    s=d[p:p+n].decode(); p+=n; return s
def wstr(s):
    b=s.encode(); n=len(b); out=bytearray()
    while True:
        x=n&0x7f; n>>=7
        out.append(x|(0x80 if n else 0))
        if not n: break
    return bytes(out)+b
bid=rstr()
rest=d[p:p+40] if major>=2 else b''   # deps/runtimeconfig offsets+sizes, flags
depoff,depsize,rcoff,rcsize,flags=struct.unpack_from('<qqqqQ',d,p) if major>=2 else (0,0,0,0,0); p+=40 if major>=2 else 0
entries=[]
for _ in range(count):
    off,size=struct.unpack_from('<qq',d,p);p+=16
    comp=struct.unpack_from('<q',d,p)[0];p+=8
    typ=d[p];p+=1
    path=rstr()
    entries.append([off,size,comp,typ,path])
first=min(e[0] for e in entries)
out=bytearray(d[:first])
newdep=newrc=(0,0)
for e in entries:
    off,size,comp,typ,path=e
    if path in repl:
        data=open(repl[path],'rb').read(); comp=0; size=len(data)
    else:
        data=bytes(d[off:off+(comp if comp else size)])
    while len(out)%16: out.append(0)
    e[0]=len(out); e[1]=size; e[2]=comp
    out+=data
    if typ==3: newdep=(e[0],size)
    if typ==4: newrc=(e[0],size)
while len(out)%16: out.append(0)
newhdr=len(out)
m=bytearray(struct.pack('<IIi',major,minor,count))+wstr(bid)
m+=struct.pack('<qqqqQ',newdep[0],newdep[1],newrc[0],newrc[1],flags)
for off,size,comp,typ,path in entries:
    m+=struct.pack('<qqq',off,size,comp)+bytes([typ])+wstr(path)
out+=m
struct.pack_into('<q',out,si-8,newhdr)
open(dst,'wb').write(out); os.chmod(dst,0o755)
print('ok',count,'entries, replaced',list(repl))
