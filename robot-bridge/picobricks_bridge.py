r"""
PicoBricks -> UR5 bridge  (runs on the laptop)
==============================================
Pico headset (BrickSender.cs, A) --UDP 5005--> this script --> RoboDK (simulation or the real UR5)
                                                           \-> or plain URScript to the UR5 :30002

Coordinates: the headset sends poses in the TABLE frame (origin = middle of the table's front edge,
on the top surface; x right, y away from the user, z up; metres; yaw in degrees, CCW from above,
0 = brick's long side pointing away from the user). calib.json holds the robot's coordinates of the
SAME two front corners the headset calibrates on, which links the two systems.

Easiest: double-click PICOBRICKS.bat and pick a number. Or by hand:
  python picobricks_bridge.py calib                         # type the 2 corners from the pendant -> calib.json
  python picobricks_bridge.py run --file sample_layout.json # RoboDK simulation, no headset
  python picobricks_bridge.py run                           # listen for the headset, RoboDK simulation
  python picobricks_bridge.py corners --real                # robot hovers over both corners (check calib!)
  python picobricks_bridge.py run --real                    # RoboDK drives the real UR5
  python picobricks_bridge.py run --backend urscript --real # no RoboDK: URScript straight to the UR5
  python picobricks_bridge.py run --backend print           # only print the robot targets
  python picobricks_bridge.py run --backend ghost           # headset's ghost robot acts it out, nothing else moves

After every wall the bridge also sends the robot's movements (joint angles from ur5_kinematics.py) back to the
headset on UDP 5006, where the ghost UR5 plays them (GhostPlayer.cs). In simulation the robot is placed where the
headset's ghost robot stands; the real robot uses calib.json.

Needs:  python -m pip install robodk     (only for --backend robodk, the default)
"""
import argparse
import json
import math
import os
import socket
import struct
import sys
import time

import ur5_kinematics as kin                 # own UR5 joint maths (robot-bridge/ur5_kinematics.py)

HERE = os.path.dirname(os.path.abspath(__file__))
CALIB_FILE = os.path.join(HERE, "calib.json")

# ======================= settings you may want to change =======================
ROBOT_IP = "192.168.1.198"   # check on the pendant: Setup Robot > Network (or use --ip)
ROBOT_STATE_PORT = 30003     # the robot streams its joints and position here (read only)
LAMA_WIDTH = 1348.0          # mm, the real LAMA table's front edge (tape, 21 Oct); = RealTableLength in BrickSceneSetup.cs
UDP_PORT = 5005
PREVIEW_PORT = 5006     # the headset listens here for the robot movements (ghost UR5 preview, GhostPlayer.cs)
PREVIEW_STEPS = 4       # straight (MoveL) moves are sent as this many in-between poses

# Gripper (soft/parallel gripper picking the brick from the top)
GRIP_DO = 0             # digital output that closes the gripper (find it with menu 7, the gripper test)
GRIP_ON_TOOL = False    # True if the gripper is wired to the TOOL outputs at the flange, not the control box
GRIP_CLOSE_VALUE = 1    # value that CLOSES it (flip to 0 if yours is the other way round)
GRIP_WAIT_S = 0.8       # time for the gripper to close / open
GRASP_DEPTH = 25.0      # mm: how far below the brick's top face the TCP goes (0 = suction on top)
TOOL_YAW_OFFSET = 0.0   # deg: add 90 if the fingers close along the brick's length instead of across it
TOOL_LENGTH = 150.0     # mm from the flange (round end plate) to the fingertips: MEASURE IT with a tape at the robot

BRICK_W, BRICK_L, BRICK_H = 50.0, 100.0, 50.0   # mm
APPROACH = 80.0         # mm: go straight down/up this last bit with MoveL
TRAVEL_Z = 250.0        # mm above the table top for moves between pick and place
PLACE_CLEARANCE = 2.0   # mm: release this far above the exact place height
SPEED = 100.0           # mm/s linear (first tests: slow)
JOINT_SPEED = 30.0      # deg/s (the pendant's speed slider slows this down further)
JOINT_ACCEL = 60.0      # deg/s^2
HOME_JOINTS = [0, -90, -90, -90, 90, 0]   # simulation only: a tool-down "ready" pose
UR5_REACH = 920.0       # mm: refuse targets further than this from the base (900 at table height, gripper down)
MAX_BRICKS = 20
# ===============================================================================


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


# ---------------- table frame <-> robot base frame ----------------
class TableFrame:
    """Built from the robot TCP touching the table's front-left and front-right corners (mm, robot base)."""

    def __init__(self, fl, fr):
        dx, dy = fr[0] - fl[0], fr[1] - fl[1]
        width = math.hypot(dx, dy)
        if width < 100:
            sys.exit("calib.json: front corners are only %.0f mm apart - wrong numbers?" % width)
        self.ux = (dx / width, dy / width)          # table x (right) in robot XY
        self.uy = (-self.ux[1], self.ux[0])         # table y = z cross x (away from the user)
        self.origin = ((fl[0] + fr[0]) / 2, (fl[1] + fr[1]) / 2, (fl[2] + fr[2]) / 2)
        self.yaw0 = math.degrees(math.atan2(self.ux[1], self.ux[0]))
        self.width = width
        if abs(fl[2] - fr[2]) > 5:
            log("!! corner heights differ by %.1f mm - table not level or bad touch" % abs(fl[2] - fr[2]))

    def point(self, x_mm, y_mm, z_mm):
        return (self.origin[0] + self.ux[0] * x_mm + self.uy[0] * y_mm,
                self.origin[1] + self.ux[1] * x_mm + self.uy[1] * y_mm,
                self.origin[2] + z_mm)

    def to_robot(self, p):
        """table pose (m, deg) -> robot (x, y, z mm, yaw deg) of the brick centre."""
        x, y, z = self.point(p["x"] * 1000, p["y"] * 1000, p["z"] * 1000)
        return x, y, z, self.yaw0 + p["yaw"]


