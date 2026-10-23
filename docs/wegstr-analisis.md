# Análisis del software Wegstr v3.2.0

Ingeniería inversa del software original de la máquina (carpeta `Wegstr_v3.2.0`), hecha para
lograr compatibilidad con la CNC Wegstr desde nuestro propio programa. **No se copió ni se va a
redistribuir código descompilado**: este documento describe el comportamiento observado y nuestro
software lo implementa desde cero.

Fecha del análisis: 2026-10-20.

---

## 1. En qué está hecho

**No es .NET: es una aplicación Java (Swing)**, empaquetada con jpackage y un runtime Java 21
incluido. La lógica está en `Windows x64\app\wegstr.jar` (~7 MB). Se descompiló con CFR 0.152.

- El código propio (`com.cnc.wegstr.*`) está ofuscado (clases `a`, `b`, `c`...), pero los
  **enums, eventos y textos de recursos conservan sus nombres reales**, lo que permitió mapear
  todo el comportamiento: `AutoLevelDiffEvent`, `SpindleOverloadEvent`, `Status.AUTO_LEVEL`, etc.
- Librerías: **jssc** (serial), un bus de eventos (`com.kk.bus`), Jakarta Mail (envío de
  diagnósticos), slf4j. Nada de CAM: **Wegstr no genera trayectorias, solo ejecuta G-code**.
- El driver incluido es **CH340/CH341** (puente USB‑serial de WCH, el mismo de los clones de
  Arduino). La electrónica de la máquina es un microcontrolador detrás de un CH340. Se confirma
  la hipótesis de "Arduino por USB/serial".

## 2. Comunicación con la CNC

### 2.1 Puerto serial
- **9600 baudios, 8 datos, 1 stop, sin paridad, sin control de flujo por hardware.**
  DTR y RTS se activan al abrir. (Los parámetros están cableados en el código: aunque la llamada
  recibe argumentos, siempre configura 9600 8N1.)
- Lectura por evento RXCHAR (llegó al menos un byte); se lee todo lo disponible.

### 2.2 El protocolo NO es GRBL ni texto
Es un **protocolo binario propio, de nivel muy bajo**: la PC hace toda la planeación de
movimiento (interpolación, rampas, arcos, compensaciones) y manda ráfagas de **micro-movimientos
en pasos de motor**. El firmware solo ejecuta pasos, vigila la sonda y el husillo, y contesta
bytes de estado. No hay posiciones absolutas, ni homing, ni comandos de texto.

### 2.3 Paquetes PC → máquina

| Cabecera | Tamaño | Significado |
|---|---|---|
| `0xFF` | 7 bytes | 3 micro-movimientos; **sin** detección de toque de sonda |
| `0x3F` | 7 bytes | 3 micro-movimientos; **con** detección de toque (se usa en jog, en auto level y para detectar la máquina) |
| `0x7F` | 2 bytes | Ajuste: `[0x7F, rendimiento]` con rendimiento = 2..20 (default 4; 2 = PC rápida). Se manda cada 1 s cuando la máquina está conectada y ociosa |

Cada paquete de movimiento lleva **exactamente 3 comandos de 2 bytes**. Cada comando codifica
deltas de pasos ΔX, ΔY, ΔZ en el rango **−14..+14** (15 se recorta a 14) y el estado del husillo:

```
Byte 1: [ z2 z1 z0 | sx | x3 x2 x1 x0 ]
         bits 5-7: parte baja de |ΔZ| (si |ΔZ| ≥ 8 se guarda |ΔZ|−8)
         bit 4   : signo de X (1 = negativo)
         bits 0-3: |ΔX|

Byte 2: [ sp | sz | z8 | sy | y3 y2 y1 y0 ]
         bit 7   : husillo (1 = encendido)  ← viaja en CADA comando
         bit 6   : signo de Z
         bit 5   : 1 si |ΔZ| ≥ 8
         bit 4   : signo de Y
         bits 0-3: |ΔY|
```

- Husillo encendido/apagado = mandar un paquete (aunque sea de movimientos 0,0,0) con el bit 7
  en 1/0. **No hay control de RPM por software**: la `S` del G-code se ignora.
