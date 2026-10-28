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
ROBOT_IP = "192.168.1.3"     # LAMA UR5 (CB3, Polyscope 3.15), pendant: About screen; laptop 192.168.1.100 (was .2 until 28 Oct)
ROBOT_STATE_PORT = 30003     # the robot streams its joints and position here (read only)
LAMA_WIDTH = 1348.0          # mm, the real LAMA table's front edge (tape, 21 Oct); = RealTableLength in BrickSceneSetup.cs
UDP_PORT = 5005
PREVIEW_PORT = 5006     # the headset listens here for the robot movements (ghost UR5 preview, GhostPlayer.cs)
PREVIEW_STEPS = 4       # straight (MoveL) moves are sent as this many in-between poses
ELBOW_MIN_Z = 150.0     # mm: only "elbow up" arm shapes, elbow at least this high above the robot base (safety, 22 Oct)

# Gripper (soft/parallel gripper picking the brick from the top)
GRIP_DO = 4             # digital output 4 closes the LAMA gripper, off opens it (pendant I/O tab, 21 Oct)
GRIP_ON_TOOL = False    # True if the gripper is wired to the TOOL outputs at the flange, not the control box
GRIP_CLOSE_VALUE = 1    # value that CLOSES it (flip to 0 if yours is the other way round)
GRIP_WAIT_S = 0.8       # time for the gripper to close / open
GRASP_DEPTH = 25.0      # mm: how far below the brick's top face the TCP goes (0 = suction on top)
TOOL_YAW_OFFSET = 0.0   # deg: add 90 if the fingers close along the brick's length instead of across it
TOOL_LENGTH = 151.0     # mm flange -> fingertips: the TCP set on the LAMA pendant (menu c, 21 Oct)

BRICK_W, BRICK_L, BRICK_H = 50.0, 100.0, 50.0   # mm
APPROACH = 80.0         # mm: go straight down/up this last bit with MoveL
TRAVEL_Z = 250.0        # mm above the table top for moves between pick and place
PLACE_CLEARANCE = 2.0   # mm: release this far above the exact place height
SPEED = 100.0           # mm/s linear (first tests: slow)
APPROACH_SPEED = 50.0   # mm/s: the last bit straight down onto a brick and straight up again (movel)
APPROACH_ACCEL = 300.0  # mm/s^2
JOINT_SPEED = 30.0      # deg/s (the pendant's speed slider slows this down further)
JOINT_ACCEL = 60.0      # deg/s^2
BLEND_MAX = 20.0        # mm: how much the robot may cut corners between in-between poses (0 = stop at every pose)
HOME_JOINTS = [0, -90, -90, -90, 90, 0]   # simulation only (no READY pose saved): a tool-down pose
# READY pose (28 Oct): every real-robot program starts AND ends here, planned from it. The robot sometimes started
# from a pose that looked right but had a joint turned by most of a turn (+300 deg looks like -60 deg); the first move
# then spun the arm and it hit itself. Jin can't see that difference, so the LAPTOP decides READY: calculated with our
# UR5 maths (gripper down above the table, every joint untwisted, -180..180 deg). Menu g gets the robot there by a
# careful route, menu v shows it on the ghost robot first. (Menu r can still save a hand-placed pose instead, only if
# no joint is outside -180..180; it goes to ready_pose.json, delete that file to go back to the calculated one.)
READY_FILE = os.path.join(HERE, "ready_pose.json")
READY_ABOVE_TABLE = 250.0   # mm: READY = gripper this high above the middle of the table, pointing down
LIFT_ABOVE_TABLE = 150.0    # mm: menu g first lifts the gripper to at least this height (away from bricks / table)
ROUTE_MIN_CLEARANCE = 30.0  # mm: on every step of menu g the gripper tip and wrist stay this far above the table
READY_TOLERANCE = 5.0   # deg: every joint must be this close to READY (as numbers, so a full turn counts) to start
READY_SPEED = 15.0      # deg/s: menu g, going to READY
# Safety net: refuse a program if a joint turns more than this between two planned poses (base, shoulder, elbow,
# wrist 1, wrist 2, wrist 3). A spin of a full turn is always caught.
MAX_JOINT_STEP = [200.0, 120.0, 120.0, 120.0, 120.0, 200.0]
JOINT_NAMES = ["base", "shoulder", "elbow", "wrist 1", "wrist 2", "wrist 3"]
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


def computed_ready(tf):
    """READY worked out by the laptop: gripper pointing down READY_ABOVE_TABLE above the middle of the table, elbow
    up, and every joint as close as possible to the untwisted UR pose HOME_JOINTS (so all are within -180..180)."""
    x, y, z = tf.point(0, 300, READY_ABOVE_TABLE)
    q = ik_closest(x, y, z, [tf.yaw0 + 90 * k for k in range(4)], [float(v) for v in HOME_JOINTS])
    if q is None or any(abs(v) > 180 for v in q):
        return None
    return [round(v, 3) for v in q]


def load_ready(tf=None):
    """READY joint angles (deg): a pose saved with menu r (ready_pose.json) if there is one, else calculated."""
    try:
        with open(READY_FILE) as f:
            q = [float(v) for v in json.load(f)["joints_deg"]]
        if len(q) == 6:
            return q
    except (OSError, ValueError, KeyError, TypeError):
        pass
    try:
        return computed_ready(tf or load_calib())
    except (OSError, ValueError, KeyError, SystemExit):
        return None