GHOST_CHECK_POSES = [[0, -90, -90, -90, 90, 0], [30, -60, -100, -60, 90, 0], [-45, -120, 60, -30, -90, 45]]  # = GhostRobot.CheckPoses


def ghost_frame(robot, kin_yaw0, kin_yaw90):
    """Simulation only: a table frame that puts the simulated robot exactly where the headset's ghost UR5 stands.

    robot = the headset's `robot` message: ghost base (x, y, z m, table frame) and the direction the ghost's arm
    points with all joints 0 (armYaw0) and with the base turned to 90 deg (armYaw90), degrees CCW from table x.
    kin_yaw0 / kin_yaw90 = the same directions from the UR5 maths, in the robot's own base frame. Their difference
    is how the robot is turned on the table. Then the movements worked out here are the ones the ghost can play."""
    psi = math.radians(robot["armYaw0"] - kin_yaw0)          # robot base x axis, seen in the table frame
    # check that the ghost's base joint turns the same way as the maths
    turn_ghost = (robot["armYaw90"] - robot["armYaw0"]) % 360
    turn_kin = (kin_yaw90 - kin_yaw0) % 360
    if abs(turn_ghost - turn_kin) > 10:
        log("!! ghost and UR5 maths turn the base joint differently (%.0f vs %.0f deg) - the preview will be wrong"
            % (turn_ghost, turn_kin))
    check_ghost(robot, psi)
    bx, by, bz = robot["x"] * 1000, robot["y"] * 1000, robot["z"] * 1000
    c, s = math.cos(-psi), math.sin(-psi)

    def to_robot(tx, ty):                                    # table mm -> robot base mm
        dx, dy = tx - bx, ty - by
        return [c * dx - s * dy, s * dx + c * dy, -bz]
    log("simulation: robot placed like the headset's ghost (base %.0f, %.0f mm on the table, turned %.0f deg)"
        % (bx, by, math.degrees(psi)))
    return TableFrame(to_robot(-500, 0), to_robot(500, 0))


def check_ghost(robot, psi):
    """Compare the ghost's hand position (sent by the headset) with the UR5 maths for a few test poses.
    The two measure slightly different points of the hand (a few cm); a joint turning the wrong way = tens of cm."""
    check = robot.get("check") or []
    if len(check) < 3 * len(GHOST_CHECK_POSES):
        return
    c, s = math.cos(psi), math.sin(psi)
    worst = 0.0
    for i, q in enumerate(GHOST_CHECK_POSES):
        T = kin.forward(q)
        x, y, z = T[0][3], T[1][3], T[2][3]
        mx, my = c * x - s * y, s * x + c * y              # robot base -> table axes
        gx, gy, gz = (v * 1000 for v in check[3 * i: 3 * i + 3])
        worst = max(worst, math.sqrt((mx - gx) ** 2 + (my - gy) ** 2 + (z - gz) ** 2))
    if worst > 150:
        log("!! ghost robot and UR5 maths disagree by up to %.0f mm - a joint of the ghost turns the wrong way" % worst)
    else:
        log("ghost robot matches the UR5 maths (within %.0f mm)" % worst)


def load_calib():
    if not os.path.exists(CALIB_FILE):
        sys.exit("No calib.json yet. Run:  python picobricks_bridge.py calib")
    with open(CALIB_FILE) as f:
        c = json.load(f)
    if "front_edge_mm" in c:
        # Same as the headset's LAMA calibration: the front-right corner + any point further left on the front edge.
        # The robot can't reach the far-left corner of the 1.43 m LAMA table, so it is worked out from the LAMA width.
        fr, e = c["front_right_mm"], c["front_edge_mm"]
        dx, dy = fr[0] - e[0], fr[1] - e[1]
        n = math.hypot(dx, dy)
        if n < 100:
            sys.exit("calib.json: the two points are only %.0f mm apart - redo menu 5" % n)
        c["front_left_mm"] = [fr[0] - dx / n * LAMA_WIDTH, fr[1] - dy / n * LAMA_WIDTH, (fr[2] + e[2]) / 2]
    tf = TableFrame(c["front_left_mm"], c["front_right_mm"])
    # The headset (LAMA mode) places the table from its FRONT-RIGHT corner with the LAMA table's width, so the
    # table frame's origin (middle of the front edge) must be found the same way here, or headset and robot differ
    # by half the width difference.
    if "front_edge_mm" not in c and abs(tf.width - LAMA_WIDTH) > 5:
        log("note: measured front edge %.0f mm, LAMA table %.0f mm: using the front-right corner as the anchor,"
            " like the headset" % (tf.width, LAMA_WIDTH))
    fr = c["front_right_mm"]
    tf.origin = (fr[0] - tf.ux[0] * LAMA_WIDTH / 2, fr[1] - tf.ux[1] * LAMA_WIDTH / 2, tf.origin[2])
    return tf


