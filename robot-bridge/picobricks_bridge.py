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

Needs:  python -m pip install robodk     (only for --backend robodk, the default)
"""
import argparse
import json
import math
import os
import socket
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
CALIB_FILE = os.path.join(HERE, "calib.json")

# ======================= settings you may want to change =======================
ROBOT_IP = "192.168.1.198"
UDP_PORT = 5005

# Gripper (soft/parallel gripper picking the brick from the top)
GRIP_DO = 0             # standard digital output that closes the gripper
GRIP_CLOSE_VALUE = 1    # value that CLOSES it (flip to 0 if yours is the other way round)
GRIP_WAIT_S = 0.8       # time for the gripper to close / open
GRASP_DEPTH = 25.0      # mm: how far below the brick's top face the TCP goes (0 = suction on top)
TOOL_YAW_OFFSET = 0.0   # deg: add 90 if the fingers close along the brick's length instead of across it
TOOL_LENGTH = 150.0     # mm flange -> TCP; only used to add a tool in RoboDK if the station has none

BRICK_W, BRICK_L, BRICK_H = 50.0, 100.0, 50.0   # mm
APPROACH = 80.0         # mm: go straight down/up this last bit with MoveL
TRAVEL_Z = 250.0        # mm above the table top for moves between pick and place
PLACE_CLEARANCE = 2.0   # mm: release this far above the exact place height
SPEED = 100.0           # mm/s linear (first tests: slow)
JOINT_SPEED = 30.0      # deg/s
HOME_JOINTS = [0, -90, -90, -90, 90, 0]   # simulation only: a tool-down "ready" pose
UR5_REACH = 850.0       # mm: refuse targets further than this from the base
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


def load_calib():
    if not os.path.exists(CALIB_FILE):
        sys.exit("No calib.json yet. Run:  python picobricks_bridge.py calib")
    with open(CALIB_FILE) as f:
        c = json.load(f)
    return TableFrame(c["front_left_mm"], c["front_right_mm"])


def cmd_calib(_args):
    print("Jog the TCP (gripper fingertips) onto each corner of the table TOP, then read X Y Z\n"
          "on the pendant: Move tab, Feature = BASE, mm. Same corners as the headset calibration.\n")
    pts = {}
    for key, label in (("front_left_mm", "FRONT-LEFT"), ("front_right_mm", "FRONT-RIGHT")):
        while True:
            s = input("%s corner  X Y Z (mm, spaces): " % label).replace(",", " ").split()
            try:
                pts[key] = [float(v) for v in s[:3]]
                if len(pts[key]) == 3:
                    break
            except ValueError:
                pass
            print("  need three numbers, e.g.  -350.2 -420.0 12.5")
    with open(CALIB_FILE, "w") as f:
        json.dump(pts, f, indent=2)
    tf = TableFrame(pts["front_left_mm"], pts["front_right_mm"])
    log("saved %s - front edge %.0f mm wide (tape-measure it!), table yaw %.1f deg" % (CALIB_FILE, tf.width, tf.yaw0))


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
    def box(self, name):
        hx, hy, hz = BRICK_W / 2, BRICK_L / 2, BRICK_H / 2
        v = [[sx * hx, sy * hy, sz * hz] for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
        faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
        tris = []
        for a, b, c, d in faces:
            tris += [v[a], v[b], v[c], v[a], v[c], v[d]]
        obj = self.RDK.AddShape(tris)
        obj.setName(name)
        obj.setColor([0.62, 0.42, 0.24, 1.0])
        obj.setParent(self.base)
        return obj

    def show_bricks(self, jobs):
        for it in self.RDK.ItemList(self.rl.ITEM_TYPE_OBJECT) + self.RDK.ItemList(self.rl.ITEM_TYPE_TARGET):
            if it.Name().startswith("PB_"):
                it.Delete()
        for name, P, Q, pc, qc in jobs:
            obj = self.box("PB_" + name)
            obj.setPose(self.rm.transl(pc[0], pc[1], pc[2]) * self.rm.rotz(math.radians(pc[3])))
            self.bricks[name] = obj
            self.RDK.AddTarget("PB_place_" + name, self.base).setPose(self.pose(*Q))

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

    def run(self, jobs):
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

    def grip(self, close):
        val = "True" if (close == bool(GRIP_CLOSE_VALUE)) else "False"
        return ["  set_standard_digital_out(%d, %s)" % (GRIP_DO, val), "  sleep(%.2f)" % GRIP_WAIT_S]

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
        lines = []
        for _n, P, Q, _, _ in jobs:
            lines += self.down_up(P, True) + self.down_up(Q, False)
        self.send(lines)

    def hover(self, pts):
        lines = []
        for p in pts:
            lines += ["  movej(%s, a=0.8, v=0.5)" % self.p(p[0], p[1], p[2] + 30, self.tf.yaw0), "  sleep(2)"]
        self.send(lines)


class PrintBackend:
    def __init__(self, real, tf):
        pass

    def run(self, jobs):
        log("(print backend: nothing moves)")

    def hover(self, pts):
        for p in pts:
            log("would hover 30 mm above", ["%.1f" % v for v in p])


BACKENDS = {"robodk": RoboDKBackend, "urscript": URScriptBackend, "print": PrintBackend}


# ---------------- commands ----------------
def confirm(args, n):
    if args.yes:
        return True
    if args.real:
        return input("Build %d bricks on the REAL robot? Speed slider low, e-stop in hand. [y/N] " % n).strip().lower() == "y"
    return input("Run %d bricks in the simulation? [Y/n] " % n).strip().lower() != "n"


def handle(layout, tf, backend, args):
    jobs = plan(layout, tf, args)
    log("layout seq %s: %d bricks received, %d to build" % (layout.get("seq"), len(layout.get("bricks", [])), len(jobs)))
    if not jobs:
        return
    print_jobs(jobs)
    if confirm(args, len(jobs)):
        try:
            backend.run(jobs)
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
    last_seq = None
    while True:
        data, addr = sock.recvfrom(65535)
        try:
            layout = json.loads(data.decode())
        except ValueError:
            log("ignored junk from", addr)
            continue
        if layout.get("seq") == last_seq:          # same message via direct + broadcast
            continue
        last_seq = layout.get("seq")
        log("from headset", addr[0])
        with open(os.path.join(HERE, "last_layout.json"), "w") as f:
            json.dump(layout, f, indent=2)       # replay later with --file last_layout.json
        handle(layout, tf, backend, args)


def cmd_corners(args):
    tf = load_calib()
    with open(CALIB_FILE) as f:
        c = json.load(f)
    if args.real and not args.yes and input("Hover 30 mm above both corners on the REAL robot? [y/N] ").lower() != "y":
        return
    BACKENDS[args.backend](args.real, tf).hover([c["front_left_mm"], c["front_right_mm"]])


def main():
    ap = argparse.ArgumentParser(description="PicoBricks headset -> UR5")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("calib", help="type the two corner coordinates from the pendant")
    for name in ("run", "corners"):
        s = sub.add_parser(name)
        s.add_argument("--backend", choices=BACKENDS, default="robodk")
        s.add_argument("--real", action="store_true", help="move the real robot (default: simulation / dry run)")
        s.add_argument("--yes", action="store_true", help="don't ask before moving")
        if name == "run":
            s.add_argument("--file", help="use a saved layout instead of listening for the headset")
            s.add_argument("--grid", type=float, default=0, help="snap x/y to this grid in mm (e.g. 50); 0 = off")
            s.add_argument("--snap-rot", action="store_true", help="snap yaw to 90 deg (hand-placed bricks)")
    args = ap.parse_args()
    {"calib": cmd_calib, "run": cmd_run, "corners": cmd_corners}[args.cmd](args)


if __name__ == "__main__":
    main()
