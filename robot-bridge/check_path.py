"""Safety check of a wall before the real robot builds it (nothing moves):
  - the gap between neighbouring bricks in the same layer (they need room: the real robot can be ~8 mm off the
    ghost, and touching bricks get hit while the next one is lowered; 0 mm gaps made the robot collide, 25 Oct)
  - how far the tool dips below the straight line between the planned poses (joint moves can swing low)
   python check_path.py last_layout.json"""
import json, sys, types
import picobricks_bridge as br
import ur5_kinematics as kin


def tcp(q):
    T = kin.mul(kin.forward(q), kin.transl(0, 0, br.TOOL_LENGTH))
    return T[0][3], T[1][3], T[2][3]


def main(path):
    layout = json.load(open(path))
    tf = br.load_calib()
    jobs = br.plan(layout, tf, types.SimpleNamespace(grid=0, snap_rot=False))
    gaps = br.neighbour_gaps(jobs)
    for a, b, g in gaps:
        print("  %s - %s: %.1f mm apart" % (a, b, g))
    frames = br.preview(jobs, tf)
    dip = 0.0
    for a, b in zip(frames, frames[1:]):
        pa, pb = tcp(a["j"]), tcp(b["j"])
        for k in range(1, 40):
            f = k / 40
            p = tcp([x + (y - x) * f for x, y in zip(a["j"], b["j"])])
            dip = max(dip, pa[2] + (pb[2] - pa[2]) * f - p[2])
    smallest = min((g for _, _, g in gaps), default=None)
    print("%d bricks; smallest gap between neighbours: %s; deepest dip of the tool between poses: %.0f mm"
          % (len(jobs), "-" if smallest is None else "%.1f mm" % smallest, dip))
    if smallest is not None and smallest < br.MIN_GAP:
        print("!! bricks are closer than %.0f mm: the robot may hit a placed brick. Rebuild the app (gap 10 mm)."
              % br.MIN_GAP)


main(sys.argv[1] if len(sys.argv) > 1 else "last_layout.json")