# ---------------- reading the real robot (UR "realtime" interface, port 30003; nothing moves) ----------------
def read_robot_state():
    """The robot's joint angles (deg) and the TCP position it reports (mm, robot base frame), right now."""
    with socket.create_connection((ROBOT_IP, ROBOT_STATE_PORT), timeout=3) as s:
        data = b""
        while len(data) < 4:
            data += s.recv(4096)
        size = struct.unpack(">i", data[:4])[0]
        while len(data) < size:
            data += s.recv(4096)

    def d(i):                                    # the i-th number after the message size (CB3 / Polyscope 3.x)
        return struct.unpack(">d", data[4 + 8 * i: 12 + 8 * i])[0]
    q = [math.degrees(d(31 + k)) for k in range(6)]       # q_actual
    tcp = [d(55 + k) * 1000 for k in range(3)]            # tool vector actual (position part)
    return q, tcp


def tip_from_joints(q):
    """Where the gripper tip is for these joint angles (our UR5 maths + TOOL_LENGTH), and how far the tool is from
    pointing straight down (deg)."""
    T = kin.mul(kin.forward(q), kin.transl(0, 0, TOOL_LENGTH))
    tilt = math.degrees(math.acos(max(-1.0, min(1.0, -T[2][2]))))
    return [T[0][3], T[1][3], T[2][3]], tilt


def cmd_check(_args):
    """Nothing moves: shows the robot's joints, where our maths puts the hand, and where the robot says its TCP is."""
    try:
        q, tcp = read_robot_state()
    except OSError as e:
        sys.exit("Can't reach the robot at %s: %s\n(Ethernet cable in? Laptop IP 192.168.1.x? Robot IP right?)" % (ROBOT_IP, e))
    flange = kin.forward(q)
    f = [flange[0][3], flange[1][3], flange[2][3]]
    tip, tilt = tip_from_joints(q)
    gap = math.dist(f, tcp)
    log("connected to the robot at %s" % ROBOT_IP)
    log("joints (deg):            " + "  ".join("%7.1f" % v for v in q))
    log("our maths, flange (mm):  x %7.1f  y %7.1f  z %7.1f" % tuple(f))
    log("our maths, gripper tip:  x %7.1f  y %7.1f  z %7.1f   (TOOL_LENGTH %.0f mm, tool %.0f deg from straight down)"
        % (tip[0], tip[1], tip[2], TOOL_LENGTH, tilt))
    log("robot says TCP (mm):     x %7.1f  y %7.1f  z %7.1f" % tuple(tcp))
    log("distance flange <-> robot's TCP: %.1f mm" % gap)
    print("\nHow to read this: the distance should equal the TCP length set on the pendant (0 if none is set),\n"
          "and stay the SAME when you run this again in a different pose. Then our maths matches the robot.\n"
          "If it is huge (hundreds of mm) or changes a lot between poses: stop and tell Claude.")


def cmd_calib(args):
    print("Calibrate the table for the robot: the SAME two points as the headset uses.\n"
          "Jog the robot (pendant, Move tab) with the gripper pointing straight DOWN until its fingertips touch\n"
          "the table TOP at each point. Then press Enter here: the laptop reads the position from the robot.\n"
          "  1. the FRONT-RIGHT corner (front edge = the long side where the user stands; right = the robot's end)\n"
          "  2. a point on the FRONT EDGE further left, as far as the robot comfortably reaches (40-60 cm)\n"
          "(No connection? Type X Y Z from the pendant instead: Move tab, Feature = Base, mm.)\n")
    pts = {}
    for key, label in (("front_right_mm", "1. FRONT-RIGHT corner"), ("front_edge_mm", "2. FRONT EDGE point, further left")):
        while True:
            s = input("%s: Enter = read from robot, or type X Y Z: " % label).replace(",", " ").split()
            if not s:
                try:
                    q, _tcp = read_robot_state()
                except OSError as e:
                    print("  can't reach the robot (%s). Type X Y Z instead." % e)
                    continue
                tip, tilt = tip_from_joints(q)
                if tilt > 10:
                    print("  the gripper is %.0f deg from pointing straight down: turn it straight down and try again" % tilt)
                    continue
                pts[key] = [round(v, 1) for v in tip]
                print("  read: x %.1f  y %.1f  z %.1f mm" % tuple(tip))
                break
            try:
                pts[key] = [float(v) for v in s[:3]]
                if len(pts[key]) == 3:
                    break
            except ValueError:
                pass
            print("  need three numbers, e.g.  -350.2 -420.0 12.5")
    with open(CALIB_FILE, "w") as f:
        json.dump(pts, f, indent=2)
    tf = load_calib()
    log("saved %s - points %.0f mm apart, table turned %.1f deg in the robot's frame"
        % (CALIB_FILE, math.dist(pts["front_right_mm"][:2], pts["front_edge_mm"][:2]), tf.yaw0))
    if abs(pts["front_edge_mm"][2] - pts["front_right_mm"][2]) > 5:
        log("!! the points differ %.0f mm in height: table not level, or the tip didn't touch both the same way"
            % abs(pts["front_edge_mm"][2] - pts["front_right_mm"][2]))


