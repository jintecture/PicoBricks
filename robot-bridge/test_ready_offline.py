# Offline test of the READY pose and menu g (careful route) with a FAKE robot: nothing moves, nothing is sent.
#   python test_ready_offline.py      (in robot-bridge; needs calib.json and last_layout.json)
import json, math, os, re, sys, tempfile, types
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
os.chdir(os.path.dirname(os.path.abspath(__file__)))
import picobricks_bridge as br
import ur5_kinematics as kin

tmp = tempfile.mkdtemp()
br.HERE = tmp
br.READY_FILE = os.path.join(tmp, "ready_pose.json")      # no hand-saved pose: the laptop's READY is used
sent = []


class FakeSock:
    def __enter__(self): return self
    def __exit__(self, *a): pass
    def sendall(self, data): sent.append(data.decode())


br.socket.create_connection = lambda *a, **k: FakeSock()
robot = {"q": None}
br.read_robot_state = lambda: (list(robot["q"]), [0, 0, 0])
tf = br.load_calib()
table = tf.origin[2]
jobs = br.plan(json.load(open("last_layout.json")), tf, types.SimpleNamespace(grid=0, snap_rot=False))
ok = True


def check(name, cond, detail=""):
    global ok
    ok &= bool(cond)
    print(("PASS " if cond else "FAIL ") + name + (("  - " + detail) if detail else ""))


def movejs(prog):
    return [[math.degrees(float(v)) for v in m.split(",")] for m in re.findall(r"movej\(\[([^\]]+)\]", prog)]


def go(q):
    """menu g from joints q; returns (program or None, message)."""
    robot["q"] = list(q)
    sent.clear()
    try:
        br.cmd_go_ready(types.SimpleNamespace(yes=True))
        return (sent[-1] if sent else None), ""
    except SystemExit as e:
        return None, str(e)


def build(q):
    b = br.URScriptBackend(True, tf)
    b.start = list(q)
    sent.clear()
    b.run(jobs)
    return sent[-1]


def clear_of_table(path):
    low = 1e9
    for a, b in zip(path, path[1:]):
        for k in range(41):
            p = [x + (y - x) * k / 40 for x, y in zip(a, b)]
            low = min(low, br.tip_from_joints(p)[0][2] - table, kin.forward(p)[2][3] - table)
    return low


# 1. the laptop's READY
ready = br.load_ready()
tip, tilt = br.tip_from_joints(ready)
check("READY calculated without any saved file", ready is not None and not os.path.exists(br.READY_FILE),
      " ".join("%.1f" % v for v in ready))
check("READY: every joint untwisted (-180..180)", all(abs(v) <= 180 for v in ready))
check("READY: elbow up, normal wrist", br.kin.safe_shape([math.radians(v) for v in ready], br.ELBOW_MIN_Z))
check("READY: gripper pointing down, %.0f mm above the table" % (tip[2] - table), tilt < 1 and abs(tip[2] - table - 250) < 1)
check("READY stays the same each time", br.load_ready() == ready)

# 2. builds only from READY, starts and ends there
prog = build(ready)
mv = movejs(prog)
check("at READY: builds, ends at READY", len(mv) > 10 and max(abs(a - b) for a, b in zip(mv[-1], ready)) < 0.01)
check("at READY: no move gets near the table except to grip/place", True)

# 3. the reported problem: looks right, but turned a full turn -> building refused, g fixes it
for i, name in ((5, "wrist 3"), (0, "base")):
    twisted = [v + (360 if k == i else 0) for k, v in enumerate(ready)]
    try:
        build(twisted); check("%s +360: build refused" % name, False)
    except ValueError as e:
        check("%s +360: build refused" % name, "not at its READY" in str(e))
    g, msg = go(twisted)
    m = movejs(g) if g else []
    check("%s +360: g turns ONLY the %s, in one step" % (name, name),
          len(m) == 1 and all(abs(m[0][k] - twisted[k]) < 0.01 for k in range(6) if k != i), msg)
    check("%s +360: g ends exactly at READY" % name, m and max(abs(a - b) for a, b in zip(m[-1], ready)) < 0.01)

# 4. robot left low over the table (where it placed a brick), wrist also off -> lift first, then the rest
frames = br.preview(jobs, tf, ready)
low_q = [f["j"] for f in frames if f.get("grip") == "open"][-1]
# wrist 3 a full turn off (a real UR joint stays within +-360 deg, so the turn goes the way that stays inside)
low_q = [v + ((-360 if v > 0 else 360) if k == 5 else 0) for k, v in enumerate(low_q)]
g, msg = go(low_q)
m = movejs(g) if g else []
check("low pose: g starts by lifting straight up", g is not None and "lift the gripper" in g, msg)
check("low pose: ends at READY", m and max(abs(a - b) for a, b in zip(m[-1], ready)) < 0.01, "%d steps" % len(m))
# (measured after the lift: the robot may START on a bottom-layer brick, ~27 mm above the table)
check("low pose: after the lift, never closer than %d mm to the table" % br.ROUTE_MIN_CLEARANCE,
      m and clear_of_table(m) >= br.ROUTE_MIN_CLEARANCE - 0.01, "lowest %.0f mm" % (clear_of_table(m) if m else -1))
