# Tostatronic PCB CNC

**Software libre de escritorio (Windows) para controlar la CNC [Wegstr](https://www.wegstr.com) sin el software original, pensado para fresar circuitos impresos (PCB).**

Hecho en C# (.NET 8 + WPF), con interfaz en español. Nace de la necesidad de un flujo completo en un solo programa: controlar la máquina, mandar G-code con calibración de altura estilo Wegstr y, en fases siguientes, generar las trayectorias directamente desde los Gerber/Excellon de EasyEDA — incluyendo placas de dos caras con pines de calibración.

> ⚠️ **Proyecto en desarrollo activo.** Hoy están completas las **Fases A, B y C** (control de máquina, envío de G-code, calibración de altura y generación de trayectorias desde Gerber/Excellon, con modo simulación); B y C aún están pendientes de validarse con la máquina real y con un ZIP real de EasyEDA. Ver el [plan por fases](docs/plan.md).

## ¿Por qué?

La Wegstr es una CNC excelente para PCB, pero su software es cerrado y no genera trayectorias: dependes de un CAM externo y de su flujo de trabajo. Este proyecto documenta el protocolo de la máquina mediante ingeniería inversa (solo para interoperabilidad; **no contiene código del software original**) y lo reimplementa desde cero, agregando lo que faltaba:

- Jog por pasos discretos (0.01 / 0.1 / 1 / 10 mm), además del continuo.
- Trabajo reanudable incluso tras un corte de comunicación.
- Modo simulación completo para probar sin la máquina.
- Gerber/Excellon → trayectorias de aislamiento, barrenos y contorno, con vista previa.
- (En camino) Dos caras con asistente de calibración y repaso por zona.

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
- **PCB desde Gerber (CAM integrado)**: abre el ZIP de fabricación que exporta EasyEDA (o cualquier CAD) y genera el aislamiento (offsets de la unión del cobre, N pasadas), los barrenos (brocas agrupadas 0.8/1.0/1.2 o broca única) y el contorno (por fuera, pasadas de 0.3 mm), con el mismo origen y formato de G-code del flujo probado de Tostatronic: X0 Y0 = esquina inferior izquierda vista desde el cobre (cara inferior en espejo), Z0 = cobre. Incluye vista previa 2D con las pasadas al ancho real de la herramienta, aviso cuando dos redes quedan más cerca que el ancho efectivo, presets editables y carga directa del .nc a la máquina. Parser propio de Gerber RS-274X (aperturas estándar y macros, arcos, regiones, polaridad) y Excellon.
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

## Estructura

```
src/Tostatronic.Cnc.Core   protocolo binario, planeador de pasos, parser de G-code,
                           controlador de máquina, calibración de altura, simulador
src/Tostatronic.Cnc.Cam    parsers de Gerber/Excellon, geometría (Clipper2), generación
                           de aislamiento/barrenos/contorno, presets, emisión de G-code
src/Tostatronic.Cnc.App    interfaz WPF (español)
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
| C | Gerber/Excellon (EasyEDA) → aislamiento, barrenos y contorno, vista previa 2D | ✅ |
| D | Dos caras con pines de calibración (asistente de volteo, ajuste por mínimos cuadrados) | ⏳ |
| E | Repaso por zona y comodidades | ⏳ |

## Aviso

Este software controla una máquina-herramienta real. Úsalo bajo tu propio riesgo, nunca dejes la máquina sin supervisión y verifica los recorridos en modo simulación antes de cortar. El proyecto no está afiliado a Wegstr; el análisis del protocolo se hizo únicamente para lograr interoperabilidad con hardware propio.

---

Hecho por **Tostatronic** (Jorge Alvarado) con ayuda de Claude.