def real_vs_ghost(robot, tf):
    """Real robot (from calib.json) vs where the headset's ghost stands: how far off would the AR preview be?"""
    ox, oy = -tf.origin[0], -tf.origin[1]                 # robot base, seen from the table origin (robot axes)
    bx = ox * tf.ux[0] + oy * tf.ux[1]
    by = ox * tf.uy[0] + oy * tf.uy[1]
    off = math.hypot(bx - robot["x"] * 1000, by - robot["y"] * 1000)
    real_turn = math.degrees(math.atan2(tf.uy[0], tf.ux[0]))
    ghost_turn = robot["armYaw0"] - arm_yaw(0)
    dturn = (real_turn - ghost_turn + 180) % 360 - 180
    msg = ("real robot vs ghost in the headset: base %.0f mm apart, turned %.1f deg differently "
           "(real base at %.0f, %.0f mm on the table)" % (off, dturn, bx, by))
    if off > 20 or abs(dturn) > 3:
        log("!! " + msg + " - the AR preview will not match the real robot. Redo the calibration (menu 5 and B in"
            " the headset), or tell Claude the numbers.")
    else:
        log(msg + " - OK")


# ---------------- cleaning up what the headset sends ----------------
def half_turn(yaw):
    """A brick (and a two-finger gripper) looks the same turned 180 deg: keep yaw in [-90, 90]."""
    while yaw > 90:
        yaw -= 180
    while yaw < -90:
        yaw += 180
    return yaw


def clean_pose(p, grid_mm, snap_rot):
    p = dict(p)
    # height: whole brick layers (the headset is a few mm off, stacking is not forgiving)
    layer = max(0, round((p["z"] * 1000 - BRICK_H / 2) / BRICK_H))
    p["z"] = (layer * BRICK_H + BRICK_H / 2) / 1000.0
    if grid_mm > 0:
        p["x"] = round(p["x"] * 1000 / grid_mm) * grid_mm / 1000.0
        p["y"] = round(p["y"] * 1000 / grid_mm) * grid_mm / 1000.0
    if snap_rot:
        p["yaw"] = round(p["yaw"] / 90.0) * 90.0
    return p


def plan(layout, tf, args):
    """-> list of jobs: (name, pick, place, pick_centre, place_centre); poses are TCP targets (x,y,z mm, tool yaw deg)."""
    jobs = []
    for b in layout.get("bricks", [])[:MAX_BRICKS]:
        name = b.get("name", "brick%d" % b.get("id", 0))
        if b.get("standing"):
            log("!! skipping %s: standing bricks can't be built with a top-down grip yet" % name)
            continue
        pick_c = list(tf.to_robot(clean_pose(b["pick"], 0, False)))
        place_c = list(tf.to_robot(clean_pose(b["place"], args.grid, args.snap_rot)))
        P, Q = list(pick_c), list(place_c)
        P[2] += BRICK_H / 2 - GRASP_DEPTH                       # brick centre -> grasp point
        Q[2] += BRICK_H / 2 - GRASP_DEPTH + PLACE_CLEARANCE
        P[3] = half_turn(P[3] + TOOL_YAW_OFFSET)
        Q[3] = half_turn(Q[3] + TOOL_YAW_OFFSET)
        bad = [n for n, T in (("pick", P), ("place", Q))
               if math.hypot(T[0], T[1]) > UR5_REACH or T[2] < tf.origin[2] - 1]
        if bad:
            log("!! skipping %s: %s out of reach / below the table" % (name, "+".join(bad)))
            continue
        jobs.append((name, P, Q, pick_c, place_c))
    return jobs


def print_jobs(jobs):
    for name, P, Q, _, _ in jobs:
        log("%-9s pick  x%7.1f y%7.1f z%6.1f yaw%6.1f   ->  place x%7.1f y%7.1f z%6.1f yaw%6.1f"
            % (name, P[0], P[1], P[2], P[3], Q[0], Q[1], Q[2], Q[3]))