- El firmware ejecuta los comandos a cadencia fija. De la fórmula de velocidad (ver 2.6) se
  infiere ≈ **50.6 comandos/s** (un paquete de 3 cada ~59 ms). A 9600 baudios sobra ancho de banda.
  **Confirmado en hardware (§10)**: el ACK llega 59 ms después de cada paquete y Wegstr manda el
  siguiente 0.1 ms después del ACK → 50.8 comandos/s.

### 2.4 Respuestas máquina → PC (1 byte, salvo el toque)

| Byte | Significado | Reacción de Wegstr |
|---|---|---|
| `0x01` | ACK / listo para el siguiente paquete | Manda el siguiente paquete de la cola |
| `0x02` | **Husillo sobrecargado** | Detiene el proceso/jog, apaga husillo, avisa |
| `0x03` | **Máquina "liberada"** (botón físico del equipo) | Detiene proceso/jog y husillo |
| `0x04 hi lo` | **Toque de sonda**: `hi,lo` = short big-endian (`hi<<8 | lo&0xFF`) con la posición Z en pasos donde ocurrió el contacto | En auto level: registra la medición y sigue; en jog: detiene el movimiento de Z ("surface touch during moving") |

- El manejo tolera que `0x04 hi` llegue partido del `lo` (guarda el byte pendiente y lo completa
  con la siguiente lectura). Hay que replicar ese reensamblado.
- La detección de máquina (buscar puerto) consiste en: abrir cada puerto a 9600 8N1, mandar un
  paquete `0x3F` con 3 movimientos nulos y esperar ≤ 500 ms; si contesta 1, 2 o 3, es una Wegstr
  (2 y 3 además fijan el estado inicial de sobrecarga/liberada).

### 2.5 Control de flujo
**Stop-and-wait estricto: un paquete en vuelo.** Tras mandar un paquete se espera el ACK (`0x01`,
o el `0x04` que también libera el turno) antes del siguiente. Hay una cola para paquetes
pendientes y un **timeout de 2 s**: si la máquina no contesta, se vacía la cola, se reporta
"la máquina no envió la señal" y el proceso queda pausable/reanudable. Los hilos generadores
(proyecto, jog, auto level) se bloquean en un latch por paquete.

### 2.6 Unidades, pasos y velocidad
- **Resolución: 2500 pasos por 10 mm = 0.004 mm/paso** en los tres ejes. Es calibrable por eje
  entre 2400 y 2600 ("corrección ligera", ajuste `FINE_CORRECTION_X/Y/Z`, unidad "pasos/10 mm").
- Las coordenadas del G-code se convierten a pasos absolutos (redondeo a entero); la PC lleva los
  contadores de posición X/Y/Z en pasos. "Fijar cero" = poner el contador en 0 (no se manda nada
  a la máquina).
- **Feed máximo 170 mm/min** (si el G-code trae F mayor, se recorta a 170).
- Velocidad en pasos/comando: `v = F · (slider%/100) / 12.142857` con tope 14 y piso 0.1.
  A F170 y 100 % → 14 pasos/comando. Los G00 van "a tope": 14 pasos/comando en el eje dominante.
- La interpolación lineal es tipo Bresenham sobre la distancia 3D en pasos, emitiendo deltas
  redondeados por comando; los residuos fraccionarios se acumulan.
- Hay una penalización de frenado en inversiones de sentido por eje (si un delta invierte el
  signo respecto al anterior, se insertan comandos de pausa proporcionales: 1.0 ó 0.5 unidades
  de distancia), para no perder pasos en los cambios de dirección.
- Arcos G02/G03: la PC los discretiza por ángulo (asin(v/r) por iteración) con los mismos límites
  de ±14 pasos; exige I/J (R no se acepta en arcos).

## 3. Jog, ceros, husillo y paro

- **Jog**: mientras la tecla/el botón está presionado se genera un flujo continuo de paquetes
  `0x3F` (¡con detección de toque!). Rampa de aceleración: los pasos/comando suben de 3 en 3
  hasta 10 y luego de 1 en 1 hasta la velocidad objetivo (slider 0–100 % sobre 14). Soporta
  diagonales (varios ejes a la vez) y velocidades sub-paso (acumuladores fraccionarios).
  No hay pasos discretos de jog (0.1/1/10 mm) en Wegstr: es jog continuo por tecla.