def ready_source():
    return "the start position shared with Suzanne (ready_pose.json)" if os.path.exists(READY_FILE) else "calculated by the laptop"


def ready_offsets(q, ready):
    """[(joint index, how many degrees it is away from READY)] for joints further away than READY_TOLERANCE.
    Compared as numbers: a joint turned a full turn (+300 vs -60) is 360 deg away, not 0."""
    return [(i, a - r) for i, (a, r) in enumerate(zip(q, ready)) if abs(a - r) > READY_TOLERANCE]


def describe_offsets(offs):
    return ", ".join("%s %+.0f deg" % (JOINT_NAMES[i], d) for i, d in offs)


def require_ready(q):
    """READY joints if the robot (joints q) stands there; otherwise ValueError with what to do."""
    ready = load_ready()
    if ready is None:
        raise ValueError("no READY pose (calib.json missing or the READY spot is out of reach)")
    offs = ready_offsets(q, ready)
    if offs:
        raise ValueError("the robot is not at its READY pose (%s). Menu g = go to READY slowly, then try again"
                         % describe_offsets(offs))
    return ready


def check_steps(poses):
    """ValueError if a joint turns more than MAX_JOINT_STEP between two consecutive poses (lists of 6 deg)."""
    for n, (a, b) in enumerate(zip(poses, poses[1:])):
        for i in range(6):
            if abs(b[i] - a[i]) > MAX_JOINT_STEP[i]:
                raise ValueError("planned move %d would turn the %s by %.0f deg in one go - refusing (safety)"
                                 % (n + 1, JOINT_NAMES[i], b[i] - a[i]))


def _wrap(a):
    return (a + 180.0) % 360.0 - 180.0


def arm_side(q):
    """The arm shape of joints q: (base angle, sign of wrist 2, sign of elbow). A program keeps ONE shape from start
    to end: on the start position's side the gripper-down poses have wrist 2 at -90 instead of +90, and a filter that
    only knew +90 made the planner jump to the other side mid-program: base 133 / wrist 1 150 deg in one go (28 Oct)."""
    return (q[0], 1 if _wrap(q[4]) >= 0 else -1, 1 if _wrap(q[2]) >= 0 else -1)


def _on_side(d, side):
    return (abs(_wrap(d[0] - side[0])) < 90 and (1 if _wrap(d[4]) >= 0 else -1) == side[1]
            and (1 if _wrap(d[2]) >= 0 else -1) == side[2] and kin.elbow_height(d) >= ELBOW_MIN_Z)


def ik_closest(x, y, z, yaws, seed, side=None):
    """Joint angles (deg) that put the tool, pointing down, at (x, y, z) mm, for whichever of these tool yaws keeps the
    joints closest to seed, or None. side (arm_side of the start pose): only solutions in that arm shape (and elbow up);
    without: the original elbow-up / wrist-2-positive filter. Choosing among equivalent yaws keeps the wrist from
    turning further than needed (28 Oct: up to 173 deg in one move before)."""
    best, best_cost = None, None
    for yaw in yaws:
        flange = kin.mul(kin.tcp_pose(x, y, z, yaw), kin.transl(0, 0, -TOOL_LENGTH))
        if side is None:
            cands = [kin.inverse(flange, seed, ELBOW_MIN_Z)]
        else:
            cands = []
            for sol in kin.solutions(flange):
                d = [math.degrees(a) for a in sol]
                d = [v + 360 * round((s - v) / 360) for v, s in zip(d, seed)]       # whole turns closest to seed
                d = [v - 360 if v > 360 else v + 360 if v < -360 else v for v in d]
                if _on_side(d, side):
                    cands.append(d)
        for q in cands:
            if q is None:
                continue
            cost = sum((a - s) ** 2 for a, s in zip(q, seed))
            if best is None or cost < best_cost:
                best, best_cost = q, cost
    return best


def read_or_exit():
    try:
        return read_robot_state()
    except OSError as e:
        sys.exit("Can't reach the robot at %s: %s\n(Ethernet cable in? Laptop IP 192.168.1.x? Robot IP right?)"
                 % (ROBOT_IP, e))


def cmd_ready_save(args):
    """Menu r: the robot's CURRENT pose becomes READY (move it there on the pendant first). Nothing moves."""
    q, _ = read_or_exit()
    tip, tilt = tip_from_joints(q)
    problems = []
    if kin.elbow_height(q) < ELBOW_MIN_Z:
        problems.append("the elbow is low (%.0f mm above the base, needs %.0f): lift it" % (kin.elbow_height(q), ELBOW_MIN_Z))
    if not kin.safe_shape([math.radians(v) for v in q], ELBOW_MIN_Z):
        problems.append("the arm is not in the normal elbow-up / wrist-down shape")
    if tilt > 20:
        problems.append("the gripper is not pointing down (%.0f deg off)" % tilt)
    if tip[2] < 150:
        problems.append("the gripper is low (%.0f mm above the robot base): lift it" % tip[2])
    twisted = [JOINT_NAMES[i] for i, v in enumerate(q) if abs(v) > 180]
    if twisted:
        problems.append("%s outside -180..180 deg (turned more than half a turn, it may LOOK fine): turn it back "
                        "on the pendant, or just use the laptop's calculated READY (menu g)" % ", ".join(twisted))
    log("robot now (deg): " + "  ".join("%s %.1f" % (n, v) for n, v in zip(JOINT_NAMES, q)))
    log("gripper tip (mm): x %.0f  y %.0f  z %.0f" % tuple(tip))
    if problems:
        sys.exit("NOT saved as READY:\n  - " + "\n  - ".join(problems))
    if not args.yes and input("Save this as the READY pose (every program starts and ends here)? [y/N] ").lower() != "y":
        return
    with open(READY_FILE, "w") as f:
        json.dump({"joints_deg": [round(v, 3) for v in q], "tip_mm": [round(v, 1) for v in tip],
                   "saved": time.strftime("%Y-%m-%d %H:%M")}, f, indent=2)
    log("READY pose saved to", READY_FILE)