# ---------------- backend 1: RoboDK ----------------
class RoboDKBackend:
    def __init__(self, real, tf):
        try:
            from robodk import robolink, robomath
        except ImportError:
            sys.exit("RoboDK API missing:  python -m pip install robodk")
        self.rl, self.rm, self.tf, self.real = robolink, robomath, tf, real
        self.RDK = robolink.Robolink()                       # attaches to the open RoboDK
        self.robot = self.RDK.Item("", robolink.ITEM_TYPE_ROBOT)
        if not self.robot.Valid():
            sys.exit("No robot in the RoboDK station. Add a UR5 (File > Open online library > UR5).")
        log("RoboDK robot:", self.robot.Name())
        self.base = self.robot.Parent()
        self.robot.setPoseFrame(self.base)                   # all targets in robot base coordinates
        self.tool = self.robot.getLink(robolink.ITEM_TYPE_TOOL)
        if not self.tool.Valid():
            self.tool = self.robot.AddTool(robomath.transl(0, 0, TOOL_LENGTH), "PicoBricks gripper")
            log("added a %.0f mm tool in RoboDK (no tool in the station) - set TOOL_LENGTH to your gripper" % TOOL_LENGTH)
        self.robot.setPoseTool(self.tool)
        if real:
            self.robot.setConnectionParams(ROBOT_IP, 30000, "/", "anonymous", "")
            log("connecting to UR5", ROBOT_IP, "...")
            self.robot.Connect()
            state, msg = self.robot.ConnectedState()
            if state != robolink.ROBOTCOM_READY:
                sys.exit("RoboDK could not connect to the robot: %s\n(licence? IP? try --backend urscript)" % msg)
            self.RDK.setRunMode(robolink.RUNMODE_RUN_ROBOT)
            log("RoboDK is driving the REAL robot")
        else:
            self.RDK.setRunMode(robolink.RUNMODE_SIMULATE)
            log("RoboDK simulation only")
        self.robot.setSpeed(SPEED, JOINT_SPEED)
        self.bricks = {}

    # tool pointing down, turned by yaw
    def pose(self, x, y, z, yaw):
        m = self.rm
        return m.transl(x, y, z) * m.rotz(math.radians(yaw)) * m.rotx(math.pi)

    def go_home(self):
        if self.real:
            self.home = self.robot.Joints()                  # wherever the operator left it
            return
        # Simulation: start from a sensible tool-down pose above the table, not RoboDK's stretched-out zero pose
        self.robot.MoveJ(HOME_JOINTS)
        cx, cy, cz = self.tf.point(0, 300, TRAVEL_Z)
        try:
            self.robot.MoveJ(self.pose(cx, cy, cz, self.tf.yaw0 + 90))
        except Exception as e:
            log("   (couldn't reach above the table centre: %s)" % e)
        self.home = self.robot.Joints()

    # ---- visible bricks in the simulation ----
    def box(self, name, height=BRICK_H, color=(0.62, 0.42, 0.24, 1.0)):
        hx, hy, hz = BRICK_W / 2, BRICK_L / 2, height / 2
        v = [[sx * hx, sy * hy, sz * hz] for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
        faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
        tris = []
        for a, b, c, d in faces:
            tris += [v[a], v[b], v[c], v[a], v[c], v[d]]
        obj = self.RDK.AddShape(tris)
        obj.setName(name)
        obj.setColor(list(color))
        obj.setParent(self.base)
        return obj

    def show_bricks(self, jobs):
        for it in self.RDK.ItemList(self.rl.ITEM_TYPE_OBJECT) + self.RDK.ItemList(self.rl.ITEM_TYPE_TARGET):
            if it.Name().startswith("PB_"):
                it.Delete()
        # The big coordinate arrows on the robot base and the tool hide the bricks: show the items, not their frames
        for item in (self.base, self.tool):
            try:
                item.setVisible(True, False)
            except Exception:
                pass
        for name, P, Q, pc, qc in jobs:
            obj = self.box("PB_" + name)
            obj.setPose(self.rm.transl(pc[0], pc[1], pc[2]) * self.rm.rotz(math.radians(pc[3])))
            self.bricks[name] = obj
            # Where the brick will go: a flat, see-through green footprint (instead of a large target arrow)
            ghost = self.box("PB_place_" + name, height=2.0, color=(0.2, 0.75, 0.4, 0.35))
            ghost.setPose(self.rm.transl(qc[0], qc[1], qc[2] - BRICK_H / 2 + 1.0) * self.rm.rotz(math.radians(qc[3])))

    def grip(self, close, name=None):
        self.robot.setDO(GRIP_DO, GRIP_CLOSE_VALUE if close else 1 - GRIP_CLOSE_VALUE)
        self.robot.Pause(GRIP_WAIT_S * 1000)
        obj = self.bricks.get(name)
        if obj is not None:
            obj.setParentStatic(self.tool if close else self.base)   # brick travels with the gripper

    def travel_to(self, T):
        x, y, z, yaw = T
        self.robot.MoveJ(self.pose(x, y, max(z + APPROACH, self.tf.origin[2] + TRAVEL_Z), yaw))

    def down_up(self, T, close, name):
        x, y, z, yaw = T
        self.travel_to(T)
        self.robot.MoveL(self.pose(x, y, z + APPROACH, yaw))
        self.robot.MoveL(self.pose(x, y, z, yaw))
        self.grip(close, name)
        self.robot.MoveL(self.pose(x, y, z + APPROACH, yaw))
        self.travel_to(T)

    def ensure_connected(self):
        # RoboDK may have been restarted or the station reloaded since we connected (the listener can run
        # for hours); the old robot/tool handles are then invalid ("Invalid item provided"). Reconnect.
        try:
            stale = not self.robot.Valid() or not self.tool.Valid()
        except Exception:
            stale = True
        if stale:
            log("RoboDK station changed since connecting - reconnecting")
            self.__init__(self.real, self.tf)

    def run(self, jobs):
        self.ensure_connected()
        self.show_bricks(jobs)
        self.go_home()
        self.grip(False)                                      # start with the gripper open
        for name, P, Q, _, _ in jobs:
            log("building", name)
            self.down_up(P, True, name)
            self.down_up(Q, False, name)
        self.robot.MoveJ(self.home)
        log("done")

    def hover(self, pts):
        self.go_home()
        for p in pts:
            self.robot.MoveJ(self.pose(p[0], p[1], p[2] + 30, self.tf.yaw0))
            self.robot.Pause(2000)
        self.robot.MoveJ(self.home)


# ---------------- backend 2: plain URScript (no RoboDK needed) ----------------
class URScriptBackend:
    def __init__(self, real, tf):
        self.real, self.tf = real, tf

    @staticmethod
    def p(x, y, z, yaw):
        # tool down = Rz(yaw) * Rx(pi), written as a UR rotation vector
        h = math.radians(yaw) / 2
        return "p[%.5f,%.5f,%.5f,%.5f,%.5f,0]" % (x / 1000, y / 1000, z / 1000, math.pi * math.cos(h), math.pi * math.sin(h))

    @staticmethod
    def grip(close):
        val = "True" if (close == bool(GRIP_CLOSE_VALUE)) else "False"
        fn = "set_tool_digital_out" if GRIP_ON_TOOL else "set_standard_digital_out"
        return ["  %s(%d, %s)" % (fn, GRIP_DO, val), "  sleep(%.2f)" % GRIP_WAIT_S]

    @staticmethod
    def joint_moves(frames):
        """The exact poses the ghost played, as URScript joint moves, with the gripper at the marked poses.
        The first pose (the ghost's 'home') is skipped: the robot goes from wherever it is straight above the table."""
        v, a = math.radians(JOINT_SPEED), math.radians(JOINT_ACCEL)
        lines = []
        for f in frames[1:]:
            q = ",".join("%.5f" % math.radians(x) for x in f["j"])
            lines.append("  movej([%s], a=%.3f, v=%.3f)" % (q, a, v))
            if f.get("grip"):
                lines += URScriptBackend.grip(f["grip"] == "close")
        return lines

    def down_up(self, T, close):
        x, y, z, yaw = T
        travel = self.p(x, y, max(z + APPROACH, self.tf.origin[2] + TRAVEL_Z), yaw)
        up, at = self.p(x, y, z + APPROACH, yaw), self.p(x, y, z, yaw)
        v, a = SPEED / 1000, 0.5
        return (["  movej(%s, a=0.8, v=%.2f)" % (travel, math.radians(JOINT_SPEED)),
                 "  movel(%s, a=%.2f, v=%.3f)" % (up, a, v),
                 "  movel(%s, a=%.2f, v=%.3f)" % (at, a, v)] + self.grip(close) +
                ["  movel(%s, a=%.2f, v=%.3f)" % (up, a, v),
                 "  movel(%s, a=%.2f, v=%.3f)" % (travel, a, v)])

    def send(self, lines):
        prog = ("def picobricks():\n  home = get_actual_joint_positions()\n" + "\n".join(self.grip(False) + lines) +
                "\n  movej(home, a=0.8, v=0.5)\n  textmsg(\"picobricks done\")\nend\n")
        path = os.path.join(HERE, "last_program.script")
        with open(path, "w") as f:
            f.write(prog)
        log("URScript written to", path)
        if not self.real:
            log("(dry run: add --real to send it to the robot)")
            return
        with socket.create_connection((ROBOT_IP, 30002), timeout=3) as s:
            s.sendall(prog.encode())
        log("sent to UR5 - it is moving now. E-stop in reach.")

    def run(self, jobs):
        # Exactly the joint angles the ghost robot played in the headset (same maths), not targets the robot's
        # controller solves its own way: what you saw is what it does.
        self.send(self.joint_moves(preview(jobs, self.tf)))

    def hover(self, pts):
        # Joint moves from our own UR5 maths, so this also tests that our maths matches the real robot
        v, a = math.radians(JOINT_SPEED), math.radians(JOINT_ACCEL)
        q = [float(x) for x in HOME_JOINTS]
        lines = []
        for p in pts:
            flange = kin.mul(kin.tcp_pose(p[0], p[1], p[2] + 30, self.tf.yaw0), kin.transl(0, 0, -TOOL_LENGTH))
            q = kin.inverse(flange, q)
            if q is None:
                sys.exit("can't reach above the corner %s - check calib.json" % p)
            lines += ["  movej([%s], a=%.3f, v=%.3f)" % (",".join("%.5f" % math.radians(x) for x in q), a, v),
                      "  sleep(3)"]
        self.send(lines)

    def grip_test(self, close):
        self.send(self.grip(close))


class PrintBackend:
    def __init__(self, real, tf):
        pass

    def run(self, jobs):
        log("(print backend: nothing moves)")

    def hover(self, pts):
        for p in pts:
            log("would hover 30 mm above", ["%.1f" % v for v in p])


class GhostBackend(PrintBackend):
    """Only the ghost robot in the headset moves (no RoboDK, no robot)."""


BACKENDS = {"robodk": RoboDKBackend, "urscript": URScriptBackend, "print": PrintBackend, "ghost": GhostBackend}


# ---------------- commands ----------------
def confirm(args, n):
    if args.yes:
        return True
    if args.real:
        return input("Build %d bricks on the REAL robot? Speed slider low, e-stop in hand. [y/N] " % n).strip().lower() == "y"
    # Asking in the simulation too gives the user time to take the headset off and watch the screen (Jin, 19 Oct).
    # Remember: a new wall from the headset is only built after answering here.
    return input("Run %d bricks in the simulation? [Y/n] " % n).strip().lower() != "n"


# ---------------- ghost preview: the robot's movements as joint angles (own UR5 maths, no RoboDK) ----------------
def arm_yaw(base_deg):
    """Direction the arm points (deg, robot base frame) with base joint `base_deg` and all others 0."""
    a = kin.forward([base_deg, 0, 0, 0, 0, 0])
    b = kin.forward([base_deg, -90, 0, 0, 0, 0])
    return math.degrees(math.atan2(a[1][3] - b[1][3], a[0][3] - b[0][3]))


def preview(jobs, tf):
    """The same moves as RoboDKBackend.run(), as joint angles for the headset's ghost robot:
    list of {"j": 6 angles in deg, "grip": "close"/"open", "brick": name}."""
    frames = []

    def ik(T, seed):
        x, y, z, yaw = T
        flange = kin.mul(kin.tcp_pose(x, y, z, yaw), kin.transl(0, 0, -TOOL_LENGTH))
        q = kin.inverse(flange, seed)
        if q is None:
            raise ValueError("the robot can't reach x %.0f y %.0f z %.0f mm" % (x, y, z))
        return q

    def add(q, **extra):
        frames.append(dict(j=[round(v, 2) for v in q], **extra))

    def straight(a, b, q):                       # MoveL: a few poses on the straight line
        for i in range(1, PREVIEW_STEPS + 1):
            f = i / PREVIEW_STEPS
            q = ik([a[k] + (b[k] - a[k]) * f for k in range(4)], q)
            add(q)
        return q

    q = [float(v) for v in HOME_JOINTS]
    add(q)
    cx, cy, cz = tf.point(0, 300, TRAVEL_Z)
    try:
        q = ik((cx, cy, cz, tf.yaw0 + 90), q)
        add(q)
    except ValueError:
        pass
    home = list(q)
    for name, P, Q, _, _ in jobs:
        for T, grip in ((P, "close"), (Q, "open")):
            x, y, z, yaw = T
            up, at = (x, y, z + APPROACH, yaw), (x, y, z, yaw)
            # Carry height: TRAVEL_Z above the table, but lower for far-away bricks, where a stretched-out arm can't
            # reach that high (the UR5 reaches 900 mm from its base at table height, 815 mm at 250 mm up)
            travel_z = max(z + APPROACH, tf.origin[2] + TRAVEL_Z)
            while travel_z > z + APPROACH:
                try:
                    ik((x, y, travel_z, yaw), q)
                    break
                except ValueError:
                    travel_z = max(z + APPROACH, travel_z - 20)
            travel = (x, y, travel_z, yaw)
            try:
                q = ik(travel, q)
                add(q)
                q = straight(travel, up, q)
                q = straight(up, at, q)
                frames[-1].update(grip=grip, brick=name)
                q = straight(at, up, q)
                q = straight(up, travel, q)
            except ValueError:
                where = "in the tray" if grip == "close" else "in the wall"
                dist = math.hypot(x, y)
                why = "too close to its base" if dist < 250 else "too far away"
                raise ValueError("the robot can't reach %s %s (%.0f cm from the robot: %s)"
                                 % (name, where, dist / 10, why))
    add(home)
    return frames


def send_preview(layout, tf, jobs, backend, sock, addr):
    """Work out the robot's movements in RoboDK and send them to the headset, where the ghost UR5 plays them."""
    msg = {"type": "preview", "seq": layout.get("seq", 0)}
    try:
        msg["frames"] = preview(jobs, tf)
        log("sent the robot movements to the headset (%d poses) - watch the ghost robot" % len(msg["frames"]))
    except Exception as e:
        msg["error"] = str(e)
        log("!! preview failed:", e)
    try:
        sock.sendto(json.dumps(msg).encode(), (addr, PREVIEW_PORT))
    except OSError as e:
        log("!! couldn't send the preview to the headset:", e)


def handle(layout, tf, backend, args, sock=None, addr=None):
    # Simulation: stand RoboDK's robot where the headset's ghost robot stands (the real robot uses calib.json)
    robot = layout.get("robot") or {}
    if robot.get("valid") and not args.real:
        try:
            tf = ghost_frame(robot, arm_yaw(0), arm_yaw(90))
            backend.tf = tf
        except Exception as e:
            log("!! couldn't line the robot up with the ghost robot, using calib.json:", e)
    if robot.get("valid") and args.real:
        real_vs_ghost(robot, tf)
    jobs = plan(layout, tf, args)
    if getattr(args, "one", False) and jobs:
        jobs = jobs[:1]
        log("ONE-BRICK TEST: only %s is built" % jobs[0][0])
    log("layout seq %s: %d bricks received, %d to build" % (layout.get("seq"), len(layout.get("bricks", [])), len(jobs)))
    if not jobs:
        return
    print_jobs(jobs)
    if sock is not None and addr:
        send_preview(layout, tf, jobs, backend, sock, addr)
    if isinstance(backend, GhostBackend):
        return                                    # preview only, nothing else moves
    if confirm(args, len(jobs)):
        try:
            backend.run(jobs)
        except OSError as e:                      # no network connection to the robot
            log("!! can't connect to the robot at %s: %s (Ethernet cable? IP addresses? menu c = robot check)" % (ROBOT_IP, e))
        except Exception as e:                    # e.g. RoboDK "target not reachable"
            log("!! robot error:", e)
            log("   (unreachable target? check calib.json / where the wall is on the table)")


def cmd_run(args):
    tf = load_calib()
    backend = BACKENDS[args.backend](args.real, tf)
    if args.file:
        with open(args.file) as f:
            handle(json.load(f), tf, backend, args)
        return
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", UDP_PORT))
    try:
        ips = sorted({a[4][0] for a in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)})
    except OSError:
        ips = ["?"]
    log("listening on UDP %d - build a wall in the headset (A = OK). Laptop IPs: %s" % (UDP_PORT, ", ".join(ips)))
    log("(Ctrl+C = stop listening and go back to the menu)")
    last_seq, last_time = None, 0.0
    # Wait in 1-second steps: a plain wait doesn't notice Ctrl+C on Windows until a message arrives
    sock.settimeout(1.0)
    while True:
        try:
            data, addr = sock.recvfrom(65535)
        except socket.timeout:
            continue
        except KeyboardInterrupt:
            log("stopped listening")
            return
        # Walls that arrived while the previous one was being built are queued: only build the newest
        sock.setblocking(False)
        try:
            while True:
                data, addr = sock.recvfrom(65535)
        except (BlockingIOError, OSError):
            pass
        finally:
            sock.settimeout(1.0)          # back to waiting in 1-second steps
        try:
            layout = json.loads(data.decode())
        except ValueError:
            log("ignored junk from", addr)
            continue
        # Each wall arrives twice (direct + broadcast) with the same seq. The headset restarts seq at 1 when the
        # app restarts, so only treat it as a duplicate if it also arrived within a couple of seconds.
        now = time.time()
        if layout.get("seq") == last_seq and now - last_time < 2.0:
            continue
        last_seq, last_time = layout.get("seq"), now
        log("from headset", addr[0])
        with open(os.path.join(HERE, "last_layout.json"), "w") as f:
            json.dump(layout, f, indent=2)       # replay later with --file last_layout.json
        handle(layout, tf, backend, args, sock, addr[0])


