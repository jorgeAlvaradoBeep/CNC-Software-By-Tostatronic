"""bascula_cnc.py - G-code CNC para la PCB de 2 caras Bascula Multiusos (GRBL, mm).
Fase 1 (cara inferior, cobre bottom hacia arriba):
    python3 bascula_cnc.py bottom
Fase 2 (despues de voltear la placa, cara superior hacia arriba):
    python3 bascula_cnc.py top --calib "A:x,y;B:x,y;C:x,y;D:x,y"
  A..D = coordenadas medidas en la CNC (mismo origen de trabajo de la fase 1) del centro
  de cada barreno de calibracion, vistos ya desde la cara superior:
    A = junto a las borneras de la galga (abajo-izq)   B = abajo-der (junto al texto)
    C = arriba-izq (junto al HX711)                    D = arriba-der (junto a la bornera +5V)
  Bastan 2 puntos; con 3 o 4 el script promedia y reporta el error."""
import sys, math, argparse
from shapely.geometry import LineString, Point, box
from shapely.ops import unary_union
from shapely import affinity
a=argparse.ArgumentParser(); a.add_argument('fase',choices=['bottom','top'])
for k,v in dict(tool=0.2,depth=0.1,passes=2,step=0.15,feed=150,plunge=50,rpm=10000,thick=1.6,broca=1.0).items(): a.add_argument('--'+k,type=type(v),default=v)
a.add_argument('--calib'); a.add_argument('--preview',action='store_true')
o=a.parse_args()
W,H=80.0,56.0
# ---- geometria (mm, vista superior, origen esquina inf-izq) ----
P=[]  # x,y,diam,cuadrado
def col(x,ys,sq0,d=1.7):
    for i,y in enumerate(ys): P.append((x,y,d,sq0 and i==0))
ys=[47-2.54*i for i in range(10)]
col(38.38,ys,True); col(53.62,ys,True)
for i,x in enumerate([14.19,16.73,19.27,21.81]): P.append((x,47,1.7,i==0))
for i,x in enumerate([11.65,14.19,16.73,19.27,21.81,24.35]): P.append((x,16.52,1.7,i==0))
for x,sq in [(9,1),(14,0),(20,1),(25,0)]: P.append((x,6.5,2.0,bool(sq)))
P+=[(64,50,2.0,True),(69,50,2.0,False)]
for x in [40.92,43.46,46,48.54,51.08]: P.append((x,10,1.7,x==51.08))
for i in range(7): P.append((70,40-2.54*i,1.7,i==0))
VIAS=[(58,41.92),(33.5,46.5),(56,34.3),(66,34.92),(56.5,39.38),(73,24.76),(35.5,26.68),(16.73,44.5)]
for x,y in VIAS: P.append((x,y,1.8,False))
CAL={'A':(3.5,3.5),'B':(76.5,3.5),'C':(3.5,52.5),'D':(76.5,52.5)}
B,T='B','T'; PW,SW=0.8,0.6
R=[(B,PW,[(53.62,47),(57,47),(60,50),(64,50)]),(B,PW,[(53.62,44.46),(68,44.46),(69,45.46),(69,50)]),
(B,PW,[(68,44.46),(70,42.46),(70,40)]),(B,PW,[(51.08,10),(51.08,43.2),(52.34,44.46),(53.62,44.46)]),
(B,PW,[(51.08,43.2),(51.08,50.5),(14.19,50.5),(14.19,47)]),(B,PW,[(53.62,41.92),(66,41.92),(66,39),(67.54,37.46),(70,37.46)]),
(B,PW,[(70,37.46),(71.5,37.46),(72.5,36.46),(72.5,30.84),(71.5,29.84),(70,29.84)]),
(B,SW,[(53.62,36.84),(56,36.84),(60.46,32.38),(70,32.38)]),(B,SW,[(53.62,29.22),(55.54,27.3),(70,27.3)]),
(B,SW,[(38.38,24.14),(30,24.14),(30,41),(21.27,41),(19.27,43),(19.27,47)]),
(B,SW,[(11.65,16.52),(11.65,11.5),(9,8.85),(9,6.5)]),(B,SW,[(14.19,16.52),(14.19,12),(14,11.81),(14,6.5)]),
# pistas superiores: solo entre vias (puentes de cable), ningun pin de header se suelda arriba
(T,PW,[(58,41.92),(58,47.5),(56.5,49),(35.5,49),(33.5,47),(33.5,46.5)]),(B,PW,[(33.5,46.5),(32,45),(23.81,45),(21.81,47)]),
(B,SW,[(53.62,34.30),(56,34.30)]),(T,SW,[(56,34.30),(56.62,34.92),(66,34.92)]),(B,SW,[(66,34.92),(70,34.92)]),
(B,SW,[(53.62,39.38),(56.5,39.38)]),(T,SW,[(56.5,39.38),(60,39.38),(63,42.38),(71.5,42.38),(73,40.88),(73,24.76)]),(B,SW,[(73,24.76),(70,24.76)]),
(B,SW,[(38.38,26.68),(35.5,26.68)]),(T,SW,[(35.5,26.68),(32.5,26.68),(32.5,42.5),(18.73,42.5),(16.73,44.5)]),(B,SW,[(16.73,44.5),(16.73,47)]),
(B,SW,[(16.73,16.52),(16.73,12),(20,8.73),(20,6.5)]),(B,SW,[(19.27,16.52),(19.27,13),(25,7.27),(25,6.5)]),
(B,SW,[(38.38,41.92),(46.5,41.92),(48.54,39.88),(48.54,10)]),(B,SW,[(38.38,39.38),(44,39.38),(46,37.38),(46,10)]),
(B,SW,[(38.38,36.84),(41.46,36.84),(43.46,34.84),(43.46,10)]),(B,SW,[(38.38,34.30),(39.92,34.30),(40.92,33.3),(40.92,10)])]
def copper(layer):
    g=[LineString(p).buffer(w/2,quad_segs=16) for l,w,p in R if l==layer]
    g+=[box(x-d/2,y-d/2,x+d/2,y+d/2) if sq else Point(x,y).buffer(d/2,quad_segs=16) for x,y,d,sq in P]
    return unary_union(g)
