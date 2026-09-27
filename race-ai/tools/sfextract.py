import struct,sys,os,zlib
sig=bytes([0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae])
d=open(sys.argv[1],'rb').read(); out=sys.argv[2]
i=d.find(sig); hdr=struct.unpack_from('<q',d,i-8)[0]
p=hdr
major,minor,count=struct.unpack_from('<IIi',d,p); p+=12
def rstr():
    global p
    n=0;shift=0
    while True:
        b=d[p];p+=1;n|=(b&0x7f)<<shift;shift+=7
        if b<0x80:break
    s=d[p:p+n].decode(); p+=n; return s
bid=rstr()
if major>=2: p+=8*4+8
print('bundle v',major,minor,count)
for _ in range(count):
    off,size=struct.unpack_from('<qq',d,p);p+=16
    comp=0
    if major>=6: comp=struct.unpack_from('<q',d,p)[0];p+=8
    typ=d[p];p+=1
    path=rstr()
    data=d[off:off+(comp if comp else size)]
    if comp: data=zlib.decompress(data,-15)
    fp=os.path.join(out,path); os.makedirs(os.path.dirname(fp) or out,exist_ok=True); open(fp,'wb').write(data)
