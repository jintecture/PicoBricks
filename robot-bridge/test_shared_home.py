# Offline test of the shared start position (Suzanne's, ready_pose.json) with a FAKE robot: nothing moves.
#   python test_shared_home.py      (in robot-bridge; needs calib.json, ready_pose.json, last_layout.json)
# Checks the whole wall program (change-over from the start position, every gripping move, change-over back) and
# menu g from several poses against the arm model (gripper vs own arm, arm vs table) and the joint-step limits.
import json, math, os, re, sys, tempfile, types
os.chdir(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.getcwd())
import picobricks_bridge as br

tf = br.load_calib()
table = tf.origin[2]
home = br.load_ready(tf)
ok = True


def check(name, cond, detail=""):
    global ok
    ok &= bool(cond)
    print(("PASS " if cond else "FAIL ") + name + (("  - " + detail) if detail else ""))


print("start position:", home)
check("start position is Suzanne's", home == [103.31, -98.91, 88.88, 190.03, -103.31, 0.0])

# the program for the last headset wall, and for the 6-brick test wall
for layout_file in ("last_layout.json", "test_wall_lama.json"):
    jobs = br.plan(json.load(open(layout_file)), tf, types.SimpleNamespace(grid=0, snap_rot=False))
    frames = br.preview(jobs, tf, home)
    J = [f["j"] for f in frames]
    check("%s: starts and ends at the start position" % layout_file,
          max(abs(a - b) for a, b in zip(J[0], home)) < 0.01 and max(abs(a - b) for a, b in zip(J[-1], home)) < 0.01)
    try:
        br.check_steps(J)
        check("%s: no joint jumps (safety net)" % layout_file, True)
    except ValueError as e:
        check("%s: no joint jumps (safety net)" % layout_file, False, str(e))
    check("%s: all joints within +-360" % layout_file, all(abs(v) <= 360 for q in J for v in q))
    # gripper vs own arm on EVERY move; table clearance on every move except the gripping stretches (near a brick)
    worst_arm, worst_table, where = 1e9, 1e9, ""
    for i, (a, b) in enumerate(zip(J, J[1:])):
        g_arm, g_table = br.move_gaps(a, b, table, 20)
        if g_arm < worst_arm:
            worst_arm, where = g_arm, "move %d" % (i + 1)
        if not (frames[i].get("lin") or frames[i + 1].get("lin") or frames[i].get("grip") or frames[i + 1].get("grip")):
            worst_table = min(worst_table, g_table)
    check("%s: gripper never closer than %d mm to the robot's own arm" % (layout_file, br.MIN_ARM_GAP),
          worst_arm >= br.MIN_ARM_GAP, "closest %.0f mm (%s)" % (worst_arm, where))
    check("%s: arm >= %d mm above the table outside the gripping stretches" % (layout_file, br.MIN_TABLE_GAP),
          worst_table >= br.MIN_TABLE_GAP, "closest %.0f mm" % worst_table)
    lines = br.URScriptBackend.joint_moves(frames)
    check("%s: straight-down moves onto every brick" % layout_file,
          sum("movel" in l for l in lines) == 4 * len(jobs), "%d movel for %d bricks" % (sum("movel" in l for l in lines), len(jobs)))

# menu g from several poses
work = br.work_pose(tf, home)
old_ready = br.computed_ready(tf)
poses = {
    "already at the start position": home,
    "our gripping start pose": work,
    "old READY (other side)": old_ready,
    "Jin's robot this afternoon (elbow +191, base +338)": [old_ready[0] + 338, old_ready[1] + 6, old_ready[2] + 191,
                                                          old_ready[3] + 5, old_ready[4] + 14, old_ready[5]],
    "left over a brick after a wall": br.preview(br.plan(json.load(open("test_wall_lama.json")), tf,
                                                         types.SimpleNamespace(grid=0, snap_rot=False)), tf, home)[-60]["j"],
}
for name, q in poses.items():
    try:
        steps = br.ready_route(q, home, tf)
        path = [q] + [t for _, t in steps]
        lifted = 1 if steps and steps[0][0].startswith("lift") else 0
        wa = min((br.move_gaps(a, b, table, 30)[0] for a, b in zip(path[lifted:], path[lifted + 1:])), default=99)
        wt = min((br.move_gaps(a, b, table, 30)[1] for a, b in zip(path[lifted:], path[lifted + 1:])), default=999)
        end_ok = max(abs(a - b) for a, b in zip(path[-1], home)) < 0.01
        check("g from %s: %d steps, ends at the start position" % (name, len(steps)), end_ok,
              "; ".join(w for w, _ in steps)[:110])
        check("g from %s: gripper >= %d mm from the arm, arm >= %d mm above the table" % (name, br.MIN_ARM_GAP, br.MIN_TABLE_GAP),
              wa >= br.MIN_ARM_GAP and wt >= br.MIN_TABLE_GAP, "arm %.0f mm, table %.0f mm" % (wa, wt))
    except ValueError as e:
        check("g from %s" % name, False, "refused: " + str(e))

print("\nALL PASSED" if ok else "\nSOME FAILED")