# ---------------- the arm as solid tubes: gripper must not hit the robot's own arm, nothing may hit the table -----
# (28 Oct) Suzanne's start pose has the gripper pointing sideways; going from there to our gripping shape in one move
# swung our gripper ~10 cm INTO the forearm (wrist 2 passing -180 deg). Every change-over and every menu-g route is
# now checked against this model before anything moves. UR5 links sit beside the DH line along the joint axis.
SHOULDER_OFFSET, ELBOW_OFFSET = 135.85, 119.7          # mm (UR5)
ARM_RADIUS = {"base": 75.0, "upper": 55.0, "fore": 45.0, "wrist": 45.0, "tool": 60.0}   # mm; tool = our gripper block
MIN_ARM_GAP = 15.0        # mm: gripper <-> robot's own base / upper arm / forearm (normal gripping poses have ~20)
MIN_TABLE_GAP = 30.0      # mm: arm and gripper above the table (outside the gripping moves themselves)
UPRIGHT = (-90.0, 0.0)    # shoulder, elbow: the arm pointing straight up (the UR's own safe pose)
ELBOW_LIMIT = 160.0       # deg: further than this the forearm folds onto the upper arm


def _arm_tubes(q):
    T = kin._link(0, math.radians(q[0]))
    F = [T]
    for i in range(1, 6):
        T = kin.mul(T, kin._link(i, math.radians(q[i])))
        F.append(T)
    pos = lambda M: (M[0][3], M[1][3], M[2][3])
    ax = (F[0][0][2], F[0][1][2], F[0][2][2])                 # shoulder / elbow / wrist-1 axis
    off = lambda p, d: tuple(a + d * b for a, b in zip(p, ax))
    sh, el, w1 = pos(F[0]), pos(F[1]), pos(F[2])
    fl = pos(F[5])
    tip = tuple(a + TOOL_LENGTH * b for a, b in zip(fl, (F[5][0][2], F[5][1][2], F[5][2][2])))
    return {"base": ((0.0, 0.0, -50.0), (0.0, 0.0, sh[2])),
            "upper": (off(sh, SHOULDER_OFFSET), off(el, SHOULDER_OFFSET)),
            "fore": (off(el, SHOULDER_OFFSET - ELBOW_OFFSET), off(w1, SHOULDER_OFFSET - ELBOW_OFFSET)),
            "wrist": (pos(F[3]), pos(F[4])), "tool": (fl, tip)}


def _seg_dist(p1, q1, p2, q2):
    """Shortest distance between segments p1-q1 and p2-q2 (mm)."""
    d1 = [b - a for a, b in zip(p1, q1)]
    d2 = [b - a for a, b in zip(p2, q2)]
    r = [a - b for a, b in zip(p1, p2)]
    a, e = sum(x * x for x in d1), sum(x * x for x in d2)
    b, c, f = sum(x * y for x, y in zip(d1, d2)), sum(x * y for x, y in zip(d1, r)), sum(x * y for x, y in zip(d2, r))
    den = a * e - b * b
    sn = min(1.0, max(0.0, (b * f - c * e) / den)) if den > 1e-9 else 0.0
    t = (b * sn + f) / e if e > 1e-9 else 0.0
    if t < 0:
        t, sn = 0.0, (min(1.0, max(0.0, -c / a)) if a > 1e-9 else 0.0)
    elif t > 1:
        t, sn = 1.0, (min(1.0, max(0.0, (b - c) / a)) if a > 1e-9 else 0.0)
    return math.dist([p + sn * d for p, d in zip(p1, d1)], [p + t * d for p, d in zip(p2, d2)])


def arm_gaps(q, table):
    """(gripper <-> own arm, arm/gripper <-> table) clearance in mm for joints q (deg)."""
    B = _arm_tubes(q)
    R = ARM_RADIUS
    arm = min(_seg_dist(*B["tool"], *B[o]) - R["tool"] - R[o] for o in ("base", "upper", "fore"))
    arm = min(arm, _seg_dist(*B["wrist"], *B["base"]) - R["wrist"] - R["base"])
    pts = [B["upper"][1], B["fore"][0], B["fore"][1], B["wrist"][0], B["wrist"][1], B["tool"][0], B["tool"][1]]
    return arm, min(p[2] for p in pts) - table - 40.0      # ~40 mm of body below those centre lines


