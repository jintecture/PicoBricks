r"""
UR5 joint maths, without RoboDK (plain Python, no extra libraries)
=================================================================
forward(joints)          -> where the flange is for 6 joint angles
inverse(pose, seed)      -> the 6 joint angles that put the flange there (the solution closest to `seed`)

Why: the ghost UR5 in the headset plays these joint angles before the real robot moves, so the user sees exactly
what the robot will do. RoboDK can do this too, but its free licence refuses most API calls (19 Oct).

Units: millimetres and degrees. Frame: the UR5's own base frame (the standard "DH" frame RoboDK uses too:
with all joints at 0 the arm lies flat along -x). Joint order: base, shoulder, elbow, wrist 1, wrist 2, wrist 3.
Method: the standard closed-form solution for UR robots (K. Hawkins, "Analytic Inverse Kinematics for the
Universal Robots UR-5/UR-10 Arms", 2013): up to 8 solutions per pose; we keep the one closest to the previous
pose, so the arm moves smoothly instead of flipping its elbow or wrist.
"""
import math

# UR5 Denavit-Hartenberg parameters (Universal Robots), mm
D1, A2, A3, D4, D5, D6 = 89.159, -425.0, -392.25, 109.15, 94.65, 82.3
_D = (D1, 0.0, 0.0, D4, D5, D6)
_A = (0.0, A2, A3, 0.0, 0.0, 0.0)
_ALPHA = (math.pi / 2, 0.0, 0.0, math.pi / 2, -math.pi / 2, 0.0)


# ---------------- 4x4 matrices as lists of rows ----------------
def mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def inv(m):
    """Inverse of a rotation + translation matrix."""
    r = [[m[j][i] for j in range(3)] for i in range(3)]
    t = [-sum(r[i][k] * m[k][3] for k in range(3)) for i in range(3)]
    return [r[0] + [t[0]], r[1] + [t[1]], r[2] + [t[2]], [0.0, 0.0, 0.0, 1.0]]


def transl(x, y, z):
    return [[1, 0, 0, x], [0, 1, 0, y], [0, 0, 1, z], [0, 0, 0, 1.0]]


def rotz(a):
    c, s = math.cos(a), math.sin(a)
    return [[c, -s, 0, 0], [s, c, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1.0]]


def rotx(a):
    c, s = math.cos(a), math.sin(a)
    return [[1, 0, 0, 0], [0, c, -s, 0], [0, s, c, 0], [0, 0, 0, 1.0]]


def _link(i, theta):
    """DH transform from joint i to joint i+1."""
    ct, st = math.cos(theta), math.sin(theta)
    ca, sa = math.cos(_ALPHA[i]), math.sin(_ALPHA[i])
    return [[ct, -st * ca, st * sa, _A[i] * ct],
            [st, ct * ca, -ct * sa, _A[i] * st],
            [0.0, sa, ca, _D[i]],
            [0.0, 0.0, 0.0, 1.0]]


# ---------------- forward: joints -> flange ----------------
def forward(joints_deg):
    """Flange pose (4x4, mm) in the robot base frame for 6 joint angles in degrees."""
    m = transl(0, 0, 0)
    for i, q in enumerate(joints_deg):
        m = mul(m, _link(i, math.radians(q)))
    return m


# ---------------- inverse: flange -> joints ----------------
def _wrap(a):
    return (a + math.pi) % (2 * math.pi) - math.pi


def solutions(flange):
    """All joint solutions (radians, up to 8) that put the flange at this 4x4 pose."""
    T = flange
    out = []
    # wrist centre: back from the flange along its z axis
    p5x = T[0][3] - D6 * T[0][2]
    p5y = T[1][3] - D6 * T[1][2]
    r = math.hypot(p5x, p5y)
    if r < abs(D4):
        return out                                   # too close to the base axis
    phi1 = math.atan2(p5y, p5x)
    phi2 = math.acos(D4 / r)
    for s1 in (1, -1):
        q1 = phi1 + s1 * phi2 + math.pi / 2
        c1, si1 = math.cos(q1), math.sin(q1)
        c5 = (T[0][3] * si1 - T[1][3] * c1 - D4) / D6
        if abs(c5) > 1 + 1e-9:
            continue
        c5 = max(-1.0, min(1.0, c5))
        for s5 in (1, -1):
            q5 = s5 * math.acos(c5)
            sn5 = math.sin(q5)
            Ti = inv(T)                              # base seen from the flange
            if abs(sn5) < 1e-9:
                q6 = 0.0                             # wrist straight: any wrist 3 angle works
            else:
                # x and y axes of the base frame, seen from the flange (columns of the inverse)
                q6 = math.atan2((-Ti[1][0] * si1 + Ti[1][1] * c1) / sn5,
                                (Ti[0][0] * si1 - Ti[0][1] * c1) / sn5)
            T14 = mul(mul(inv(_link(0, q1)), T), inv(mul(_link(4, q5), _link(5, q6))))
            # joint 4's origin, seen from joint 1's frame, then the elbow from the triangle shoulder-elbow-wrist
            p13 = mul(T14, transl(0, -D4, 0))
            px, py = p13[0][3], p13[1][3]
            dist2 = px * px + py * py
            c3 = (dist2 - A2 * A2 - A3 * A3) / (2 * A2 * A3)
            if abs(c3) > 1 + 1e-9:
                continue                             # out of reach
            c3 = max(-1.0, min(1.0, c3))
            for s3 in (1, -1):
                q3 = s3 * math.acos(c3)
                q2 = -math.atan2(py, -px) + math.asin(A3 * math.sin(q3) / math.sqrt(dist2))
                T13 = mul(_link(1, q2), _link(2, q3))
                T34 = mul(inv(T13), T14)
                q4 = math.atan2(T34[1][0], T34[0][0])
                out.append([_wrap(q) for q in (q1, q2, q3, q4, q5, q6)])
    return out


def inverse(flange, seed_deg):
    """Joint angles (degrees) closest to seed_deg that reach this flange pose, or None if unreachable.
    Each joint is shifted by whole turns to be as close to the seed as possible (UR joints turn +-360 deg)."""
    best, best_cost = None, None
    for sol in solutions(flange):
        q = []
        for a, s in zip(sol, seed_deg):
            d = math.degrees(a)
            d += 360 * round((s - d) / 360)
            if d > 360:
                d -= 360
            if d < -360:
                d += 360
            q.append(d)
        cost = sum((a - s) ** 2 for a, s in zip(q, seed_deg))
        if best is None or cost < best_cost:
            best, best_cost = q, cost
    return best


def tcp_pose(x, y, z, yaw_deg):
    """Tool pointing straight down at (x, y, z), turned by yaw (same as the bridge's RoboDK pose)."""
    return mul(mul(transl(x, y, z), rotz(math.radians(yaw_deg))), rotx(math.pi))


if __name__ == "__main__":
    # Self-test: random joint angles -> forward -> inverse must land on the same flange pose
    import random
    random.seed(1)
    worst = 0.0
    for _ in range(2000):
        q = [random.uniform(-180, 180) for _ in range(6)]
        T = forward(q)
        sol = inverse(T, q)
        if sol is None:
            print("no solution for", [round(v) for v in q])
            continue
        T2 = forward(sol)
        err = max(abs(T[i][j] - T2[i][j]) * (1 if j == 3 else 1000) for i in range(3) for j in range(4))
        worst = max(worst, err)
    print("worst error over 2000 random poses: %.6f mm" % worst)
    z = forward([0] * 6)
    print("all joints 0: flange at x %.1f y %.1f z %.1f mm" % (z[0][3], z[1][3], z[2][3]))
