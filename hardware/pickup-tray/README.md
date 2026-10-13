# Brick pickup tray

A minimal 3D-printed tray that puts the **6 bricks** (50 × 50 × 100 mm, lying flat) in exactly the same place every time, so the robot always knows where to pick them up.

![Top view](preview_top.png)

## Files
| File | What it is |
| --- | --- |
| `pickup_tray_bricks_1-6.stl` | **Print this.** 229 × 250 × 12 mm, fits the Bambu A1 bed (256 × 256 mm) |
| `pickup_tray_bricks_1-6.3dm` | Rhino model |
| `make_tray.py` | Script that generates everything. Change the numbers at the top, then in Rhino 8 run `RunPythonScript` on it |
| `make_tray_log.txt` | Output of the last run: size, volume, **pocket and cross coordinates** |
| `preview_*.png` | Pictures of the model |

## Design (minimal, low filament)
- A **thin 1.6 mm frame** with **open windows**: the bricks stand **directly on the table**, not on plastic.
- **L-shaped corner pieces** (4 mm thick, 12 mm high) hold each brick at its 4 corners. A **3 mm 45° lead-in** on the inside top edge funnels the brick into place.
- The middle of every side is **open** (21 mm on the short sides, 61 mm on the long sides), so the soft gripper's fingers can reach in from any direction.
- Pocket = brick + **0.5 mm play per side** (51 × 101 mm). Small round **reliefs in the inside corners** so brick corners don't catch.
- **Numbers 1–6** engraved in the frame in front of each pocket = brick IDs the app sends to the robot.
- **3 crosshair marks** on the frame: shared reference points. Teach them to the robot once (jog the tool tip onto each cross) and touch the same crosses for the AR calibration.
- About **76 cm³** of material if printed solid (~94 g PLA); much less with normal infill.

## Printing (Bambu A1, PLA)
- Layer height 0.2 mm, 3 walls, 15 % infill, **no supports** (all slopes are 45°). Flat side down.
- **Check the fit first:** measure 2–3 real bricks with calipers. If they're bigger than 50 × 50 × 100 mm or vary by more than ~1 mm, set `CLEARANCE = 1.0` in `make_tray.py` and regenerate.

## Fixing to the table
- Double-sided (mounting) tape under the frame.
- Line the tray up against a straight edge (table edge or a ruler taped down) so it goes back in the same place if it comes loose.
- If the tray moves, the robot positions must be taught again.

## Coordinates (from `make_tray_log.txt`, mm from the tray's front-left corner)
| Brick | x | y |
| --- | --- | --- |
| 1 | 35.5 | 60.5 |
| 2 | 114.5 | 60.5 |
| 3 | 193.5 | 60.5 |
| 4 | 35.5 | 189.5 |
| 5 | 114.5 | 189.5 |
| 6 | 193.5 | 189.5 |

Calibration crosses (top of frame, 1.6 mm high): (75, 125), (154, 125), (75, 245).
Pocket spacing is exactly **79 mm** (x) and **129 mm** (y), so once the robot knows one pocket it can compute the others.
Bricks 1–3 are the front row (y = 0 side of the tray).

## Still unknown
- Gripper finger size → the open sides may need to be wider once Matthias knows the gripper.
- Real brick tolerances → measure before printing.