def move_gaps(a, b, table, n=60):
    """Worst clearances along a joint move from a to b (the robot's movej interpolates the joints linearly)."""
    worst_arm, worst_table = 1e9, 1e9
    for k in range(n + 1):
        g_arm, g_table = arm_gaps([x + (y - x) * k / n for x, y in zip(a, b)], table)
        worst_arm, worst_table = min(worst_arm, g_arm), min(worst_table, g_table)
    return worst_arm, worst_table


def transition(a, b, tf):
    """[(what happens, joints)] from joints a to b, checked against the arm model; ValueError if no safe way.
    Tries, in this order: one direct move; the wrists first with wrist 2 pointing the gripper away from the arm (0 deg)
    then base, shoulder + elbow, wrist 3; the same after first bringing the arm straight up."""
    table = tf.origin[2]

    def via_wrists(p0):
        p, out = list(p0), []
        if abs(p[4]) > 0.5 and (abs(p[3] - b[3]) > 0.5 or abs(p[4] - b[4]) > 0.5):
            p[4] = 0.0
            out.append(("wrist 2 to 0 (gripper pointing away from the arm)", list(p)))
        for idx, what in (((3,), "wrist 1"), ((4,), "wrist 2"), ((0,), "base"), ((1, 2), "shoulder and elbow"),
                          ((5,), "wrist 3")):
            if any(abs(p[i] - b[i]) > 0.01 for i in idx):
                for i in idx:
                    p[i] = b[i]
                out.append((what, list(p)))
        return out

    up = list(a)
    up[1], up[2] = UPRIGHT
    candidates = [[("straight to the pose", list(b))], via_wrists(a),
                  [("arm straight up (shoulder and elbow)", list(up))] + via_wrists(up)]
    best = None
    for steps in candidates:
        if not steps:
            return []
        cur, worst_arm, worst_table, bad = list(a), 1e9, 1e9, False
        for _, nxt in steps:
            if abs(nxt[2]) > ELBOW_LIMIT:
                bad = True
                break
            g_arm, g_table = move_gaps(cur, nxt, table)
            worst_arm, worst_table = min(worst_arm, g_arm), min(worst_table, g_table)
            cur = nxt
        if not bad and worst_arm >= MIN_ARM_GAP and worst_table >= MIN_TABLE_GAP:
            return steps
        if not bad and (best is None or min(worst_arm, worst_table) > best):
            best = min(worst_arm, worst_table)
    raise ValueError("no safe way found (the gripper would come within %.0f mm of the arm or the table): move the robot "
                     "closer on the pendant (Move tab) first" % (best if best is not None else -1))


def check_arm(frames, tf):
    """Last check before a program goes to the real robot: every move against the arm model. ValueError if the
    gripper would come closer than MIN_ARM_GAP to the robot's own arm anywhere, or the arm closer than MIN_TABLE_GAP
    to the table outside the straight stretches down to a brick (there the gripper is meant to be at the table)."""
    table = tf.origin[2]
    J = [f["j"] for f in frames]
    for i, (a, b) in enumerate(zip(J, J[1:])):
        g_arm, g_table = move_gaps(a, b, table, 20)
        near_brick = any(f.get("lin") or f.get("grip") for f in (frames[i], frames[i + 1]))
        if g_arm < MIN_ARM_GAP:
            raise ValueError("move %d would bring the gripper within %.0f mm of the robot's own arm - refusing (safety)"
                             % (i + 1, g_arm))
        if not near_brick and g_table < MIN_TABLE_GAP:
            raise ValueError("move %d would bring the arm within %.0f mm of the table - refusing (safety)" % (i + 1, g_table))


def work_pose(tf, home):
    """Our gripping start pose: gripper down, TRAVEL_Z above the middle of the table. First choice: the arm shape all
    wall moves were developed and tested in (computed_ready: elbow up, untwisted). In the shape nearest Suzanne's start
    pose some gripping moves turned wrist 1 by 150 deg and brought the gripper into the forearm (28 Oct test), so that
    one only comes after, with equivalent wrist angles; the first with a safe change-over both ways is used."""
    x, y, z = tf.point(0, 300, TRAVEL_Z)
    w = ik_closest(x, y, z, [tf.yaw0 + 90 * k for k in range(4)], home, arm_side(home))
    if w is None:
        raise ValueError("the robot can't reach above the table from the start position's side (calib.json?)")
    options = []
    for w1 in (w[3], w[3] - 360, w[3] + 360):
        for w2 in (w[4], w[4] - 360, w[4] + 360):
            if abs(w1) <= 360 and abs(w2) <= 360:
                options.append(w[:3] + [w1, w2, w[5]])
    options.sort(key=lambda o: sum(abs(a - b) for a, b in zip(o, home)))
    for o in options:
        try:
            transition(home, o, tf)
            transition(o, home, tf)
            return o
        except ValueError:
            continue
    raise ValueError("no safe change-over between the start position and the gripping pose")


