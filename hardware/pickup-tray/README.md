# Brick pickup trays

3D-printed trays that hold the 10 bricks (50 × 50 × 100 mm, lying flat) in exactly the same place every time, so the robot always knows where to pick them up.

![Top view](preview_top.png)

## Files
| File | What it is |
| --- | --- |
| `tray_A_bricks_1-6.stl` | Tray A, 6 pockets (bricks 1–6), 225 × 246 × 16 mm, **print this** |
| `tray_B_bricks_7-10.stl` | Tray B, 4 pockets (bricks 7–10), 146 × 246 × 16 mm, **print this** |
| `pickup_trays.3dm` | Rhino model of both trays |
| `make_tray.py` | Script that generates everything. Change the numbers at the top, then in Rhino 8 run `RunPythonScript` on it |
| `make_tray_log.txt` | Output of the last run: sizes, volumes, **pocket and cross coordinates** |
| `preview_*.png` | Pictures of the model |

## Design
- **Pocket** 51 × 101 mm (0.5 mm play per side around the brick), 8 mm straight wall + **6 mm 45° chamfer** on top. Drop a brick in roughly and it slides into place.
- Bricks are only held at the **corners**; there are **open slots in the middle of every side** (60 mm on the long sides, 25 mm on the short sides) for the soft gripper's fingers, whichever way it closes.
- Small round **reliefs in the pocket corners**, so brick corners sit fully down.
- **Numbers 1–10** engraved in the pocket floors = brick IDs the app sends to the robot.
- **3 crosshair marks** on Tray A (top surface): shared reference points. Teach them to the robot once (jog the tool tip onto each cross) and touch the same crosses for AR calibration.
- Two trays because 10 pockets don't fit on the Bambu A1 bed (256 × 256 mm).

## Printing (Bambu A1, PLA)
- Layer height 0.2 mm, **3 walls**, 15 % infill (gyroid), no supports needed (all slopes are 45°). Flat side down.
- About 270 cm³ (Tray A) and 170 cm³ (Tray B) of solid volume. With 15 % infill they use far less filament; check the estimate in Bambu Studio.
- **Print a test first:** in Bambu Studio cut Tray B down to one pocket (or print it at full size) and check the fit with a real brick. Too tight → set `CLEARANCE = 1.0` in `make_tray.py` and regenerate.

## Fixing to the table
- Double-sided tape (strong mounting tape) under each tray.
- Line both trays up against a straight edge (table edge or a ruler taped down) so they go back in the same place if they come loose.
- If a tray moves, the robot positions must be taught again.

## Coordinates (from `make_tray_log.txt`, mm from each tray's front-left corner)
| Brick | Tray | x | y |
| --- | --- | --- | --- |
| 1 | A | 33.5 | 58.5 |
| 2 | A | 112.5 | 58.5 |
| 3 | A | 191.5 | 58.5 |
| 4 | A | 33.5 | 187.5 |
| 5 | A | 112.5 | 187.5 |
| 6 | A | 191.5 | 187.5 |
| 7 | B | 33.5 | 58.5 |
| 8 | B | 112.5 | 58.5 |
| 9 | B | 33.5 | 187.5 |
| 10 | B | 112.5 | 187.5 |

Calibration crosses on Tray A (top surface, 16 mm high): (73, 20), (73, 123), (152, 123).
Pocket spacing is exactly 79 mm (x) and 129 mm (y), so once the robot knows one pocket of a tray, it can compute the others.

## Still unknown
- Gripper finger size → the slot widths may need adjusting once Matthias knows the gripper.
- Real brick tolerances → measure a few bricks with calipers before printing all of it.