# ---- transformacion a coordenadas de maquina ----
if o.fase=='bottom':
    M=lambda g: affinity.scale(g,-1,1,origin=(W/2,0))           # espejo: vista desde el cobre inferior
    mp=lambda x,y: (W-x,y); layer=B; tag='bottom'
else:
    if not o.calib: sys.exit('Falta --calib "A:x,y;B:x,y;..."')
    meas={k.strip().upper():tuple(map(float,v.split(','))) for k,v in (s.split(':') for s in o.calib.split(';') if s.strip())}
    src=[CAL[k] for k in meas]; dst=[meas[k] for k in meas]
    n=len(src); sx=sum(p[0] for p in src)/n; sy=sum(p[1] for p in src)/n; dx=sum(p[0] for p in dst)/n; dy=sum(p[1] for p in dst)/n
    num=sum((s[0]-sx)*(d[1]-dy)-(s[1]-sy)*(d[0]-dx) for s,d in zip(src,dst)); den=sum((s[0]-sx)*(d[0]-dx)+(s[1]-sy)*(d[1]-dy) for s,d in zip(src,dst))
    th=math.atan2(num,den); c,s_=math.cos(th),math.sin(th)
    mp=lambda x,y: (dx+c*(x-sx)-s_*(y-sy), dy+s_*(x-sx)+c*(y-sy))
    M=lambda g: affinity.affine_transform(g,[c,-s_,s_,c,dx-c*sx+s_*sy,dy-s_*sx-c*sy])
    print(f'rotacion {math.degrees(th):.3f} grados, desplazamiento X{dx-sx:.3f} Y{dy-sy:.3f}')
    for k in meas:
        e=math.dist(mp(*CAL[k]),meas[k]); print(f'  {k}: error {e:.3f} mm')
    if n>=2:
        for k1 in meas:
            for k2 in meas:
                if k1<k2: print(f'  distancia {k1}-{k2}: medida {math.dist(meas[k1],meas[k2]):.3f}  diseno {math.dist(CAL[k1],CAL[k2]):.3f}')
    layer=T; tag='top'
cu=M(copper(layer)); ZS,ZT=2.0,15.0
def hdr(t,extra): return [f'( Bascula Multiusos - {t} )']+[f'( {e} )' for e in extra]+['G21 G90 G17 G94',f'G0 Z{ZT:.3f}',f'M3 S{o.rpm}','G4 P3']
def ftr(): return [f'G0 Z{ZT:.3f}','M5','M2']
def cut(cc,z,f): return [f'G0 Z{ZS:.3f}',f'G0 X{cc[0][0]:.3f} Y{cc[0][1]:.3f}',f'G1 Z{z:.3f} F{o.plunge}']+[f'G1 X{x:.3f} Y{y:.3f}'+(f' F{f}' if i==0 else '') for i,(x,y) in enumerate(cc[1:])]
def rings(g): return sum(([list(p.exterior.coords)]+[list(i.coords) for i in p.interiors] for p in getattr(g,'geoms',[g])),[])
def order(rs):
    rs=rs[:]; cur=(0,0); out=[]
    while rs: i=min(range(len(rs)),key=lambda k:math.dist(cur,rs[k][0])); out.append(rs.pop(i)); cur=out[-1][-1]
    return out
