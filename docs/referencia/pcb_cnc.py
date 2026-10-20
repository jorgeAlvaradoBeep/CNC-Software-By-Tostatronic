"""pcb_cnc.py - G-code de aislamiento, barrenos y contorno para PCB de una cara (GRBL, mm).
Uso:  python3 pcb_cnc.py placa.json [--tool 0.2] [--depth 0.1] [--passes 2] [--step 0.15]
      [--feed 150] [--plunge 50] [--rpm 10000] [--thick 1.6] [--repaso x0,y0,x1,y1]
placa.json = {"nombre","ancho_u","alto_u","pads","tracks"} exportado de EasyEDA Std (unidades 10 mil),
 pads  = "x,y,forma(E/R/O),ancho,alto,rot,radio_barreno;..."   (relativo a la esquina sup-izq del contorno)
 tracks= "grosor:x,y x,y ...;..."                              (solo capa inferior)
Origen de salida: X0 Y0 = esquina inferior izquierda de la placa VISTA DESDE EL COBRE; Z0 = superficie del cobre.
Requiere: pip install shapely matplotlib"""
import sys, json, math, argparse
from shapely.geometry import LineString, Point, box
from shapely.ops import unary_union
a=argparse.ArgumentParser(); a.add_argument('placa')
for k,v in dict(tool=0.2,depth=0.1,passes=2,step=0.15,feed=150,plunge=50,rpm=10000,thick=1.6).items(): a.add_argument('--'+k,type=type(v),default=v)
a.add_argument('--repaso',help='x0,y0,x1,y1 (mm): solo genera las pasadas de aislamiento dentro de ese rectangulo')
o=a.parse_args(); d=json.load(open(o.placa)); U=0.254
BW,BH=d['ancho_u']*U,d['alto_u']*U; N=d['nombre'].replace(' ','_')
pads=[p.split(',') for p in d['pads'].split(';')]
tracks=[(float(t.split(':')[0]),[tuple(map(float,q.split(','))) for q in t.split(':')[1].split(' ')]) for t in d['tracks'].split(';')]
def P(x,y): return (round(BW-x*U,4), round(BH-y*U,4))   # espejo en X: vista desde el cobre
geoms=[LineString([P(x,y) for x,y in pts]).buffer(w*U/2,quad_segs=16) for w,pts in tracks]; holes=[]
for x,y,sh,w,h,rot,hr in pads:
    x,y,w,h=map(float,(x,y,w,h)); cx,cy=P(x,y); w*=U; h*=U
    g=Point(cx,cy).buffer(w/2,quad_segs=16) if sh=='E' else box(cx-w/2,cy-h/2,cx+w/2,cy+h/2) if sh=='R' else LineString([(cx-(w/2-h/2),cy),(cx+(w/2-h/2),cy)]).buffer(h/2,quad_segs=16)
    geoms.append(g); holes.append((cx,cy,float(hr)*2*U))
copper=unary_union(geoms); ZS,ZT=2.0,15.0
def hdr(t,extra): return [f'( {d["nombre"]} - {t} )','( mm, GRBL. X0 Y0 = esquina inf-izq vista desde el cobre. Z0 = cobre )']+[f'( {e} )' for e in extra]+['G21 G90 G17 G94',f'G0 Z{ZT:.3f}',f'M3 S{o.rpm}','G4 P3']
def ftr(): return [f'G0 Z{ZT:.3f}','M5','G0 X0 Y0','M2']
def cut(c,z,f): return [f'G0 Z{ZS:.3f}',f'G0 X{c[0][0]:.3f} Y{c[0][1]:.3f}',f'G1 Z{z:.3f} F{o.plunge}']+[f'G1 X{x:.3f} Y{y:.3f}'+(f' F{f}' if i==0 else '') for i,(x,y) in enumerate(c[1:])]
def rings(g): return sum(([list(p.exterior.coords)]+[list(i.coords) for i in p.interiors] for p in getattr(g,'geoms',[g])),[])
def order(rs):
    rs=rs[:]; cur=(0,0); out=[]
    while rs: i=min(range(len(rs)),key=lambda k:math.dist(cur,rs[k][0])); out.append(rs.pop(i)); cur=out[-1][-1]
    return out
paths=[]
for k in range(o.passes):
    g=copper.buffer(o.tool/2+k*o.step,quad_segs=16).simplify(0.01)
    for r in order(rings(g)):
        if o.repaso:
            x0,y0,x1,y1=map(float,o.repaso.split(',')); c=LineString(r).intersection(box(x0,y0,x1,y1))
            paths+=[list(s.coords) for s in getattr(c,'geoms',[c]) if s.length>0.2]
        else: paths.append(r)
ex=[f'V-bit ancho efectivo {o.tool} mm, prof {o.depth} mm, {o.passes} pasadas cada {o.step} mm, {o.feed} mm/min']
G=hdr('REPASO '+o.repaso if o.repaso else 'Aislamiento',ex)
for p in paths: G+=cut(p,-o.depth,o.feed)
open(f'{N}_{"repaso" if o.repaso else "1_aislamiento"}.nc','w').write('\n'.join(G+ftr())+'\n')
if o.repaso: sys.exit()
bit=lambda dd: 0.8 if dd<1.0 else (1.0 if dd<1.2 else 1.2); grp={}
for x,y,dd in holes: grp.setdefault(bit(dd),[]).append([(x,y)])
zd=-(o.thick+0.2)
for b,pts in sorted(grp.items()):
    D=hdr(f'Barrenos broca {b} mm',[f'{len(pts)} barrenos a {zd} mm'])
    for (x,y), in order(pts): D+=[f'G0 X{x:.3f} Y{y:.3f}','G0 Z0.500',f'G1 Z{zd:.3f} F30',f'G0 Z{ZS:.3f}']
    open(f'{N}_2_barrenos_{str(b).replace(".","_")}mm.nc','w').write('\n'.join(D+ftr())+'\n')
oc=list(box(0,0,BW,BH).buffer(0.5,quad_segs=8).exterior.coords); n=math.ceil((o.thick+0.2)/0.3)
C=hdr('Contorno',['Fresa 1.0 mm por fuera, pasadas de 0.3 mm, sin pestanas'])+[f'G0 Z{ZS:.3f}',f'G0 X{oc[0][0]:.3f} Y{oc[0][1]:.3f}']
for i in range(1,n+1): C+=[f'G1 Z{-min(0.3*i,o.thick+0.2):.3f} F{o.plunge}']+[f'G1 X{x:.3f} Y{y:.3f} F100' for x,y in oc[1:]]
open(f'{N}_3_contorno.nc','w').write('\n'.join(C+ftr())+'\n')
print('min distancia herramienta-cobre:',round(min(LineString(p).distance(copper) for p in paths)-o.tool/2,3),'mm (debe ser >= -0.01)')
