# Plan: Tostatronic PCB CNC

Programa de escritorio Windows que reemplaza al software Wegstr: controla la CNC directamente
(protocolo binario documentado en `wegstr-analisis.md`) y genera las trayectorias de PCB desde
Gerber/Excellon de EasyEDA o ejecuta G-code ya hecho, con calibración de altura estilo Wegstr,
una o dos caras. Interfaz en español.

## 1. Stack

- **C# .NET 8, WPF**, MVVM con **CommunityToolkit.Mvvm**.
- **System.IO.Ports** para el serial (9600 8N1 es trivial; sin dependencias nativas).
- **Clipper2** (`Clipper2` en NuGet) para uniones y desplazamientos de polígonos (aislamiento).
  Es la misma matemática que usa Shapely en tus scripts (offset + unión), determinista y rápida.
- **Parser Gerber RS-274X y Excellon propios** (subconjunto que emite EasyEDA Std). No hay
  librería .NET madura y mantenida para Gerber; el dialecto de EasyEDA es acotado (apertures
  C/R/O/P, D01/D02/D03, G01/G02/G03, FS LA, MO MM/IN, LP, y poco más) y tenerlo propio nos deja
  controlar los avisos. Excellon es sencillo (T/X/Y, INCH/METRIC).
- Vista previa 2D con **DrawingVisual/StreamGeometry de WPF** (sin librería de gráficos; zoom y
  pan propios). Es suficiente y evita dependencias.
- **xUnit** para pruebas. JSON (`System.Text.Json`) para presets y sesiones.

## 2. Estructura de la solución

```
Tostatronic.Cnc.sln
├── src/Tostatronic.Cnc.Core          (sin UI, sin WPF)
│   ├── Protocol/      codificador de paquetes, enlace serial, flow control, ISerialLink,
│   │                  SimulatedMachine (firmware falso con superficie sintética y sonda)
│   ├── Motion/        planeador: G-code → pasos (Bresenham 3D, rampas, arcos, F clamp),
│   │                  contadores de posición, jog, GO ZERO
│   ├── GCode/         parser modal + expansión de ciclos + streaming con pausa/paro/progreso
│   ├── AutoLevel/     rejilla sobre trayectorias, secuencia de sondeo, mapa de alturas,
│   │                  aplicación (±1 paso + backlash), persistencia por placa y por cara
│   └── TwoSided/      transformación rígida (giro+desplazamiento, mínimos cuadrados),
│   │                  asistente de 4 puntos, predicción del siguiente punto
├── src/Tostatronic.Cnc.Cam           (sin UI)
│   ├── Gerber/        parser RS-274X; Excellon/   parser de barrenos; Zip/  carga del ZIP EasyEDA
│   ├── Isolation/     unión de cobre (Clipper2), pasadas de aislamiento, chequeo de separación
│   │                  mínima vs ancho efectivo, repaso por rectángulo
│   └── Jobs/          barrenos (agrupado por broca o broca única), contorno, espejo cara
│                      inferior, emisión de G-code (dialecto GRBL-compatible como tus .nc)
├── src/Tostatronic.Cnc.App           (WPF, español)
└── tests/Tostatronic.Cnc.Tests
    └── fixtures/      G-code de pcb_cnc.py/bascula_cnc.py y Gerbers de EasyEDA reales
```

Decisión clave: **el G-code es el formato intermedio universal**. Todo (generado o cargado)
pasa por el mismo pipeline: `G-code → planeador de pasos → auto level → paquetes serial`.
Así el repaso, las dos caras y el G-code externo reciben exactamente el mismo tratamiento.

## 3. Fases (cada una termina en algo que puedes probar con la máquina)

### Fase A — Conexión, jog y envío de G-code
- Protocolo completo (paquetes, ACK, timeout, detección de puerto, `0x7F`).
- **Modo simulación** desde el día 1: `SimulatedMachine` implementa `ISerialLink` con la cadencia
  y respuestas del firmware (incl. toque de sonda contra una superficie sintética inclinada).
  Toda la UI funciona sin máquina.
- UI: conexión/búsqueda, consola de diagnóstico, jog continuo + **jog por pasos (0.01/0.1/1/10)**,
  fijar ceros, GO ZERO, husillo, velocidad %, **paro de emergencia siempre visible** (deja de
  mandar y descarta cola), carga de .nc/.gcode, envío con barra de progreso, pausa/reanudar
  (con retorno a posición pausada), M0/M6 guiados.
