# PicoBricks – Development Log

AR layer of the Creative Robotics group project (TU Delft, ARIT1507, Oct 2026).
**Idea:** a user places timber bricks in AR on the real table, previews the result, and a UR5 robot then builds the same layout.

This log records every step, what worked, what didn't and why decisions were made, so the whole team can follow along and continue the work.
Newest entries are at the bottom. Each entry ends with **Worked / Didn't work / Still to do** where relevant.

| Who | Task (week of 12 Oct) |
| --- | --- |
| Parjanya Singh + Jin Kim | Virtual environment on the Pico headset (this repo) |
| Matthias Nahum | Robot arm moves blocks to the correct location |
| Suzanne Minkels | Computer vision + Raspberry Pi camera |

---

## Hardware and software

- **Headset:** Pico 4 Enterprise, PICO OS **5.9.9** (Aug 2024), Android 10 / API 29. Borrowed, so we don't update its firmware.
- **Robot:** UR5 CB3, bolted to a table, in the LAMA Lab until the last week, then moved to the exhibition space.
- **Unity:** **6000.3.8f1** (same version as the lecturer's HANDZONe project), URP, Android build target.
- **Packages:** PICO Integration SDK **2.5.0** (git tag `release_2.5.0`), XR Interaction Toolkit 3.4.0 + Starter Assets sample, TextMesh Pro essentials.

---

## 2026-10-09 (Fri): Getting the tools working

### Unity Hub + Editor
- Installed Unity Hub (x64, the laptop has an AMD Ryzen CPU).
- HANDZONe is pinned to Unity **6000.3.8f1**, so we installed exactly that instead of the newest version (opening it in a newer one would upgrade and possibly break the project).
- **Didn't work:** the first install was incomplete. Android Build Support was missing and the Hub showed no "Add modules" option (its `modules.json` was missing).
  **Fix:** Remove from Hub → delete `C:\Program Files\Unity\Hub\Editor\6000.3.8f1` → reinstall with **Android Build Support + OpenJDK + Android SDK & NDK Tools**, keeping the Hub open until it finishes.

### Lecturer's HANDZONe repo (https://github.com/newmedia-centre/handzone)
- Cloned with Git LFS (~6 GB). Opened `unity/` with **Add project from disk**.
- Safe Mode prompt (compile errors: `SocketIOClient` missing) → clicked **Ignore** so NuGetForUnity could download the C# libraries. Safe Mode would block that.
- Missing prefabs (UR Pendant, SliderPanel, XR Origin): the repo ignores `Assets/Samples`, so the XR Interaction Toolkit **Starter Assets** sample had to be imported via Package Manager.
- Declined the Meta XR Simulator update (would change the project's package versions).
- **What HANDZONe is:** TU Delft New Media Centre's system to control UR robots from VR:
  Unity app (with a live UR5 "digital twin") ⇄ server in Docker (login, sessions, robot connection) ⇄ real UR5 or URSim (simulated robot).
  It targets Meta Quest, so we use it as **reference and a source of parts** (UR5 model, table model, robot commands), not as our main app.

### Headset connection
- Connect using given C-TYPE cable

### New project: PicoBricks
- **Decision:** build the Pico app in a **small fresh project** instead of inside HANDZONe. HANDZONe is large and set up for Quest; debugging a new headset inside an unfamiliar big project is hard. Robot parts get copied in later.
- Template: Universal 3D. Added the PICO SDK (git URL) and XR Interaction Toolkit to `Packages/manifest.json`.
- PICO SDK window → **Apply All** (Android, API 29, IL2CPP/ARM64, PICO XR plugin). Project Validation → **Fix All** (floor tracking, `PXR_Manager` in the scene).
- **Signing key** (the app's "name tag"; the Pico only installs signed apps): Player → Publishing Settings → Custom Keystore → created `user.keystore` in the project root, alias `picobricks`. Unity forgets the password on every restart, so re-enter it before building. **The keystore is not in Git**; ask Jin for it.
- Package name `com.lama.picobricks`.
- ✅ **Worked:** first build (Ctrl+B), a grey cube floating in the headset.

---

## 2026-10-10 (Sat): Grabbing bricks

- Wrote `Assets/Editor/BrickSceneSetup.cs`: menu **PicoBricks → Setup Brick Scene** builds the whole scene in one click, so nobody has to set up 50 things by hand and teammates get an identical scene.
  It swaps the plain XR rig for the Starter Assets rig (controller ray + grab), adds a floor, table and bricks with physics (`Rigidbody`) and grabbing (`XRGrabInteractable`).
- Grab settings: `throwOnDetach = false` (put bricks down gently instead of throwing them) and `useDynamicAttach = true` (hold the brick where you grabbed it).
- Needed: Window → TextMeshPro → **Import TMP Essential Resources** (validation error from the Starter Assets).
- ✅ **Worked:** picking up and stacking bricks in VR.
- Feedback from testing: rotating bricks is hard, positioning is fiddly, need a reset → reset added (X button); rotation/snapping still to do.

### Passthrough (seeing the real room)
- Added `Assets/Scripts/Passthrough.cs` and `Assets/Scripts/BrickControls.cs` (left controller **X** = reset bricks, **Y** = passthrough on/off).
- **Didn't work, attempt 1:** black background, X/Y did nothing.
  - Buttons: Input System action bindings didn't fire on the Pico → switched to `UnityEngine.XR.InputDevices` (`primaryButton` / `secondaryButton`), the same API the PICO SDK uses internally. ✅
  - Passthrough: requested too early → wait 1 s after start. Still black.
- **Didn't work, attempts 2–3:** the app requested passthrough (log: `passthrough ON`, manifest has `enable_vst=1`), but the headset's camera service never started.
  **Cause:** headset firmware 5.9.9 is older than PICO SDK 3.x expects (release notes mention 5.13+).
  **Decision:** the headset isn't ours, so instead of updating the firmware we **downgraded the PICO SDK to 2.5.0**, the release from just before the firmware (same script IDs, so the scene and settings carried over).
  Leftover SDK 3.4 debugger files were moved to `_backup_pico_sdk34_debugger/` (not in Git).

---

## 2026-10-11 (Sun): Passthrough and placing the table in the real room

- With SDK 2.5 the camera service **did** start, but the view was still black.
  **Cause:** URP's HDR colour format (R11G11B10) has **no alpha channel**, so the "transparent" background was painted solid black.
  **Fix:** `Passthrough.cs` turns HDR off on the camera while passthrough is on. ✅ **Worked:** real room visible, Y switches VR ↔ passthrough.
- **Didn't work:** in passthrough the table floated above my head. Logs showed head height 1.1 m in VR but ~0.1–0.2 m in passthrough: switching passthrough on this firmware **shifts the tracking height by ~1.15 m**.
  - Re-requesting floor tracking: no effect. Moving the rig to compensate: unreliable, made Y toggling worse. Both removed.
  - **Decision:** don't trust the headset's floor at all; take all heights from a calibration where you touch the real table.

### Table calibration: `Assets/Scripts/TableCalibration.cs`
- The app has no idea where the real table is: the table is spawned relative to wherever the headset started. Calibration fixes that.
- v1: touch 2 front corners. ✅ worked on my desk at home, but:
  - **Bug:** bricks dropped off the table fell forever, and bricks lifted high rested on an "invisible shelf". **Cause:** the invisible floor stayed at the headset's (wrong) floor height, ~37 cm above my desk. **Fix:** the floor is now part of the `Workspace` group and moves with the table; bricks that fall >3 m are put back.
  - **Bug:** table height dropped ~1 m with every calibration. **Cause:** the Starter Assets rig has **gravity and locomotion** (made for VR games); "I" fell onto the moved floor. Thumbsticks could also move/turn the world. **Fix:** setup script switches off Move, Gravity, Turn, Teleportation, Climb, Grab Move and the CharacterController. In AR the virtual world must stay locked to the real room.
- v2 (current): **4 points**, right controller **B** to start, white ball = measuring tip, **trigger** to record:
  1. front-left corner, 2. front-right corner, 3. back-left corner, 4. the floor.
  The virtual table copies the real width, depth, height and direction; the floor goes where you touched it. The label shows the measured size to compare with a tape measure.
- ✅ **Worked** on my (messy) desk. Still to do: test on a clean table at uni.
- Known limitation: after toggling Y, re-calibrate (tracking height jump).

### Decisions and open questions
- **Robot pickup:** the robot picks bricks from a **fixed, repeatable spot**. Plan: a 3D-printed holder/tray with chamfered pockets that self-centre the bricks, pinned to the table; the soft gripper should tolerate a few mm. Matthias teaches the position once. Computer vision for finding loose bricks can come later.
- **Headset ↔ robot registration:** the table is bolted to the robot, so 3 marks on the table have **fixed robot coordinates**. Measure them once in the lab (jog the robot tip to each mark), then each session touch the same marks with the controller. This also works after moving to the exhibition hall. Computer vision (marker seen by the headset camera) is a possible upgrade; needs Pico Enterprise camera access (untested).
- **AR → robot interface:** an ordered list `(brick_id, x, y, z, yaw)` in robot coordinates (note: Unity is Y-up left-handed, UR is Z-up right-handed). Transport and format to agree with Matthias.
- **HANDZONe parts to reuse:** UR5 model (live digital twin from joint data), table model (`Meshes/Table.fbx`), robot commands (MoveL/MoveJ, tool digital out for the gripper, program playback for previews). Open question for Matthias: drive the robot through HANDZONe's server or through their own script?
- **Sharing:** Git + GitHub (not OneDrive: syncing Unity's Library folder corrupts projects).

### Version control
- Set up Git for this project. Everything up to here is in the first commit; this log is the record of how we got there.
  From now on: one commit per working step, with a short note on what was tested.

### Real brick size (in progress)
- Bricks are **5 cm wide × 5 cm high × 10 cm deep**, **10** of them: two rows of five near the front edge, long side pointing away from the user.
- Weight not measured yet; estimate 0.13 kg (0.25 L of timber at ~500 kg/m³). Change `BrickMass` in `BrickSceneSetup.cs` once weighed.

---

## 2026-10-14: 3D-printed pickup trays

- **Problem:** the robot needs every brick to start in exactly the same spot. **Decision:** 3D-printed trays (PLA, Bambu A1), fixed to the robot table with double-sided tape.
- Generated in **Rhino 8 by a script** (`hardware/pickup-tray/make_tray.py`) instead of modelling by hand: exact, and easy to change (clearance, slot widths) by editing numbers and re-running. Output: STL files ready for Bambu Studio. Details, print settings and pocket coordinates: [`hardware/pickup-tray/README.md`](../hardware/pickup-tray/README.md).
- Design choices:
  - **Two trays** (6 + 4 pockets), because 10 don't fit on the 256 × 256 mm bed.
  - Bricks held at the **corners only**, with a 45° lead-in so they self-centre; **open slots on every side** because we don't know the gripper's finger size yet.
  - **Engraved numbers 1–10** = brick IDs; **3 crosshairs on Tray A** = shared reference points for the robot and the AR calibration.
- **Didn't work at first:** Rhino booleans failed (some cutting shapes were inside-out, and pocket boxes fully inside the tray can't be subtracted on their own). **Fix:** flip shapes to face outwards, merge all cutters into one, then subtract once.
- Still to do: print a test pocket and check the fit with a real brick; adjust slots once Matthias knows the gripper.

**Revision (same day):** we only use **6 bricks**, and the trays used too much filament (436 cm³ for both).
- Now **one tray** (3 × 2 pockets, 229 × 250 mm, fits the A1 bed) and **minimal**: a 1.6 mm frame with open windows (bricks stand on the table), plus L-shaped corner pieces with a 3 mm lead-in. **76 cm³**, about 6× less plastic.
- Numbers 1–6 and the 3 crosses moved onto the frame. Old two-tray files removed (still in Git history).
- Unity: `BrickCount` = 6, 3 per row.

**Clearance 0.5 → 1.0 mm per side** (pocket 52 × 102 mm). Reasoning: timber varies by ±0.5–1 mm and swells with humidity, printed inside sizes come out 0.1–0.3 mm small, and a snug brick could drag the taped tray or make the soft gripper slip. 1 mm per side still keeps the brick within ±1 mm (~1° rotation), which the gripper tolerates.
Added `test_fit_clearance_0.5-1.0-1.5.stl`: three small frames to try real bricks before printing the full tray.

**After printing the test frames:**
- ✅ **0.5 mm per side fits the real bricks well**, so the tray went back to 0.5 (pocket 51 × 101 mm, pitch 79 × 129 mm). The 1.0 mm worry turned out unnecessary for these bricks.
- ❌ The lead-in only covered the inner 3 mm of the 4 mm corner wall, leaving a flat ledge a brick could rest on. **Fix:** the 45° slope now starts at the **outer top edge** of each corner piece, so a brick landing on a corner slides into the pocket.
- ❌ The test frames only had 2 (diagonal) corner pieces. **Fix:** the test frame now has all 4, same as the tray (`test_fit_0.5.stl`).

---

## Still to do
- [x] Test the new brick size in the headset, then weigh a real brick (**0.12 kg** measured; `BrickMass` left at the 0.13 kg estimate, the difference doesn't matter in the app)
- [ ] Real LAMA table + UR5 model from HANDZONe (ghost robot)
- [ ] 3-point calibration with robot coordinates (needs Matthias's measurements)
- [ ] Grid snapping + rotating bricks: v2 on branch `snap-grid` (lying/standing, centred stacking, no floating), **v2 not yet tested in the headset**
- [ ] Controller button to switch aligned ↔ non-aligned stacking (`BrickSnap.AlignToBelow`)
- [ ] Ask Matthias: can the gripper place a brick **standing** (picked lying from the tray, turned 90°)?
- [ ] Preview, then send brick list to the robot (URSim first)
- [ ] Test on a clean table at uni
- [ ] Print the pickup tray (measure bricks first), tape it to the robot table, teach positions

---

## How to run it (short version)
1. Unity Hub → Add project from disk → this folder (Unity **6000.3.8f1** with Android modules).
2. Get `user.keystore` from Jin, put it in the project root, enter the passwords in Player → Publishing Settings.
3. **PicoBricks → Setup Brick Scene**, then **Ctrl+B** with the headset connected (PICO cable, USB debugging on).
4. In the headset: **B** (right) calibrate the table, **trigger** to record points, **X** (left) reset bricks, **Y** (left) passthrough on/off, **grip** to grab.

---

## 2026-10-13 (Tue): Grid snap (Parjanya)

### Setting up a second laptop
- Cloned to `C:\dev\PicoBricks` (outside OneDrive), Unity **6000.3.8f1** + Android modules. No keystore yet, so Custom Keystore unticked locally (not committed).
- **Didn't work:** Build And Run built fine every time, but the install step kept failing (`Could not find any valid targets to launch on for Android` / `Failed to find device ... in 'adb devices'`), even right after Unity listed the headset as `Pico A8110`. Suspects: USB debugging not approved for this laptop, headset sleeping when taken off, or another adb (PICO Developer Center / Meta tools) fighting Unity's. Workaround: install `001.apk` by hand.

### Grid snap: `Assets/Scripts/BrickSnap.cs`
- Goal: layouts the robot can actually build, i.e. exact, discrete positions instead of wherever physics drops a brick.
- On release over the table (`selectExited`, after XRI has finished dropping):
  1. **Turn** to the nearest 90° (long side away from you or sideways).
  2. **Grid**: the brick's front-left corner snaps to a **5 cm** grid in the `Workspace` frame (origin = middle of the table's front edge), so edges line up between bricks of either orientation.
  3. **Height**: a box sweep straight down finds the table or the brick below; height is rounded to whole 5 cm layers.
  4. **Lock**: Rigidbody goes kinematic; physics only while held. Grabbing a placed brick unlocks it.
- Released off the table (footprint not fully on it): back to its start spot. **X** reset also unlocks placed bricks (`BrickControls.PutBack`).
- Each placement logs `x, z (cm), layer, yaw`, which is the start of the `(brick_id, x, y, z, yaw)` list for Matthias.
- `BrickSceneSetup.cs` adds `BrickSnap` to every brick and wires `workspace` + `table`. Re-run **PicoBricks → Setup Brick Scene** after pulling.
- Known limits: lifting a brick out of the middle of a stack leaves the ones above floating (they're locked); a brick half over the table edge counts as off the table.
- **Still to do:** test in the headset (gaps, rotations, stacks of 3, off-table return, X reset, snapping after B calibration).

### Grid snap v2 (same day, after testing v1 in the headset)
- v1 test findings (installed by hand, `001.apk`):
  - **Floating:** take a brick out from under a stack and the ones above stayed in mid-air (they were locked).
  - **Half-brick offset:** a brick on top of another often landed half a brick off. Cause: v1 snapped edges to a 5 cm grid, which is half a brick length, so a "brick-wall" offset was a valid position. Not what we want for robot towers.
  - Bricks could only lie flat.
  - 10 bricks were more than needed.
- v2 changes (`BrickSnap.cs`, `BrickSceneSetup.cs`, `BrickControls.cs`):
  - **6 bricks**, one row along the front edge.
  - **Lying or standing:** on release, if the brick's long axis is within 45° of vertical it stands (5 × 5 cm footprint, 10 cm tall); otherwise it lies flat. Yaw still snaps to 90°.
  - **Centred stacking:** released over a placed brick, the new brick is centred on it (same orientation: exactly on top; turned 90°: centred across). On the bare table the 5 cm grid still applies. `BrickSnap.AlignToBelow = false` brings back grid-only (half-brick offsets allowed), ready for a button that toggles it.
  - **Heights** are now the exact top of whatever is below (rounded to 5 cm) instead of "layers", so standing (10 cm) and lying (5 cm) bricks mix correctly.
  - **No floating:** when a placed brick is picked up, every placed brick (lowest first) sweeps straight down and drops onto whatever is left below it. X reset now also clears the placed state.
- Each placement now also logs lying/standing, which the robot list needs.
- **Still to do:** test v2 in the headset (stack aligned, crossed stack centred, standing brick on table and on a brick, pull the middle brick of a 3-stack, X reset).

---

## 2026-10-15: Merged Parjanya's grid snap into main

- Parjanya's branch `snap-grid` (Pull Request #1: grid snap v1 + v2) merged into `main` together with Jin's pickup-tray work.
- Three small clashes, resolved: `.gitignore` (kept both additions), `BrickSceneSetup.cs` (kept Parjanya's 6 bricks in one row + measured weight 0.12 kg), `README.md` (kept both).
- Checked: the merged project compiles (Unity batch mode, no errors). **Not yet tested in the headset.**
- New headset (same model, different unit) arrived; needs developer mode + USB debugging, then check its firmware version (decides which PICO SDK works, see 2026-10-10).
- Next: the project is changing direction: **draw a line in AR + set layer height → bricks stack themselves into a wall → robot builds it.** To be discussed with the team.
