# Proceso: PCB fresada en CNC (EasyEDA → G-code)

Proceso que se usó con el **Astable LM555 v2** (octubre 2026) y que salió bien a la primera, salvo un tramo que se repasó. Después se extendió a dos caras con la **Báscula Multiusos**. Generadores de referencia: `pcb_cnc.py` (una cara) y `bascula_cnc.py` (dos caras con pines de calibración).

## 1. Diseño en EasyEDA Std
- Una cara: todo el cobre en la **capa inferior**, sin vías ni puentes.
- Pistas de 0.8 mm y separación mínima de 0.4 mm.
- Dos caras (casero, sin metalizado): las pistas de la cara superior van de vía a vía (puentes de cable), para no soldar pines de headers por arriba.

## 2. Extraer la geometría de EasyEDA
Con el PCB abierto, en la consola del navegador (F12) se ejecuta este código. Copia al portapapeles los pads y las pistas de la capa inferior, relativos a la esquina superior izquierda del contorno de la placa:

```js
const s = easyeda.extension.exec(api => api('getSource',{type:'json'}));
const ol = Object.values(s.TRACK).find(t => t.layerid=='10').pointArr;   // contorno
const bx = Math.min(...ol.map(p=>p.x)), by = Math.min(...ol.map(p=>p.y));
const W = Math.max(...ol.map(p=>p.x))-bx, H = Math.max(...ol.map(p=>p.y))-by, r = v => (+v).toFixed(2);
const pads = []; for (const f of Object.values(s.FOOTPRINT)) for (const p of Object.values(f.PAD))
  pads.push([r(p.x-bx),r(p.y-by),p.shape[0],r(p.width),r(p.height),p.rotation||0,r(p.holeR)].join(','));
const tracks = Object.values(s.TRACK).filter(t=>t.layerid=='2')
  .map(t => r(t.strokeWidth)+':'+t.pointArr.map(p=>r(p.x-bx)+','+r(p.y-by)).join(' '));
copy(JSON.stringify({nombre:'MI PLACA', ancho_u:+r(W), alto_u:+r(H), pads:pads.join(';'), tracks:tracks.join(';')}));
```

Se pega en un archivo `placa.json`. Las unidades de EasyEDA Std son de 10 mil (0.254 mm).

## 3. Generar el G-code (una cara)
```
pip install shapely matplotlib
python3 pcb_cnc.py placa.json
```
Salen estos archivos, en orden de uso:
1. `*_1_aislamiento.nc`: V-bit, dos pasadas alrededor de cada red.
2. `*_2_barrenos_0_8mm.nc`, `_1_0mm`, `_1_2mm`: uno por broca. Agrupa así: menos de 1.0 mm → broca de 0.8; de 1.0 a menos de 1.2 → 1.0; 1.2 o más → 1.2. (También se usó una sola broca de 1.0 mm para todo.)
3. `*_3_contorno.nc`: fresa recta de 1.0 mm por fuera, pasadas de 0.3 mm, sin pestañas.

El script imprime la distancia mínima de la herramienta al cobre; debe ser ≥ −0.01 mm.

## Origen (el mismo en todos los archivos)
- **X0 Y0:** esquina inferior izquierda de la placa **vista desde el cobre** (la placa va con el cobre hacia arriba, así que el diseño sale en espejo respecto a EasyEDA).
- **Z0:** superficie del cobre.
- Formato GRBL, en mm.

## Parámetros que funcionaron
| Parámetro | Valor |
|---|---|
| Broca | V-bit de 0.3 mm, configurada con ancho efectivo 0.2 mm (`--tool 0.2`) |
| Profundidad | 0.10 mm |
| Pasadas | 2, separadas 0.15 mm (quedan unos 0.35 mm aislados) |
| Avance / bajada | 150 / 50 mm/min |
| Husillo | 10 000 rpm |
| Placa | 1.6 mm de espesor; barrenos a −1.8 mm, F30 |

Se probó configurar el ancho efectivo en 0.3 mm (`--tool 0.3 --step 0.25`), pero el resultado bueno fue con los parámetros de la tabla.

## 4. Repasar un tramo que no se marcó
Si un tramo no marcó completo (por ejemplo, porque la placa no está del todo plana):
```
python3 pcb_cnc.py placa.json --repaso x0,y0,x1,y1
```
El rectángulo va en mm, en el mismo sistema de la CNC. Se generan solo las pasadas de aislamiento que caen dentro, sin mover el origen.
- Conviene hacer el rectángulo ajustado: toma **todas** las pasadas que caen dentro, incluidas las de pads vecinos.
- En el Astable LM555 v2 faltó el lado de abajo de la pista horizontal superior, del centro a la derecha. Se repasó con `--repaso 25.8,41.3,47.0,42.7` y funcionó.
- Si el tramo falló por falta de profundidad, se puede usar `--depth 0.15` solo para el repaso.

## 5. Dos caras (bascula_cnc.py)
1. `python3 bascula_cnc.py bottom` → aislamiento de la cara inferior y barrenos (piezas, vías y 4 barrenos de calibración A-D). Origen igual que en una cara.
2. Voltear la placa de izquierda a derecha sin mover el origen XY; volver a tocar Z0 en el cobre superior. Medir en la CNC el centro de A, B, C y D.
3. `python3 bascula_cnc.py top --calib "A:x,y;B:x,y;C:x,y;D:x,y"` → ajuste rígido (giro + desplazamiento, mínimos cuadrados), error por punto, aislamiento superior corregido y contorno (agranda A-D a 3.2 mm y corta por fuera).

## Antes de generar, conviene revisar
- Una vista previa de los recorridos (cobre, aislamiento, barrenos y contorno).
- Que la separación mínima entre redes sea mayor que el ancho efectivo de la V-bit.
