# PicoBricks – Development Log

AR layer of the Creative Robotics group project (TU Delft, ARIT1507, Oct 2026).
**Idea:** a user places timber bricks in AR on the real table, previews the result, and a UR5 robot then builds the same layout.

This log records every step, what worked, what didn't and why decisions were made, so the whole team can follow along and continue the work.
Newest entries are at the bottom. Each entry ends with **Worked / Didn't work / Still to do** where relevant.

| Who | Task (week of 12 Oct) |
| --- | --- |
| PS + JK | Virtual environment on the Pico headset (this repo) |
| MN | Robot arm moves blocks to the correct location |
| SM | Computer vision + Raspberry Pi camera |

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
- Developer mode: Settings → General → About → tap **Software Version** 7×, then **USB Debug** on.
- **Didn't work:** the laptop didn't see the headset at all. **Cause:** a charge-only USB-C cable. **Fix:** use the cable that came with the Pico.
- Confirmed with `adb devices` (adb ships with Unity's Android module).

### New project: PicoBricks
- **Decision:** build the Pico app in a **small fresh project** instead of inside HANDZONe. HANDZONe is large and set up for Quest; debugging a new headset inside an unfamiliar big project is hard. Robot parts get copied in later.
- Template: Universal 3D. Added the PICO SDK (git URL) and XR Interaction Toolkit to `Packages/manifest.json`.
- PICO SDK window → **Apply All** (Android, API 29, IL2CPP/ARM64, PICO XR plugin). Project Validation → **Fix All** (floor tracking, `PXR_Manager` in the scene).
- **Signing key** (the app's "name tag"; the Pico only installs signed apps): Player → Publishing Settings → Custom Keystore → created `user.keystore` in the project root, alias `picobricks`. Unity forgets the password on every restart, so re-enter it before building. **The keystore is not in Git**; ask JK for it.
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
- **Robot pickup:** the robot picks bricks from a **fixed, repeatable spot**. Plan: a 3D-printed holder/tray with chamfered pockets that self-centre the bricks, pinned to the table; the soft gripper should tolerate a few mm. MN teaches the position once. Computer vision for finding loose bricks can come later.
- **Headset ↔ robot registration:** the table is bolted to the robot, so 3 marks on the table have **fixed robot coordinates**. Measure them once in the lab (jog the robot tip to each mark), then each session touch the same marks with the controller. This also works after moving to the exhibition hall. Computer vision (marker seen by the headset camera) is a possible upgrade; needs Pico Enterprise camera access (untested).
- **AR → robot interface:** an ordered list `(brick_id, x, y, z, yaw)` in robot coordinates (note: Unity is Y-up left-handed, UR is Z-up right-handed). Transport and format to agree with MN.
- **HANDZONe parts to reuse:** UR5 model (live digital twin from joint data), table model (`Meshes/Table.fbx`), robot commands (MoveL/MoveJ, tool digital out for the gripper, program playback for previews). Open question for MN: drive the robot through HANDZONe's server or through their own script?
- **Sharing:** Git + GitHub (not OneDrive: syncing Unity's Library folder corrupts projects).

### Version control
- Set up Git for this project. Everything up to here is in the first commit; this log is the record of how we got there.
  From now on: one commit per working step, with a short note on what was tested.

### Real brick size (in progress)
- Bricks are **5 cm wide × 5 cm high × 10 cm deep**, **10** of them: two rows of five near the front edge, long side pointing away from the user.
- Weight not measured yet; estimate 0.13 kg (0.25 L of timber at ~500 kg/m³). Change `BrickMass` in `BrickSceneSetup.cs` once weighed.

---

## Still to do
- [ ] Test the new brick size in the headset, then weigh a real brick
- [ ] Real LAMA table + UR5 model from HANDZONe (ghost robot)
- [ ] 3-point calibration with robot coordinates (needs MN's measurements)
- [ ] Grid snapping + rotating bricks
- [ ] Preview, then send brick list to the robot (URSim first)
- [ ] Test on a clean table at uni

---

## How to run it (short version)
1. Unity Hub → Add project from disk → this folder (Unity **6000.3.8f1** with Android modules).
2. Get `user.keystore` from JK, put it in the project root, enter the passwords in Player → Publishing Settings.
3. **PicoBricks → Setup Brick Scene**, then **Ctrl+B** with the headset connected (PICO cable, USB debugging on).
4. In the headset: **B** (right) calibrate the table, **trigger** to record points, **X** (left) reset bricks, **Y** (left) passthrough on/off, **grip** to grab.
