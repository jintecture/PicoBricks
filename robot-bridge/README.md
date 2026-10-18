# robot-bridge: headset → RoboDK → UR5

```
Pico (WallBuilder + BrickSender.cs) ──Wi-Fi, UDP 5005──▶ laptop: picobricks_bridge.py ──▶ RoboDK (simulation) ──▶ UR5 192.168.1.198 (Ethernet)
                                                                                 └─ fallback: URScript straight to port 30002
```

- **`Assets/Scripts/BrickSender.cs`** (in the Unity project) adds itself when the app starts, so there's no scene edit. A few frames after **A** (= WallBuilder's OK), it sends every brick that left its tray pocket: **pick** = its pocket, **place** = its spot in the wall, both in the table frame (origin = middle of the front edge, x right, y away, z up, m; yaw in degrees, counter-clockwise, 0 = long side pointing away). Press A on a built wall to send it again. In Play mode without a headset: press **S**.
- **`picobricks_bridge.py`** turns the table frame into the robot frame using `calib.json` (the robot's coordinates of the table's front-left and front-right corners, the same corners the headset calibrates on). It rounds heights to whole brick layers, skips standing bricks and out-of-reach targets, asks before moving, then runs pick → place in RoboDK (bricks shown as boxes that ride with the gripper) or on the real UR5.
- **`PICOBRICKS.bat`**: double-click menu. 1–4 work without the robot; 5–8 at the robot.

## Simulation (no robot)
1. RoboDK: File → Open online library → **UR5** (not UR5e) → Open. Leave RoboDK open.
2. Double-click `PICOBRICKS.bat` → **1**: six bricks go from the tray into a curved 3 × 2 wall.
3. Headset: laptop **Mobile hotspot** on → Pico on that Wi-Fi → menu **2** (print only; allow Python through the firewall, Private + Public) → in the headset calibrate, draw a wall, A → the terminal prints the bricks. Then menu **3** to see it in RoboDK.

The placeholder `calib.example.json` puts the table 75 cm in front of the robot. The layout's shape is right, but not its place relative to the real robot.

## At the robot
1. Laptop Ethernet static `192.168.1.100`, `ping 192.168.1.198`. Set the **TCP to the gripper fingertips** on the pendant.
2. Menu **5**: jog the TCP onto the table's front-left and front-right corners, type X Y Z (Move tab, Feature = Base, mm).
3. Menu **6**: the robot hovers 30 mm above each corner. If it's not above the corner, stop: the frame is wrong.
4. The real tray goes at the table's **front-left corner**, exactly like the virtual one (`TableCalibration.trayOffset` if it's taped further in).
5. Calibrate the headset on the same table, build a wall, A → menu **3** (simulation) → menu **7** (real, speed slider 20–30 %, e-stop in hand). RoboDK won't connect (licence)? Menu **8**.

## Settings (top of `picobricks_bridge.py`)
`GRIP_DO` / `GRIP_CLOSE_VALUE` (which output closes the gripper), `GRASP_DEPTH` (how far below the brick top the TCP goes), `TOOL_YAW_OFFSET` (90 if the fingers close along the brick), `TOOL_LENGTH` (RoboDK tool if the station has none), `TRAVEL_Z`, `APPROACH`, `SPEED`.
