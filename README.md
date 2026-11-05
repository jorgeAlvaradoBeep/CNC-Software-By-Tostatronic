# Tostatronic PCB CNC

**Software libre de escritorio (Windows) para controlar la CNC [Wegstr](https://www.wegstr.com) sin el software original, pensado para fresar circuitos impresos (PCB).**

Hecho en C# (.NET 8 + WPF), con interfaz en español. Nace de la necesidad de un flujo completo en un solo programa: controlar la máquina, mandar G-code con calibración de altura estilo Wegstr y, en fases siguientes, generar las trayectorias directamente desde los Gerber/Excellon de EasyEDA — incluyendo placas de dos caras con pines de calibración.

> ⚠️ **Proyecto en desarrollo activo.** Hoy están completas las **Fases A, B, C y E** (control de máquina, envío de G-code, calibración de altura, generación de trayectorias desde Gerber con validaciones y repaso de pistas, con modo simulación); C se validó con exportaciones reales de EasyEDA Std y de Altium Designer, y B se contrastó con una captura del tráfico real del software original fresando una placa (ver [`docs/wegstr-analisis.md` §10](docs/wegstr-analisis.md)); B y C aún están pendientes de probarse con nuestra app en la máquina. Ver el [plan por fases](docs/plan.md).

## ¿Por qué?

La Wegstr es una CNC excelente para PCB, pero su software es cerrado y no genera trayectorias: dependes de un CAM externo y de su flujo de trabajo. Este proyecto documenta el protocolo de la máquina mediante ingeniería inversa (solo para interoperabilidad; **no contiene código del software original**) y lo reimplementa desde cero, agregando lo que faltaba:

- Jog por pasos discretos (0.01 / 0.1 / 1 / 10 mm), además del continuo.
- Trabajo reanudable incluso tras un corte de comunicación.
- Modo simulación completo para probar sin la máquina.
- Gerber/Excellon → trayectorias de aislamiento, barrenos y contorno, con vista previa y validación de la placa (una o dos capas, cobre suelto, pads muy juntos, anillos delgados, contorno faltante).
- Giro automático de 90° cuando la placa no cabe en el recorrido de la máquina.
- Repaso de pistas: marcar sobre el dibujo qué anillos o zonas volver a fresar, sin mover el origen.
- (En camino) Dos caras con asistente de calibración.

## El protocolo Wegstr, descifrado

La máquina **no habla GRBL**: usa un protocolo binario propio a 9600 baudios donde la PC manda ráfagas de micro-movimientos en pasos de motor (0.004 mm/paso) y la máquina contesta un byte de estado — incluido el toque de la sonda de Z, con el que hace su calibración de altura punto por punto sobre las pistas. El análisis completo (formato de paquetes, respuestas, control de flujo, autonivelación, G-code soportado) está en [`docs/wegstr-analisis.md`](docs/wegstr-analisis.md).

## Características (Fases A–C)

- **Conexión**: detección automática del puerto (CH340), keep-alive, consola con tráfico TX/RX opcional.
- **Control manual**: jog continuo con rampa de aceleración o por pasos fijos; también con teclado (flechas y Re Pág/Av Pág); velocidad ajustable en vivo (10–100 %).
- **Ceros de trabajo** por eje o todos; **IR A CERO** con altura segura opcional.
- **Z0 automático al toque**: baja con jog y, al cerrar el circuito de la sonda (pinza al cobre), la máquina se detiene sola y el programa fija Z0 en el contacto; queda bloqueado bajar más hasta subir Z.
- **Husillo** encendido/apagado, con detección de sobrecarga y del botón físico de la máquina.
- **G-code**: carga de `.nc`/`.gcode` (G0/G1/G2/G3 con I/J, ciclos G73/G81–G83, G4, M0/M2/M3/M4/M5/M6/M30/M47, F máx 170 mm/min), barra de progreso, línea actual, tiempo transcurrido.
- **Pausa / reanudar / parada**: si mueves la máquina durante la pausa, al reanudar regresa primero en XY y luego en Z; un timeout de comunicación deja el trabajo en pausa reanudable, no lo pierde.
- **Cambio de herramienta guiado** (M6 + M0).
- **Paro de emergencia** siempre visible: corta el flujo de paquetes (la máquina termina a lo más ~0.17 mm) y apaga el husillo. *El botón físico del equipo sigue siendo el paro duro.*
- **Calibración de altura (auto level) estilo Wegstr**: con el G-code cargado, sondea la placa **sobre las trayectorias de corte reales** (rejilla de 1 mm sobre las pistas, no una malla ciega), toma la referencia en X0 Y0 y durante el trabajo ajusta Z celda a celda (±1 paso por comando en cortes, desnivel completo en las bajadas, compensación de backlash de Z). El mapa se guarda junto al G-code (`.nivel.json`).
- **PCB desde Gerber (CAM integrado)**: abre el ZIP de fabricación que exporta **EasyEDA, Altium Designer o KiCad** (o una carpeta con los archivos sueltos) y genera el aislamiento (offsets de la unión del cobre, N pasadas), los barrenos (brocas agrupadas 0.8/1.0/1.2 o broca única) y el contorno (por fuera, pasadas de 0.3 mm), con el mismo origen y formato de G-code del flujo probado de Tostatronic: X0 Y0 = esquina inferior izquierda vista desde el cobre (cara inferior en espejo; la superior sin espejo), Z0 = cobre. Incluye vista previa 2D con las pasadas al ancho real de la herramienta, presets editables, carga directa del .nc a la máquina y un reporte de validación que se guarda junto a los .nc. Parser propio de Gerber RS-274X (aperturas estándar y macros, arcos, regiones, polaridad, pulgadas con ceros finales omitidos, `G54Dnn` y códigos D modales como los de Altium) y Excellon; el taladro también puede venir como Gerber (`PTH_Drill.gbr` de Altium).
- **Validación de la placa** antes de fresar: detecta si es de **una capa** (la cara que trae pistas; la otra solo proyecta los pads pasantes) o de dos, y elige la cara a fresar; descarta y avisa del **cobre suelto** lejos de la placa (una pista olvidada fuera del diseño agrandaría la placa); marca en la vista previa con una × roja **dónde dos redes quedan más cerca que el ancho efectivo** de la V-bit (ahí el aislamiento no las separa); avisa de barrenos fuera del cobre y de pads con **anillo de cobre** menor a 0.25 mm; si no hay capa de contorno usa el rectángulo del cobre más un margen configurable.
- **Orientación**: si la placa no cabe en el recorrido de la máquina (configurable, por defecto 100 × 140 mm) pero sí girada, la **gira 90°** sola (también se puede forzar o impedir); los archivos salen con sufijo `_girada` y lo dicen en el encabezado. Si no cabe de ninguna forma, avisa.
- **Repaso de pistas** (ventana propia, o desde el CAM con el aislamiento recién generado): carga un `.nc` de aislamiento (el de la app o los de `pcb_cnc.py` / `detector_cnc.py`), dibuja sus trayectorias numeradas y deja **marcar con clic** las que hay que volver a fresar, **quedarse con una sección** de una pista enorme (Ctrl + arrastrar a lo largo de ella: solo sale ese tramo, p. ej. el centro donde la placa se pandea) o **encerrar zonas** (Shift + arrastrar) de las que solo sale lo que queda dentro; se exporta un `<archivo>_seleccion.nc` con el mismo encabezado, origen y cierre que el original, a la profundidad que se pida, y se puede mandar directo a la máquina. La selección se guarda junto al archivo (`.repaso.json`). Admite un segundo `.nc` de fondo para ubicarse.
- **Línea de comandos** (`tostatronic-cnc gerber <zip> [--salida DIR] [--cara] [--girar] [--recorrido 100x140] [--margen 2] ...` y `tostatronic-cnc repaso <archivo.nc> --anillos 1,5 --zona x0,y0,x1,y1 --tramo 92:80,40:80,90 --depth 0.12`) para convertir desde un script, con el mismo reporte.
- **Modo simulación**: un firmware falso ejecuta los paquetes a la cadencia real, con placa simulada (ligeramente inclinada, para que la calibración tenga algo que medir) y sonda funcional, para probar todo sin hardware.

## Compilar y correr

Requisitos: Windows, [.NET SDK 8](https://dotnet.microsoft.com/download) o superior. Para la máquina real, el driver CH340 (viene con el software original de Wegstr).

```powershell
git clone https://github.com/jorgeAlvaradoBeep/CNC-Software-By-Tostatronic.git
cd CNC-Software-By-Tostatronic
dotnet run --project "src/Tostatronic.Cnc.App"
```

Pruebas:

```powershell
dotnet test
```

Primer vuelo sin máquina: marca **“Simulación (sin máquina)”**, pulsa **Conectar**, baja Z con el jog hasta el toque (la placa simulada está 5 mm abajo), carga un `.nc` de [`examples gcode/`](examples%20gcode/) y pulsa **CALIBRAR ALTURA**: verás a la "máquina" sondear las pistas y medir la inclinación de la placa simulada antes de correr el trabajo compensado.

Convertir sin abrir la app (deja los `.nc` y `<placa>_reporte.txt` junto al ZIP, o en `--salida`):

```powershell
dotnet run --project src/Tostatronic.Cnc.Cli -- gerber "C:\ruta\PCB_MiPlaca.zip" --salida "C:\ruta\gcode"
```

La app también acepta argumentos: `Tostatronic.Cnc.App.exe archivo.nc` lo carga en la máquina y `--repaso archivo.nc` abre el repaso de pistas con ese archivo.

## Estructura

```
src/Tostatronic.Cnc.Core   protocolo binario, planeador de pasos, parser de G-code,
                           controlador de máquina, calibración de altura, simulador
src/Tostatronic.Cnc.Cam    parsers de Gerber/Excellon, carga y validación de la placa, geometría
                           (Clipper2), generación de aislamiento/barrenos/contorno con giro, presets,
                           emisión de G-code, reporte, repaso de pistas (selección y recorte)
src/Tostatronic.Cnc.Cli    línea de comandos: gerber → .nc + reporte, repaso sin interfaz
src/Tostatronic.Cnc.App    interfaz WPF (español): control, CAM con vista previa, repaso de pistas
tests/                     pruebas xUnit (usan los G-code reales de "examples gcode")
docs/                      análisis del protocolo Wegstr y plan del proyecto
examples gcode/            G-code real de PCB fabricadas con este flujo
examples gerber/           Gerber de ejemplo para la ventana "PCB desde Gerber"
```

## Hoja de ruta

| Fase | Contenido | Estado |
|---|---|---|
| A | Conexión, jog, ceros, husillo, envío de G-code, simulador | ✅ |
| B | Calibración de altura estilo Wegstr (sondeo sobre las pistas) | ✅ |
| C | Gerber/Excellon (EasyEDA, Altium, KiCad) → aislamiento, barrenos y contorno, vista previa 2D, validaciones, giro 90° | ✅ |
| D | Dos caras con pines de calibración (asistente de volteo, ajuste por mínimos cuadrados) | ⏳ |
| E | Repaso de pistas por anillo y por zona, línea de comandos | ✅ (falta historial de archivos y chequeos previos al envío) |

## Aviso

Este software controla una máquina-herramienta real. Úsalo bajo tu propio riesgo, nunca dejes la máquina sin supervisión y verifica los recorridos en modo simulación antes de cortar. El proyecto no está afiliado a Wegstr; el análisis del protocolo se hizo únicamente para lograr interoperabilidad con hardware propio.

---

Hecho por **Tostatronic** (Jorge Alvarado) con ayuda de Claude.