paths=[]
for k in range(o.passes): paths+=order(rings(cu.buffer(o.tool/2+k*o.step,quad_segs=16).simplify(0.01)))
org='X0 Y0 = esquina inf-izq de la placa vista desde el cobre inferior. Z0 = cobre' if tag=='bottom' else 'Mismo origen XY de la fase 1, corregido con los barrenos de calibracion. Z0 = cobre superior (volver a tocar Z)'
G=hdr(f'Aislamiento cara {tag.upper()}',[org,f'V-bit {o.tool} mm, prof {o.depth} mm, {o.passes} pasadas cada {o.step} mm'])
for p in paths: G+=cut(p,-o.depth,o.feed)
pre='1' if tag=='bottom' else '3'
open(f'Bascula_{pre}_{tag}_aislamiento.nc','w').write('\r\n'.join(G+ftr())+'\r\n')
print('min distancia herramienta-cobre:',round(min(LineString(p).distance(cu) for p in paths)-o.tool/2,3),'mm')
zd=-(o.thick+0.2)
if tag=='bottom':
    pts=[mp(x,y) for x,y,d,sq in P]+[mp(*CAL[k]) for k in 'ABCD']
    D=hdr(f'Barrenos broca {o.broca} mm (incluye 4 de calibracion A B C D)',[f'{len(pts)} barrenos a {zd} mm'])
    for (x,y), in order([[p] for p in pts]): D+=[f'G0 X{x:.3f} Y{y:.3f}','G0 Z0.500',f'G1 Z{zd:.3f} F30',f'G0 Z{ZS:.3f}']
    open('Bascula_2_barrenos_1_0mm.nc','w').write('\r\n'.join(D+ftr())+'\r\n')
else:
    # contorno + agrandar barrenos de montaje a 3.2 mm, fresa recta de 1.0 mm
    n=math.ceil((o.thick+0.2)/0.3); C=hdr('Barrenos M3 y contorno',['Fresa recta 1.0 mm. Primero agranda A B C D a 3.2 mm, luego corta el contorno por fuera'])
    for k in 'ABCD':
        cx,cy=mp(*CAL[k]); circ=list(Point(cx,cy).buffer(1.1,quad_segs=16).exterior.coords)
        C+=[f'G0 Z{ZS:.3f}',f'G0 X{circ[0][0]:.3f} Y{circ[0][1]:.3f}']
        for i in range(1,n+1): C+=[f'G1 Z{-min(0.3*i,o.thick+0.2):.3f} F{o.plunge}']+[f'G1 X{x:.3f} Y{y:.3f} F100' for x,y in circ[1:]]
    oc=list(M(box(0,0,W,H)).buffer(0.5,quad_segs=8,join_style=1).exterior.coords)
    C+=[f'G0 Z{ZS:.3f}',f'G0 X{oc[0][0]:.3f} Y{oc[0][1]:.3f}']
    for i in range(1,n+1): C+=[f'G1 Z{-min(0.3*i,o.thick+0.2):.3f} F{o.plunge}']+[f'G1 X{x:.3f} Y{y:.3f} F100' for x,y in oc[1:]]
    open('Bascula_4_contorno.nc','w').write('\r\n'.join(C+ftr())+'\r\n')
if o.preview:
    import matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as plt
    fig,ax=plt.subplots(figsize=(10,7.4)); ax.set_aspect('equal')
    for pg in getattr(cu,'geoms',[cu]): ax.fill(*pg.exterior.xy,color='#c87533')
    for p in paths: ax.plot(*zip(*p),lw=0.5,color='k')
    for x,y,d,sq in P: X,Y=mp(x,y); ax.plot(X,Y,'o',ms=2.5,color='w',mec='k',mew=0.4)
    for k in 'ABCD':
        X,Y=mp(*CAL[k]); ax.plot(X,Y,'o',ms=7,mfc='none',mec='red',mew=1.5); ax.text(X+1.6,Y+1.3,k,color='red',fontsize=13,weight='bold')
    bx=M(box(0,0,W,H)); ax.plot(*bx.exterior.xy,'--',color='purple',lw=0.8)
    ax.plot(0,0,'+',color='blue',ms=14,mew=2) ; ax.text(0.8,-2.6,'X0 Y0',color='blue')
    ax.set_title(f'Bascula Multiusos - cara {tag.upper()} (vista desde arriba en la CNC)'); ax.grid(alpha=0.25)
    ax.set_xlim(-4,W+4); ax.set_ylim(-5,H+4); fig.tight_layout(); fig.savefig(f'Bascula_vista_{tag}.png',dpi=130)