- **Cero de trabajo**: botones que ponen en 0 el contador de cada eje (o todos). Todo es
  conteo PC-side; la máquina no guarda nada.
- **Z0 sobre el cobre**: se baja Z con jog; como el jog usa `0x3F`, al cerrar el circuito de la
  sonda (pinza al cobre) la máquina **se detiene sola** y reporta el toque; **Wegstr pone Z=0
  automáticamente** en el contacto (confirmado: en un cambio de broca el usuario bajó hasta tocar
  sin pulsar Z0 y el programa siguió con el nuevo cero). Los pasos de bajada que la PC ya había
  mandado y la máquina descartó no importan: el contador de la PC queda anclado a la superficie.
- **GO ZERO**: G00 a Z de altura segura (ajuste, default 5 mm, activable), luego X0 Y0, luego Z0.
- **Husillo**: toggle; si se activa durante un movimiento solo se marca el bit en los comandos
  siguientes. Hay ajuste "husillo automático" (encenderlo al iniciar un trabajo). La sobrecarga
  (respuesta 2) para todo y requiere reconocimiento.
- **Paro / pausa**: no existe un comando de paro en el protocolo. **Parar = dejar de mandar
  paquetes**; lo más que sigue moviéndose la máquina es el paquete en vuelo (3 comandos ≈ 0.17 mm
  a tope). La pausa guarda los contadores; al reanudar, si la máquina se movió (jog) regresa
  primero a la posición pausada (XY, luego Z). El botón físico del equipo ("released", respuesta
  3) actúa como paro desde la máquina.
- **M0 / M6+M0**: pausa obligatoria con diálogo (cambio de herramienta si la línea anterior fue
  M6); M47 = reiniciar el programa desde la primera línea. Observado: la pausa actúa a nivel de
  paquete, así que el resto de la línea en curso (p. ej. 9 pasos de un `G1 Z0`) se manda al
  reanudar, antes de la línea siguiente; no se reejecuta la línea. Al entrar en pausa y al
  terminar el programa Wegstr manda un paquete `0x3F` nulo (si la herramienta está tocando el
  cobre la máquina contesta "toque", sin consecuencias).

## 4. Calibración de altura (auto level) — el mecanismo exacto

Es lo más valioso. **No es una malla rectangular ciega**: los puntos se eligen sobre las
trayectorias reales de corte.

### 4.1 Elección de puntos
1. Al cargar el G-code se hace una pasada de preproceso que simula todo el recorrido en pasos y
   calcula el bounding box del proyecto.
2. Sobre el bounding box se define una **rejilla (raster) de 1.0 mm** por defecto (ajustable
   0.5–70 mm). Celda de un punto = `((x_pasos − xmin_pasos) / tamañoCelda_pasos ,
   (ymax_pasos − y_pasos) / tamañoCelda_pasos)` (división entera).
3. Durante el preproceso, **cada comando de micro-movimiento que NO pertenece a un G00** (es
   decir, solo movimiento de corte G1/G2/G3) registra su celda; la **primera vez** que el
   recorrido pisa una celda se guarda la posición exacta (en pasos) de ese punto.
   Resultado: un `LinkedHashMap celda → punto`, **en el orden en que el G-code recorre la placa**,
   y solo en celdas por donde de verdad pasa la herramienta (sobre las pistas).
   Con raster de 1 mm, una placa de 50×40 mm da del orden de cientos de puntos — por eso el
   resultado es tan bueno.

### 4.2 Secuencia de sondeo
Se genera internamente esta lista (IDs internos 1001–1003; 1001 marca "comando de sondeo"):

1. `G00 Z(z_actual + 2 mm)` — levantar.
2. `G00 X0 Y0` — ir al origen de trabajo.
3. **Sondeo de referencia en X0 Y0**: `G00 Z(z_actual − 10 mm)` mandado con cabecera `0x3F`.
   La máquina baja hasta el contacto, para y responde `0x04 hi lo` con la Z de contacto en pasos.
   Esa primera lectura es la **referencia z₀**.