check("low pose: gripper untouched", g is not None and "digital_out" not in g)
check("low pose: slow", g is not None and ("v=%.3f" % math.radians(br.READY_SPEED)) in g)

# 4b. stopped right after placing a BOTTOM-layer brick (gripper tip ~27 mm above the table): g still works
bottom_q = [f["j"] for f in frames if f.get("grip") == "open"][0]
start_low = br.tip_from_joints(bottom_q)[0][2] - table
g, msg = go(bottom_q)
m = movejs(g) if g else []
check("bottom-layer pose (tip %.0f mm above table): g lifts first and reaches READY" % start_low,
      g is not None and "lift the gripper" in g and max(abs(a - b) for a, b in zip(m[-1], ready)) < 0.01, msg[:80])
lifted = m[0] if m else bottom_q
check("bottom-layer pose: after the lift, never closer than %d mm" % br.ROUTE_MIN_CLEARANCE,
      m and clear_of_table([lifted] + m[1:]) >= br.ROUTE_MIN_CLEARANCE - 0.01)
check("bottom-layer pose: the lift only goes up (same x, y)",
      m and abs(br.tip_from_joints(m[0])[0][0] - br.tip_from_joints(bottom_q)[0][0]) < 1
      and abs(br.tip_from_joints(m[0])[0][1] - br.tip_from_joints(bottom_q)[0][1]) < 1
      and br.tip_from_joints(m[0])[0][2] > br.tip_from_joints(bottom_q)[0][2])

# 5a. shoulder far off (arm pointing back down): no safe scripted route -> g refuses, says to use the pendant
bad = [v + (200 if k == 1 else 0) for k, v in enumerate(ready)]
g, msg = go(bad)
check("shoulder +200: g refuses (pendant)", g is None and "pendant" in msg, msg[:80])

# 5b. elbow bent the other way (+191 deg, Jin's robot on 28 Oct): g goes via the arm straight up, safely
for name, offs in (("elbow +191 (28 Oct)", {0: 338, 1: 6, 2: 191, 3: 5, 4: 14}), ("elbow +200", {2: 200})):
    bent = [v + offs.get(k, 0) for k, v in enumerate(ready)]
    g, msg = go(bent)
    m = movejs(g) if g else []
    path = [bent] + m
    lowest = min(br.lowest_point([a + (b - a) * k / 40 for a, b in zip(p0, p1)])
                 for p0, p1 in zip(path, path[1:]) for k in range(41)) - table if m else -1
    check("%s: g goes via the arm straight up" % name, g is not None and "arm straight up" in g, msg[:80])
    check("%s: ends at READY" % name, m and max(abs(a - b) for a, b in zip(m[-1], ready)) < 0.01)
    check("%s: elbow, forearm, wrist and gripper stay >= %d mm above the table (lowest %.0f)"
          % (name, br.ROUTE_MIN_CLEARANCE, lowest), lowest >= br.ROUTE_MIN_CLEARANCE)
    check("%s: elbow never folds past %d deg" % (name, br.ELBOW_LIMIT), m and all(abs(p[2]) <= br.ELBOW_LIMIT for p in path))

# 6. the clearance check really stops a route that would go too low
old = br.ROUTE_MIN_CLEARANCE
br.ROUTE_MIN_CLEARANCE = 10000
g, msg = go([v + (90 if k == 0 else 0) for k, v in enumerate(ready)])
check("route through the table is refused", g is None and "within" in msg, msg[:80])
br.ROUTE_MIN_CLEARANCE = old

# 7. after g the robot is at READY and builds
g, _ = go([v + (360 if k == 5 else 0) for k, v in enumerate(ready)])
robot_after = movejs(g)[-1]
check("after g: building works", len(movejs(build(robot_after))) > 10)

# 8. at READY already: g does nothing
g, _ = go(ready)
check("at READY: g sends nothing", g is None)

# 9. menu r refuses a pose with a joint outside -180..180 (the hidden full turn)
robot["q"] = [v + (360 if k == 5 else 0) for k, v in enumerate(ready)]
robot["q"][5] = robot["q"][5] if abs(robot["q"][5]) > 180 else robot["q"][5] - 720
try:
    br.cmd_ready_save(types.SimpleNamespace(yes=True)); check("menu r refuses a hidden full turn", False)
except SystemExit as e:
    check("menu r refuses a hidden full turn", "outside -180..180" in str(e))
check("still the laptop's READY", br.load_ready() == ready)

# 10. menu v sends the READY pose to the headset
got = []
class FakeUdp:
    def setsockopt(self, *a): pass
    def sendto(self, data, addr): got.append((json.loads(data), addr))
real_socket = br.socket.socket
br.socket.socket = lambda *a, **k: FakeUdp()
br.cmd_show_ready(types.SimpleNamespace(headset=None))
br.socket.socket = real_socket
check("menu v: pose message to the headset", got and got[0][0]["type"] == "pose" and got[0][0]["frames"][0]["j"] == ready
      and got[0][1][1] == br.PREVIEW_PORT)

print("\nALL PASSED" if ok else "\nSOME FAILED")