def ready_route(q, ready, tf):
    """Menu g: [(what happens, joints)] from joints q to READY: lift the gripper straight up if it is low, then a
    route checked against the arm model (transition). ValueError with what to do if there is no safe way."""
    table = tf.origin[2]
    for i in (1, 2):
        if abs(q[i]) > 180:
            raise ValueError("the %s is turned more than half a turn (%.0f deg): move it back on the pendant "
                             "(Move tab) first, then g again" % (JOINT_NAMES[i], q[i]))
    if abs(q[2]) > ELBOW_LIMIT:
        raise ValueError("the elbow is folded very far (%.0f deg): unfold it on the pendant first, then g again" % q[2])
    steps, cur = [], list(q)
    if tip_from_joints(cur)[0][2] < table + LIFT_ABOVE_TABLE:
        T = kin.forward(cur)
        dz = table + LIFT_ABOVE_TABLE - tip_from_joints(cur)[0][2]
        T[2][3] += dz
        up = kin.inverse(T, cur)
        if up is not None and max(abs(a - b) for a, b in zip(up, cur)) < 60:
            steps.append(("lift the gripper %.0f mm straight up" % dz, up))
            cur = up
    return steps + transition(cur, list(ready), tf)


def cmd_go_ready(args):
    """Menu g: move the real robot to READY by the careful route (ready_route), slowly, after showing the steps."""
    tf = load_calib()
    ready = load_ready(tf)
    if ready is None:
        sys.exit("No READY pose: calib.json missing, or the READY spot is out of the robot's reach.")
    q, _ = read_or_exit()
    if not ready_offsets(q, ready):
        log("the robot is already at READY - nothing to do")
        return
    try:
        steps = ready_route(q, ready, tf)
    except ValueError as e:
        sys.exit("Not moving: " + str(e))
    print("\nGoing to READY (%s), slowly (%.0f deg/s), one step at a time:" % (ready_source(), READY_SPEED))
    for n, (what, _) in enumerate(steps, 1):
        print("   %d. %s" % (n, what))
    if any(w.startswith("turn the gripper") and abs(ready[5] - q[5]) > 180 for w, _ in steps):
        print("\n   (the gripper undoes a full turn on itself: watch its cable)")
    if not args.yes and input("\nMove the REAL robot to READY now? Nothing in the way, e-stop in hand [y/N] ").lower() != "y":
        return
    v, a = math.radians(READY_SPEED), math.radians(JOINT_ACCEL) / 2
    lines = []
    for what, target in steps:
        lines += ['  textmsg("PicoBricks to READY: %s")' % what,
                  "  movej([%s], a=%.3f, v=%.3f)" % (",".join("%.5f" % math.radians(x) for x in target), a, v)]
    # gripper left as it is (opening it could drop a brick it still holds)
    URScriptBackend(True, None).send(lines, open_first=False)


def cmd_show_ready(args):
    """Menu v: show READY on the ghost robot in the headset (nothing real moves)."""
    ready = load_ready()
    if ready is None:
        sys.exit("No READY pose: calib.json missing, or the READY spot is out of the robot's reach.")
    msg = json.dumps({"type": "pose", "frames": [{"j": ready}]}).encode()
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    for target in ["255.255.255.255", "192.168.137.255"] + ([args.headset] if args.headset else []):
        try:
            sock.sendto(msg, (target, PREVIEW_PORT))
        except OSError:
            pass
    log("READY (%s): %s" % (ready_source(), "  ".join("%s %.1f" % (n, v) for n, v in zip(JOINT_NAMES, ready))))
    log("sent to the headset: the ghost robot should now stand in READY (robot mode in the app, same hotspot)")


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
    ready = load_ready()
    if ready is None:
        log("READY pose: none (calib.json missing?)")
    else:
        offs = ready_offsets(q, ready)
        log("READY pose (%s): %s" % (ready_source(),
            "the robot is AT READY" if not offs else "NOT at ready (%s) - menu g" % describe_offsets(offs)))
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
                print("  read: x %.1f  y %.1f  z %.1f mm" % tuple(tip))
                # The robot may not be allowed to touch the table (safety limit / protective stop): then hold the
                # fingertips just above the point and measure the gap with a ruler
                g = input("  gap between fingertips and tabletop in mm (Enter = 0, touching): ").strip().replace(",", ".")
                try:
                    gap = float(g) if g else 0.0
                except ValueError:
                    print("  not a number - try this point again")
                    continue
                tip[2] -= gap
                pts[key] = [round(v, 1) for v in tip]
                if gap:
                    print("  table top here: z %.1f mm" % tip[2])
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


# ---------------- room between bricks (25 Oct: touching bricks got hit while the next one was lowered) ----------------
MIN_GAP = 5.0           # mm: warn below this (the app plans 10 mm; the real robot can be ~8 mm off the ghost)


def brick_outline(c, yaw):
    """Corners of a placed brick on the table (robot frame, mm); its width runs along the tool yaw (top-down grip)."""
    a = math.radians(yaw)
    u, v = (math.cos(a), math.sin(a)), (-math.sin(a), math.cos(a))
    hw, hl = BRICK_W / 2, BRICK_L / 2
    return [(c[0] + su * hw * u[0] + sv * hl * v[0], c[1] + su * hw * u[1] + sv * hl * v[1])
            for su, sv in ((1, 1), (1, -1), (-1, -1), (-1, 1))]


def outline_gap(A, B):
    """Distance between two convex outlines along the best separating edge normal (negative = overlap)."""
    best = -1e9
    for P in (A, B):
        for i in range(4):
            e = (P[(i + 1) % 4][0] - P[i][0], P[(i + 1) % 4][1] - P[i][1])
            n = (-e[1] / math.hypot(*e), e[0] / math.hypot(*e))
            pa = [p[0] * n[0] + p[1] * n[1] for p in A]
            pb = [p[0] * n[0] + p[1] * n[1] for p in B]
            best = max(best, min(pb) - max(pa), min(pa) - max(pb))
    return best


