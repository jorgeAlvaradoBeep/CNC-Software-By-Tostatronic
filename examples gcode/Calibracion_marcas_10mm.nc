( Tostatronic - Marcas de calibracion de 10 mm )
( Z0 = superficie del cobre: fija Z0 al toque ANTES de correr esto. X0 Y0 = donde este el cabezal )
( Hace 3 rayas verticales en X = 0, 10 y 20 mm, y 3 horizontales en Y = 0, 10 y 20 mm )
( Mide con vernier entre bordes iguales de dos rayas: debe dar 10.00 y 20.00 mm )
( V-bit, 0.06 mm de profundidad, 10000 rpm. Area usada: X -4..20, Y -4..20 )
G21 G90 G17 G94
G0 Z5.000
M3 S10000
G4 P3
G0 Z2.000
( rayas verticales: para medir en X )
G0 X0.000 Y-4.000
G1 Z-0.060 F50
G1 Y-2.000 F100
G0 Z2.000
G0 X10.000 Y-4.000
G1 Z-0.060 F50
G1 Y-2.000 F100
G0 Z2.000
G0 X20.000 Y-4.000
G1 Z-0.060 F50
G1 Y-2.000 F100
G0 Z2.000
( rayas horizontales: para medir en Y )
G0 X-4.000 Y0.000
G1 Z-0.060 F50
G1 X-2.000 F100
G0 Z2.000
G0 X-4.000 Y10.000
G1 Z-0.060 F50
G1 X-2.000 F100
G0 Z2.000
G0 X-4.000 Y20.000
G1 Z-0.060 F50
G1 X-2.000 F100
G0 Z5.000
M5
G0 X0.000 Y0.000
M2
