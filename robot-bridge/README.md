# robot-bridge: headset → RoboDK → UR5

```
Pico (WallBuilder + BrickSender.cs) ──Wi-Fi, UDP 5005──▶ laptop: picobricks_bridge.py ──▶ RoboDK (simulation) ──▶ UR5 192.168.1.198 (Ethernet)
                                                                                 └─ fallback: URScript straight to port 30002
```

- **`Assets/Scripts/BrickSender.cs`** (in the Unity project) adds itself when the app starts, so there's no scene edit. A few frames after **A** (= WallBuilder's OK), it sends every brick that left its tray pocket: **pick** = its pocket, **place** = its spot in the wall, both in the table frame (origin = middle of the front edge, x right, y away, z up, m; yaw in degrees, counter-clockwise, 0 = long side pointing away). Press A on a built wall to send it again. In Play mode without a headset: press **S**.
- **`picobricks_bridge.py`** turns the table frame into the robot frame using `calib.json` (the robot's coordinates of the table's front-left and front-right corners, the same corners the headset calibrates on). It rounds heights to whole brick layers, skips standing bricks and out-of-reach targets, asks before moving, then runs pick → place in RoboDK (bricks shown as boxes that ride with the gripper) or on the real UR5.
- **`PICOBRICKS.bat`**: double-click menu. 1–4 work without the robot; 5–8 at the robot.
- **Ghost robot preview (19 Oct):** after every wall the bridge works out the robot's joint angles itself (**`ur5_kinematics.py`**: standard UR5 maths, plain Python, no RoboDK) and sends them back to the headset on **UDP 5006**. There the see-through UR5 (`GhostPlayer.cs`) puts the bricks back in the tray and builds the wall the way the robot will. In simulation the robot stands where the headset's ghost stands (the headset sends its position); the real robot uses `calib.json`. The bridge also checks the ghost model against the maths ("ghost robot matches the UR5 maths").
- **RoboDK licence:** the trial on Jin's laptop expired on 19 Oct; the free licence refuses most API calls, so menu 1 and 4 (RoboDK simulation) need a licence. Menu **3** (ghost robot in the headset) doesn't use RoboDK.

## Simulation (no robot)
1. RoboDK: File → Open online library → **UR5** (not UR5e) → Open. Leave RoboDK open.
2. Double-click `PICOBRICKS.bat` → **1**: six bricks go from the tray into a curved 3 × 2 wall.
3. Headset: laptop **Mobile hotspot** on (or both on the same home Wi-Fi) → Pico on that Wi-Fi → menu **2** (print only; allow Python through the firewall, Private + Public) → in the headset calibrate, draw a wall, A → the terminal prints the bricks. Then menu **3**: the ghost robot in the headset builds the wall.

The placeholder `calib.example.json` puts the table 75 cm in front of the robot. The layout's shape is right, but not its place relative to the real robot.

## At the robot (first real test, checklist)
Have someone who knows the robot and its **emergency stop** there for every real move. Speed slider on the pendant **10–20 %**.

**0. Measure (tape measure):**
- the gripper, from the flange (round end plate) to the fingertips → `TOOL_LENGTH` at the top of `picobricks_bridge.py` (now 150 mm);
- where the robot stands: its base centre is **25 cm from the table's right end** and **30 cm from the front edge** (measured 21 Oct; table 1348 × 600 mm). If it moves, change `RobotFromRightEnd` / `RobotFromFrontEdge` in `BrickSceneSetup.cs`.

**1. Tray:** tape it in the back corner beside the robot, as in the headset: **440 mm from the table's right end** to the tray's right edge and **350 mm from the front edge** to the tray's front edge (flush against the back edge, 19 cm left of the robot centre). Pocket 1 at the front-left.

**2. Network:**
- Laptop ↔ robot: Ethernet cable. Robot IP: pendant → *Setup Robot → Network* (the LAMA UR5 is **192.168.1.3** since 28 Oct (was .2), shown on the pendant's About screen; another one? add `--ip x.x.x.x`). Laptop: Windows network settings → Ethernet → IP manually `192.168.1.100`, mask `255.255.255.0`.
- Headset ↔ laptop: laptop **Mobile hotspot** on, headset joins it (eduroam blocks device-to-device traffic).

**3. Menu `c` (robot check, nothing moves):** shows the robot's joints and position. Run it twice in two different poses: the "distance flange ↔ robot's TCP" must stay the same. Then our UR5 maths matches the robot.

**4. Gripper:** easiest on the pendant's *I/O* tab: switch outputs on and off until the gripper moves; note the number and whether on = closed. Put it in `GRIP_DO` / `GRIP_CLOSE_VALUE` (`GRIP_ON_TOOL = True` if it's a "tool" output). Then menu **7** closes and opens it from the laptop.

**5. Menu 5 (calibrate):** jog the gripper (pointing straight down) onto the table top at (1) the **front-right corner** and (2) a point on the **front edge 40–60 cm further left**; press Enter at each, the laptop reads the position from the robot. Same two points as the headset's calibration. (The robot can't reach the far-left corner of the 1.43 m table.)

**6. Menu 6 (hover):** the robot goes 3 cm above both points. Not above them? Stop, redo 5.

**7. Headset:** calibrate with **B** on the same two points. The ghost robot should sit on the real robot.

**8. Rehearsal, menu 3:** draw a wall, A, watch the ghost build it. Nothing real moves.

**9. Menu 8 (one brick):** draw, A, watch the ghost, then answer **y** in the black window: the robot moves **one** brick with exactly the ghost's joint angles. The window also prints "real robot vs ghost: … OK" (or a warning if the headset and robot calibrations disagree).

**10. Menu 9 (full wall)** when one brick worked.

## Settings (top of `picobricks_bridge.py`)
`GRIP_DO` / `GRIP_CLOSE_VALUE` (which output closes the gripper), `GRASP_DEPTH` (how far below the brick top the TCP goes), `TOOL_YAW_OFFSET` (90 if the fingers close along the brick), `TOOL_LENGTH` (RoboDK tool if the station has none), `TRAVEL_Z`, `APPROACH`, `SPEED`.