def neighbour_gaps(jobs):
    """[(name, name, gap mm)] for bricks in the same layer that are close to each other."""
    out = []
    for i in range(len(jobs)):
        for j in range(i + 1, len(jobs)):
            ci, cj = jobs[i][4], jobs[j][4]
            if abs(ci[2] - cj[2]) > BRICK_H / 2:
                continue
            g = outline_gap(brick_outline(ci, ci[3]), brick_outline(cj, cj[3]))
            if g < 30:
                out.append((jobs[i][0], jobs[j][0], g))
    return out


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
        frames = frames[1:]
        # Where the hand is at each pose (our UR5 maths), to size the blends
        hand = []
        for f in frames:
            T = kin.forward(f["j"])
            hand.append((T[0][3], T[1][3], T[2][3]))
        lines = []
        for i, f in enumerate(frames):
            q = ",".join("%.5f" % math.radians(x) for x in f["j"])
            nxt = frames[i + 1] if i + 1 < len(frames) else {}
            # Straight down onto the brick and straight up again: ONE movel to the end of that stretch (the robot's
            # own straight line with its TCP), slow, no blend. As joint moves the gripper came down slightly sideways
            # and hit the brick's corner while gripping (28 Oct).
            if f.get("lin"):
                if not f.get("grip") and nxt.get("lin"):
                    continue                                   # in-between pose of the straight stretch
                lines.append("  movel([%s], a=%.3f, v=%.3f, r=0)" % (q, APPROACH_ACCEL / 1000, APPROACH_SPEED / 1000))
                if f.get("grip"):
                    lines += URScriptBackend.grip(f["grip"] == "close")
                continue
            # Blend: flow through in-between poses instead of stopping at each one (stop-go looked jerky, 22 Oct).
            # Stop exactly where the gripper acts, before a straight stretch, and at the end. The blend may cut the
            # corner by at most BLEND_MAX, and never more than 40 % of the distance to the neighbouring poses.
            if f.get("grip") or i == len(frames) - 1 or nxt.get("lin"):
                r = 0.0
            else:
                d_prev = math.dist(hand[i], hand[i - 1]) if i > 0 else 1e9
                d_next = math.dist(hand[i], hand[i + 1])
                r = min(BLEND_MAX, 0.4 * min(d_prev, d_next)) / 1000.0
            lines.append("  movej([%s], a=%.3f, v=%.3f, r=%.4f)" % (q, a, v, r))
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

    def send(self, lines, end_joints=None, open_first=True):
        """end_joints (deg): finish there (READY). The program used to go back to wherever the robot started, which
        carried an odd start pose over to the next run (28 Oct). open_first=False leaves the gripper as it is."""
        end = []
        if end_joints is not None:
            end = ["  movej([%s], a=%.3f, v=%.3f)" % (",".join("%.5f" % math.radians(x) for x in end_joints),
                                                      math.radians(JOINT_ACCEL), math.radians(JOINT_SPEED))]
        # Our tool for this program only, whatever TCP is set on the pendant (Suzanne's disassembly setup uses another
        # one, 28 Oct). The straight moves onto the bricks (movel) follow this TCP; the pendant setting is untouched.
        tcp = ["  set_tcp(p[0, 0, %.4f, 0, 0, 0])" % (TOOL_LENGTH / 1000)]
        prog = ("def picobricks():\n" + "\n".join(tcp + (self.grip(False) if open_first else []) + lines + end) +
                "\n  textmsg(\"picobricks done\")\nend\n")
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
        # Only from READY (checked as exact joint numbers), planned from READY and back to READY (28 Oct)
        start = getattr(self, "start", None)
        if self.real:
            if start is None:
                raise ValueError("couldn't read the robot's pose - not moving")
            ready = require_ready(start)
        else:
            ready = load_ready() or [float(v) for v in HOME_JOINTS]
        frames = preview(jobs, self.tf, ready)
        check_steps([f["j"] for f in frames])
        check_arm(frames, self.tf)
        self.send(self.joint_moves(frames))

    def hover(self, pts):
        # Joint moves from our own UR5 maths, so this also tests that our maths matches the real robot
        # From READY and back to READY, like the walls (28 Oct)
        v, a = math.radians(JOINT_SPEED), math.radians(JOINT_ACCEL)
        try:
            ready = require_ready(read_or_exit()[0]) if self.real else (load_ready() or [float(x) for x in HOME_JOINTS])
        except ValueError as e:
            sys.exit(str(e))
        q = list(ready)
        poses, lines = [q], []
        for p in pts:
            # hovering doesn't care which way the gripper faces: keep the wrist as it is
            q = ik_closest(p[0], p[1], p[2] + 30, [self.tf.yaw0 + 90 * k for k in range(4)], q, arm_side(ready))
            if q is None:
                sys.exit("can't reach above the corner %s - check calib.json" % p)
            poses.append(q)
            lines += ["  movej([%s], a=%.3f, v=%.3f)" % (",".join("%.5f" % math.radians(x) for x in q), a, v),
                      "  sleep(3)"]
        try:
            check_steps(poses + [ready])
        except ValueError as e:
            sys.exit(str(e))
        self.send(lines, end_joints=ready)

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
    """-> "all", "one" (first brick only), "no", or "menu" (stop listening, back to the menu)."""
    if args.yes:
        return "all"
    if args.real:
        a = input("Build on the REAL robot?  y = all %d bricks   1 = first brick only   n = no   m = back to menu\n"
                  "(speed slider low, e-stop in hand): " % n).strip().lower()
        return {"y": "all", "1": "one", "m": "menu"}.get(a, "no")
    # Asking in the simulation too gives the user time to take the headset off and watch the screen (Jin, 19 Oct).
    # Remember: a new wall from the headset is only built after answering here.
    a = input("Run %d bricks in the simulation? [Y/n]  m = back to menu: " % n).strip().lower()
    return {"n": "no", "m": "menu"}.get(a, "all")