4. Para cada punto del mapa (en orden del recorrido): subir Z a la "altura Z segura" del ajuste
   (default 1.0 mm; si el traslado XY es ≥ 10 mm usa mínimo 2.0 mm), `G00 X Y` al punto, sondear
   igual que en (3).
5. Al final: `G00 Z2`, `G00 X0 Y0`.

**Confirmado en hardware (§10), con matices importantes:**

- **Todos los movimientos del auto level van con cabecera `0x3F`**, no solo las bajadas: las
  subidas y los traslados XY también. Hay una razón: el contador de Z que reporta el firmware
  **solo se mantiene coherente mientras se mueve con `0x3F`**; con `0xFF` se descompone (entre
  dos toques con 25 600 pasos arriba y 25 600 abajo en modo `0xFF`, el contador cambió −50 638:
  cada paso de Z lo decrementa sin importar el sentido).
- **El contador de Z del firmware crece al BAJAR** (sentido opuesto al contador de la PC) y
  **no es absoluto**: en dos sesiones el mismo cobre dio 1909 y 255. Solo sirven las diferencias
  entre toques consecutivos dentro de una secuencia `0x3F`.
- La subida entre puntos es **relativa a la posición del toque** (250 pasos = 1 mm, o 500 si
  el traslado es ≥ 10 mm), no a una Z absoluta. Wegstr **no resincroniza** su contador de Z con
  el valor reportado; como la máquina descarta los pasos de bajada que llegan después del toque
  (la PC ya había mandado 76–90 pasos de más, ≈ 2 paquetes), el contador de la PC queda anclado
  a la superficie y el error se absorbe solo.
- Traslados XY a 14 pasos/comando; bajada con rampa 10, 11, 12, 13, 14. Unos 1.15 s por punto
  (299 puntos en 6 minutos).
- Si la máquina deja de contestar (timeout de 2 s), Wegstr desconecta y conserva el estado: tras
  reconectar el USB y volver a "online" **continuó la calibración exactamente donde se quedó**.

Por cada toque se guarda `diff(celda) = z₀ − z_medida` **en pasos** (short) y se emite el evento
de diferencia para mostrarla en la UI. Si el conteo de toques no coincide con los sondeos
esperados, auto level falla ("no se detectó ningún toque o el husillo estaba demasiado alto").
El resultado queda en memoria hasta "reset"; la UI tiene un toggle **"aplicar"**.

### 4.3 Aplicación al G-code (en tiempo real, no modifica el archivo)
Durante la ejecución, con auto level hecho y "aplicar" activo:

- **Movimientos XY de corte**: por cada comando se calcula la celda actual; si
  `diff(celda) ≠ offset_aplicado`, el ΔZ del comando se ajusta en **±1 paso** (0.004 mm) hasta
  alcanzarla. Es decir, **no hay interpolación bilineal: se usa la diff de la celda y se converge
  gradualmente** (rampa de 1 paso por comando ≈ 1 paso cada 0.056 mm de avance), lo que suaviza
  las transiciones entre celdas.
- **Bajadas puras de Z (plunge)**: la diff de la celda se suma completa al objetivo del plunge.
- **Backlash de Z**: ajuste "reacción del eje Z" (default **4 pasos** = 0.016 mm, 0–30): cada vez
  que el sentido del ajuste de Z se invierte (sube↔baja) se añaden/quitan esos pasos extra para
  compensar el juego del husillo de Z. El estado sube/baja se rastrea durante todo el trabajo,
  incluidos los retornos de pausa.
- **Los G00 en XY también se corrigen** con la misma rampa de ±1 paso por comando (confirmado en
  hardware: en el `G0 X10 Y10` previo a un cambio de broca, a Z = 15 mm, la Z fue variando −6,
  −4, +6, −11, −228 pasos a lo largo del traslado, y al final del trabajo el `G0 X0 Y0` también).
  La versión anterior de este documento decía lo contrario.
- El mapa vive en memoria por proyecto; cambiar el raster exige recargar el proyecto y re-sondear.