- Prueba contigo: mover la máquina, Z0 por toque, correr un .nc de tus scripts.

### Fase B — Calibración de altura
- Rejilla sobre trayectorias (raster configurable, default 1.0 mm), sondeo, mapa de alturas,
  aplicación con rampa y backlash. Verificación del signo de Z del firmware (prueba guiada).
- Guardar/cargar el mapa por placa y por cara (archivo de sesión JSON junto al proyecto).
- Vista del mapa medido (colores sobre la vista previa) y error por punto.
- Prueba contigo: placa real con auto level, comparar contra el resultado de Wegstr.

### Fase C — Gerber/Excellon → trayectorias de una cara
- Carga del ZIP de EasyEDA (o archivos sueltos): cobre superior/inferior, contorno, Excellon.
- Aislamiento sobre la **unión de todo el cobre** (Clipper2), N pasadas separadas `step`,
  ordenamiento por cercanía, aviso si la separación mínima entre redes < ancho efectivo.
- Barrenos (broca única 1.0 mm o agrupados), contorno por fuera con pasadas de 0.3 mm.
- Origen X0 Y0 esquina inferior izquierda **vista desde el cobre fresado** (cara inferior en
  espejo X), Z0 en el cobre. Vista previa 2D completa (cobre, pasadas, barrenos, contorno, origen).
- Trabajos separados con cambio de herramienta guiado y re-sondeo de Z entre trabajos.
- **Pruebas contra tus scripts**: mismos datos de entrada → comparar geometría de pasadas y
  G-code con la salida de `pcb_cnc.py` (tolerancia numérica).
- Presets editables (JSON): "V 0.3 mm probado" como default, V 0.2 y V 0.1 seleccionables, y
  todos los parámetros de tu tabla.

### Fase D — Dos caras con pines de calibración
- 4 barrenos de calibración en la cara 1 (de montaje del diseño o elegidos en la vista previa),
  numerados 1=inf-izq, 2=sup-izq, 3=sup-der, 4=inf-der; volteo horizontal.
- Asistente post-volteo exactamente como lo describes: capturar 4 → predecir y mandar la máquina
  al 3 → ajustar/capturar → con 2 puntos calcular giro+desplazamiento (sin escala ni espejo) →
  predecir 2 → predecir 1 → ajuste final por mínimos cuadrados con error por punto y comparación
  de distancias medidas vs diseño; umbral 0.1 mm con recaptura por punto.
- Aplicar la transformación al G-code de la cara 2; recordatorio de Z0; sondeo propio de la cara 2.
- Sesión persistente por placa (puntos, transformación, mapas de altura) para retomar.
- Pruebas: la matemática de la transformación contra `bascula_cnc.py` (mismos puntos → misma
  rotación/desplazamiento/errores), incluido el volteo.

### Fase E — Repaso y comodidades
- Repaso: rectángulo sobre la vista previa → regenerar solo las pasadas de aislamiento dentro,
  sin mover el origen, con opción de más profundidad (y aplicando el mapa de alturas vigente).
- Pulido: atajos de teclado, historial de archivos, exportar .nc, chequeos previos al envío.

## 4. Pruebas automáticas

- Protocolo: codificación de paquetes contra casos calculados a mano (bits, signos, Z≥8, husillo).
- Planeador: distancia total en pasos = suma de deltas; límites ±14; velocidades.
- Parser G-code: líneas del sample oficial de Wegstr y de tus .nc.
- Gerber/Excellon: fixtures reales de EasyEDA.
- Trayectorias: comparación contra la salida de tus scripts Python con los mismos insumos.
- Dos caras: transformación rígida (casos con rotación conocida, error esperado), volteo.
- Auto level: celdas elegidas, orden, aplicación de diffs con rampa y backlash (simulador).

## 5. Preguntas abiertas (no bloquean la Fase A)

1. ¿La vista previa debe mostrar también una estimación de tiempo de trabajo?
2. Para el ZIP de EasyEDA: ¿exportas Gerber con el nombre de capas por defecto de EasyEDA Std
   (p. ej. `Gerber_TopLayer.GTL`, `Gerber_BottomLayer.GBL`, `Gerber_BoardOutline.GKO`,
   `Drill_PTH_Through.DRL`)? Si me pasas un ZIP de ejemplo de un diseño tuyo, lo uso como fixture.
3. ¿Quieres que el programa también pueda *exportar* los .nc generados (para archivo), además de
   mandarlos directo? (Asumo que sí; es barato.)