def waiting_key():
    """While waiting for a wall (Windows): "m" = back to the menu, "r" = headset back to the opening, else None."""
    try:
        import msvcrt
    except ImportError:
        return None
    hit = None
    while msvcrt.kbhit():
        k = msvcrt.getwch().lower()
        if k in ("m", "r"):
            hit = k
    return hit


# ---------------- ghost preview: the robot's movements as joint angles (own UR5 maths, no RoboDK) ----------------
def arm_yaw(base_deg):
    """Direction the arm points (deg, robot base frame) with base joint `base_deg` and all others 0."""
    a = kin.forward([base_deg, 0, 0, 0, 0, 0])
    b = kin.forward([base_deg, -90, 0, 0, 0, 0])
    return math.degrees(math.atan2(a[1][3] - b[1][3], a[0][3] - b[0][3]))


def preview(jobs, tf, start=None):
    """The same moves as RoboDKBackend.run(), as joint angles for the headset's ghost robot:
    list of {"j": 6 angles in deg, "grip": "close"/"open", "brick": name}."""
    frames = []

    def ik(T, seed):
        x, y, z, yaw = T
        q = ik_closest(x, y, z, (yaw, yaw + 180), seed, side)   # brick and gripper look the same turned half a turn
        if q is None:
            raise ValueError("the robot can't reach x %.0f y %.0f z %.0f mm" % (x, y, z))
        return q

    def add(q, **extra):
        frames.append(dict(j=[round(v, 2) for v in q], **extra))

    def straight(a, b, q, lin=False):            # MoveL: a few poses on the straight line (for the ghost)
        for i in range(1, PREVIEW_STEPS + 1):
            f = i / PREVIEW_STEPS
            q = ik([a[k] + (b[k] - a[k]) * f for k in range(4)], q)
            if lin:
                add(q, lin=True)                 # the real robot does this bit as ONE true straight move (movel)
            else:
                add(q)
        return q

    # Start and end at READY (start), so the ghost and the robot begin in the same, known pose. Every pose is planned
    # from the one before, so joints never jump by a turn. (Until 28 Oct the first pose was planned from HOME_JOINTS
    # while the robot could be anywhere: a joint at +300 deg was sent to -60 deg and the arm spun into itself.)
    # (28 Oct) READY is the start position shared with Suzanne: the program changes over from there into our
    # gripping shape by a route checked against the arm model, and back at the end.
    home = [float(v) for v in (start if start else (load_ready(tf) or HOME_JOINTS))]
    side = arm_side(home)                         # one arm shape for the whole program (no side switches)
    q = list(home)
    add(q)

    def go_along(steps):                          # change-over: in steps of <= 20 deg (ghost and robot follow them)
        nonlocal q
        for _, target in steps:
            n = max(1, int(math.ceil(max(abs(a - b) for a, b in zip(q, target)) / 20.0)))
            a = list(q)
            for k in range(1, n + 1):
                q = [x + (y - x) * k / n for x, y in zip(a, target)]
                add(q)

    work = work_pose(tf, home)
    go_along(transition(home, work, tf))
    q = list(work)
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
                q = straight(up, at, q, lin=True)       # straight down onto the brick
                frames[-1].update(grip=grip, brick=name)
                q = straight(at, up, q, lin=True)       # straight up again
                q = straight(up, travel, q)
            except ValueError:
                where = "in the tray" if grip == "close" else "in the wall"
                dist = math.hypot(x, y)
                why = "too close to its base" if dist < 250 else "too far away"
                raise ValueError("the robot can't reach %s %s (%.0f cm from the robot: %s)"
                                 % (name, where, dist / 10, why))
    # Back above the middle of the table with the wrist where it is now (jumping to the exact start-of-program wrist
    # angle turned wrist 3 by 218 deg in one go, 28 Oct), then the checked change-over home in steps of <= 20 deg
    last = list(q)
    q = ik_closest(*tf.point(0, 300, TRAVEL_Z), [tf.yaw0 + 90 * k for k in range(4)], q, side) or q
    if max(abs(a - b) for a, b in zip(q, last)) > 0.5:
        add(q)
    go_along(transition(q, home, tf))
    return frames