> Signo (resuelto en hardware, §10): el contador que reporta el firmware **crece al bajar**.
> Con `diff = z₀ − z_medida`, una superficie más alta que la referencia da una lectura menor y
> por tanto `diff > 0` = subir la herramienta, que es justo lo que hace Wegstr.

## 5. G-code soportado por Wegstr

- Parser **modal** (recuerda G y X/Y/Z/I/J/R previos), por línea, ignora comentarios `( )`,
  espacios, y `G17`; `N` opcional. Decimales con punto o coma.
- Movimientos: G0/G00, G1/G01, G2/G02, G3/G03 (arcos **requieren I/J**; plano XY solamente,
  G18/G19 → error).
- Ciclos de taladrado G73/G81/G82/G83: se expanden a `G00 XY → G00 Z(R) → G01 Z(fondo) F →
  G00 Z(retorno)`.
- M3/M4 = husillo on, M5 = off, M0 = pausa obligatoria (M6 previo → diálogo de cambio de
  herramienta), M47 = volver a empezar. S y T se ignoran. F se recorta a 170.
- G21/G90/G94/G40/G49/G80/G71/G91.1 etc. se ignoran sin error (el sample oficial los trae).
  **Todo se asume milímetros y coordenadas absolutas** (no hay soporte G91).
- **G4 se ignora** (confirmado: entre el fin de `G0 Z15` y el arranque hacia el primer barreno,
  con `M3 S10000 / G4 P3` en medio, pasaron 0 ms). **M3/M5 no mandan paquete propio**: el bit
  del husillo simplemente viaja en el siguiente movimiento, sin tiempo de arranque.
- Los comentarios con paréntesis anidados `( a (b) )` no dan error.

## 6. Ajustes persistentes de Wegstr (Java Preferences)

| Clave | Default | Rango | Qué es |
|---|---|---|---|
| COMPUTER_PERFORMANCE | 4 | 2–20 | Se manda al firmware en el paquete `0x7F` (2 = PC rápida) |
| FINE_CORRECTION_X/Y/Z | 2500 | 2400–2600 | Pasos por 10 mm por eje |
| GO_ZERO_SAFE_HEIGHT (+_STATE) | 5.0 mm / off | 0–40 | Altura segura antes de ir al cero |
| AUTO_SPINDLE_STATE | off | — | Encender husillo al iniciar trabajo |
| AUTO_LEVEL_RASTER | 1.0 mm | 0.5–70 | Tamaño de celda de la rejilla |
| AUTO_LEVEL_SAFE_Z | 1.0 mm | 0.1–40 | Altura entre sondeos |
| AUTO_LEVEL_Z_BACKLASH | 4 pasos | 0–30 | Compensación de juego de Z (pasos de 0.004 mm) |

## 7. Requisitos de compatibilidad para nuestro software

Lo que hay que implementar igual para que la máquina funcione:

1. Serial 9600 8N1, DTR/RTS on, sin flow control HW.
2. Paquetes `0xFF`/`0x3F` de 7 bytes con la codificación de bits de §2.3, y `0x7F` de ajuste.
3. Stop-and-wait con ACK, reensamblado del `0x04 hi lo`, timeout ~2 s.
4. Planeador PC-side: G-code → pasos (0.004 mm), deltas ±14, Bresenham 3D, rampas de jog,
   frenado en inversiones, discretización de arcos, clamp F170.
5. Manejo de respuestas 2 (sobrecarga) y 3 (liberada) parando el flujo.
6. Sondeo con `0x3F` + lectura de posición de toque (jog-Z0 y auto level).
7. Auto level: rejilla sobre trayectorias de corte, referencia en X0Y0, diffs en pasos,
   aplicación con rampa ±1 paso + backlash Z.
8. Detección de máquina con paquete nulo `0x3F`.

Lo que Wegstr NO tiene (y nosotros sí tendremos): generación de trayectorias desde
Gerber/Excellon, jog por pasos discretos, dos caras con pines de calibración, repaso por zona,
persistencia de sesión/mapa de alturas, consola, simulador.

## 8. Incertidumbres a verificar con la máquina