def cmd_corners(args):
    tf = load_calib()
    with open(CALIB_FILE) as f:
        c = json.load(f)
    if args.real and not args.yes and input("Hover 30 mm above both calibration points on the REAL robot? [y/N] ").lower() != "y":
        return
    pts = [c["front_right_mm"], c["front_edge_mm"]] if "front_edge_mm" in c else [c["front_left_mm"], c["front_right_mm"]]
    BACKENDS[args.backend](args.real, tf).hover(pts)


def cmd_grip(args):
    if args.real and not args.yes and input("%s the gripper on the REAL robot? [y/N] " % args.action.capitalize()).lower() != "y":
        return
    URScriptBackend(args.real, None).grip_test(args.action == "close")


def main():
    global ROBOT_IP
    ap = argparse.ArgumentParser(description="PicoBricks headset -> UR5")
    sub = ap.add_subparsers(dest="cmd", required=True)
    parsers = {
        "calib": sub.add_parser("calib", help="the two table corners: read from the robot (Enter) or typed"),
        "check": sub.add_parser("check", help="read the robot's joints and position (nothing moves)"),
        "grip": sub.add_parser("grip", help="open or close the gripper (gripper test)"),
    }
    parsers["grip"].add_argument("action", choices=["open", "close"])
    parsers["grip"].add_argument("--real", action="store_true", help="send it to the real robot (default: only write the script)")
    parsers["grip"].add_argument("--yes", action="store_true", help="don't ask")
    for name in ("run", "corners"):
        s = sub.add_parser(name)
        parsers[name] = s
        s.add_argument("--backend", choices=BACKENDS, default="robodk")
        s.add_argument("--real", action="store_true", help="move the real robot (default: simulation / dry run)")
        s.add_argument("--yes", action="store_true", help="don't ask before moving")
        if name == "run":
            s.add_argument("--file", help="use a saved layout instead of listening for the headset")
            s.add_argument("--grid", type=float, default=0, help="snap x/y to this grid in mm (e.g. 50); 0 = off")
            s.add_argument("--snap-rot", action="store_true", help="snap yaw to 90 deg (hand-placed bricks)")
            s.add_argument("--one", action="store_true", help="first test: build only the first brick")
    for s in parsers.values():
        s.add_argument("--ip", default=ROBOT_IP, help="robot IP address (default %s)" % ROBOT_IP)
    args = ap.parse_args()
    ROBOT_IP = args.ip
    {"calib": cmd_calib, "run": cmd_run, "corners": cmd_corners, "check": cmd_check, "grip": cmd_grip}[args.cmd](args)


if __name__ == "__main__":
    main()