def send_preview(layout, tf, jobs, backend, sock, addr, start=None):
    """Work out the robot's movements and send them to the headset, where the ghost UR5 plays them."""
    msg = {"type": "preview", "seq": layout.get("seq", 0)}
    try:
        msg["frames"] = preview(jobs, tf, start)
        log("sent the robot movements to the headset (%d poses) - watch the ghost robot" % len(msg["frames"]))
    except Exception as e:
        msg["error"] = str(e)
        log("!! preview failed:", e)
    try:
        sock.sendto(json.dumps(msg).encode(), (addr, PREVIEW_PORT))
    except OSError as e:
        log("!! couldn't send the preview to the headset:", e)


def say(sock, addr, key):
    """Ask the headset to play a voice line (VoiceGuide key, Assets/Resources/Voice/<key>)."""
    try:
        sock.sendto(json.dumps({"type": "say", "key": key}).encode(), (addr, PREVIEW_PORT))
    except OSError as e:
        log("!! couldn't reach the headset:", e)


def restart_headset(sock, addr):
    """R while waiting: the headset goes back to the opening (next visitor). Straight to the last headset that sent a
    wall, and as a broadcast on the hotspot in case none did yet."""
    msg = json.dumps({"type": "restart"}).encode()
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    for target in ([addr] if addr else []) + ["255.255.255.255", "192.168.137.255"]:
        try:
            sock.sendto(msg, (target, PREVIEW_PORT))
        except OSError:
            pass
    log("headset: back to the opening")


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
    tight = [g for g in neighbour_gaps(jobs) if g[2] < MIN_GAP]
    if tight:
        log("!! %d pair(s) of bricks closer than %.0f mm (e.g. %s - %s: %.1f mm): the robot may hit a placed brick. "
            "Rebuild the headset app (it plans 10 mm gaps) before building on the real robot."
            % (len(tight), MIN_GAP, tight[0][0], tight[0][1], tight[0][2]))
    # The ghost and the robot's program start and end at READY. Real robot: read where it is now; it only builds
    # from READY (URScriptBackend.run checks again right before moving)
    start = load_ready()
    if args.real and isinstance(backend, URScriptBackend):
        backend.start = None
        try:
            backend.start, _tcp = read_robot_state()
            if start is None:
                log("!! no READY pose (calib.json?): the real robot will NOT move")
            else:
                offs = ready_offsets(backend.start, start)
                log("robot is at READY" if not offs else
                    "!! robot is NOT at READY (%s): menu g first - the real robot will NOT move" % describe_offsets(offs))
        except OSError as e:
            log("!! couldn't read the robot's pose (%s): the real robot will NOT move" % e)
    if sock is not None and addr:
        send_preview(layout, tf, jobs, backend, sock, addr, start)
    if isinstance(backend, GhostBackend):
        return                                    # preview only, nothing else moves
    choice = confirm(args, len(jobs))
    if choice == "menu":
        return "menu"
    if choice == "one" and len(jobs) > 1:
        jobs = jobs[:1]
        log("ONE BRICK: only %s is built" % jobs[0][0])
    if choice in ("all", "one"):
        if args.real and sock is not None and addr:
            # Warn the visitor in the headset ("Caution, the physical UR5 will now move") and give it time to play
            say(sock, addr, "real_start")
            time.sleep(3.0)
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
    log("(press M = back to the menu, R = headset back to the opening for the next visitor)")
    last_key = None
    headset = None
    # Wait in 1-second steps: a plain wait doesn't notice Ctrl+C on Windows until a message arrives
    sock.settimeout(1.0)
    while True:
        try:
            data, addr = sock.recvfrom(65535)
        except socket.timeout:
            key = waiting_key()
            if key == "m":
                log("back to the menu")
                return
            if key == "r":
                restart_headset(sock, headset)
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
        # Each wall arrives twice (direct + broadcast). The copy can sit in the queue while the bridge waits for
        # "y" and arrive long after the first one (it replayed the ghost, 22 Oct), so copies are recognised by their
        # number AND content, however late. (The headset restarts its numbering when the app restarts, but a new
        # wall then has different bricks.)
        key = (layout.get("seq"), json.dumps(layout.get("bricks"), sort_keys=True))
        if key == last_key:
            continue
        last_key = key
        log("from headset", addr[0])
        headset = addr[0]
        with open(os.path.join(HERE, "last_layout.json"), "w") as f:
            json.dump(layout, f, indent=2)       # replay later with --file last_layout.json
        if handle(layout, tf, backend, args, sock, addr[0]) == "menu":
            log("back to the menu")
            return
        log("waiting for the next wall from the headset (M = back to the menu, R = headset back to the opening)")


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
        "ready-save": sub.add_parser("ready-save", help="the robot's current pose becomes READY (nothing moves)"),
        "go-ready": sub.add_parser("go-ready", help="move the real robot slowly to READY"),
        "show-ready": sub.add_parser("show-ready", help="show READY on the ghost robot in the headset"),
    }
    parsers["show-ready"].add_argument("--headset", help="headset IP (default: broadcast on the hotspot)")
    for name in ("ready-save", "go-ready"):
        parsers[name].add_argument("--yes", action="store_true", help="don't ask")
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
    {"calib": cmd_calib, "run": cmd_run, "corners": cmd_corners, "check": cmd_check, "grip": cmd_grip,
     "ready-save": cmd_ready_save, "go-ready": cmd_go_ready, "show-ready": cmd_show_ready}[args.cmd](args)


if __name__ == "__main__":
    main()