1. ~~**Signo del conteo Z del firmware**~~ **Resuelto (§10)**: crece al bajar; no es absoluto y
   solo es coherente con movimiento `0x3F`. Queda pendiente la prueba fina con placa inclinada
   usando nuestro software, para confirmar el signo de las diffs aplicadas.
2. ~~**Cadencia exacta del firmware**~~ **Resuelto**: 59 ms por paquete (50.8 comandos/s) con
   rendimiento 4. El efecto de otros valores de `0x7F` sigue sin medirse.
3. ~~ACK al paquete `0x7F`~~ **Resuelto**: sí, en ≈ 5 ms.
4. Comportamiento del toque de sonda con cabecera `0xFF` (se asume que lo ignora). Sin datos
   directos; el `0x3F` nulo que Wegstr manda al pausar sí recibió "toque" con la broca en el cobre.
5. El sample oficial usa Y negativas; confirmar que no hay límites de software (todo indica que
   no: la máquina solo cuenta pasos).
6. **Nuevo**: la máquina (o el puente CH340) puede colgarse a mitad de una bajada de sondeo
   (ocurrió una vez en 464 toques); dos intentos de detección quedaron sin respuesta hasta
   reconectar el cable USB. Nuestro software debe tolerar reconexión y continuar.

## 9. Archivos útiles de la carpeta Wegstr

- `Utils\Samples (g-code)\PCB_test.txt` — ejemplo del dialecto G-code esperado.
- `Utils\Postprocessors\WEGSTR_Fusion360.txt` — referencia del postprocesador oficial.
- `Driver\Windows\CH341SER.EXE` — driver del puerto (ya instalado si la máquina ya se usó).

## 10. Confirmación en hardware (captura del tráfico real, 2026-10-21)

Se capturó el tráfico serial entre Wegstr v3.2.0 y la máquina mientras se fresaba una
baquelita real (aislamiento de una cara, ~1.3 m de corte, y 21 barrenos con cambio de broca):
jog, Z0 al toque, dos calibraciones de altura (una interrumpida, otra con 299 puntos), el
trabajo de aislamiento (solo su final) y el de barrenos completo, incluida la pausa M0 con
cambio de broca y la reanudación. Método: enganche de `WriteFile`/`ReadFile`/`GetOverlappedResult`
dentro del proceso de Wegstr (Frida), sin drivers ni puertos virtuales. Nunca apareció un byte
fuera del protocolo descrito. Los registros crudos, los CSV decodificados y los scripts están en
`Documents\Wegstr_capturas_2026-10-21` de la PC de pruebas (no en el repo).

Resumen de lo confirmado y de lo que cambió respecto a las secciones anteriores:

| Tema | Resultado |
|---|---|
| Paquetes, ACK, stop-and-wait, keep-alive `7F 04` cada 1 s con ACK | Igual que §2 |
| Cadencia | 59 ms/paquete, 50.8 cmd/s; el siguiente paquete sale 0.1 ms tras el ACK |
| Velocidades | F30 → 3 pasos/cmd, F100 → 8–9, F150 → 12–13, G0 → 14 (fórmula de §2.6) |
| Auto level | Todo con `0x3F`; subida relativa 1 mm (2 mm si traslado ≥ 10 mm); rampa de bajada 10..14; 1.15 s/punto; sin resincronizar el contador; 76–90 pasos de bajada descartados tras cada toque |
| Contador Z del firmware | Crece al bajar; no absoluto; coherente solo con `0x3F` |
| Aplicación del mapa | También en G00 XY (±1 paso/cmd), no solo en cortes |
| Jog con toque | Fija Z0 automáticamente |
| G4 | Se ignora; M3/M5 no mandan paquete propio |
| M0 | Pausa a nivel de paquete; el resto de la línea se vacía al reanudar; `0x3F` nulo al pausar y al terminar |
| Pérdida de señal | Wegstr conserva el estado y continúa tras reconectar (calibración incluida) |
| Placa real (baquelita 1.6 mm) | 0.9 mm de desnivel entre pistas, 1.6 mm contando el origen: por eso el auto level es imprescindible |
