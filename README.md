# PicoBricks

AR app for the **Pico 4 Enterprise** headset: place timber bricks on a real table in augmented reality, preview the layout, and (later) let a **UR5 robot** build it.
Part of the Creative Robotics group project (TU Delft, ARIT1507, Oct 2026).

| Who | Part |
| --- | --- |
| Parjanya Singh + Jin Kim | AR on the Pico headset (this repo) |
| Matthias Nahum | Robot arm moves blocks to the correct location |
| Suzanne Minkels | Computer vision + Raspberry Pi camera |

📖 **Full history of what we did, what worked, what didn't and why:** [`docs/DEVLOG.md`](docs/DEVLOG.md)

**Versions**
- **v1: manual placement** (tag [`v1-manual-placement`](../../tree/v1-manual-placement), 15 Oct 2026): grab and place bricks by hand, grid snap, table calibration, pickup tray. Described below.
- **Next (in planning):** draw a line in AR and set the layer height, the bricks stack themselves into a wall, preview it with an AR robot, then the real robot builds it.

---

## What it does right now
- Shows your **real room** through the headset cameras (passthrough) with virtual bricks in it.
- **Calibrate** the virtual table onto a real table by touching 4 points.
- **6 bricks (5 × 5 × 10 cm)** you can grab and place, lying flat or standing on end.
- **Grid snap**: let go of a brick over the table and it
  - settles **lying** (5 cm tall) or **standing** (10 cm tall), whichever is closer to how you held it, turned to the nearest 90°;
  - on another placed brick: sits **centred on it** (no half-brick offsets); on the table: edges snap to a **5 cm grid**;
  - lands on whatever is below and locks in place. Take a brick out of a stack and the ones above **drop down** onto what's left.
  - Let go off the table and it returns to its start spot. Every placement is logged (position, rotation, lying/standing): the data the robot will need.
- A 3D-printable **pickup tray** for the real bricks: [`hardware/pickup-tray`](hardware/pickup-tray).

## Controls (in the headset)
| Button | Action |
| --- | --- |
| **Grip** (either hand, side button) | Grab a brick (point the laser at it, or reach out and grab it). Release over the table to snap it; hold it upright to stand it on end |
| **B** (right controller) | Start table calibration |
| **Trigger** (right controller) | Record a calibration point. Order: front-left corner, front-right corner, back-left corner, floor |
| **X** (left controller) | Put all bricks back on the table |
| **Y** (left controller) | Switch passthrough ↔ VR (re-calibrate afterwards) |

The **white ball** in front of the right controller is the measuring tip. The text above the controller says what to do next.

---

## Where things are

**You'll mostly work in these:**
| Path | What it is |
| --- | --- |
| `Assets/Scenes/SampleScene.unity` | **The scene**: XR rig, table, bricks, floor. Open this in Unity. |
| `Assets/Editor/BrickSceneSetup.cs` | Menu **PicoBricks → Setup Brick Scene**: builds the whole scene in one click. Brick size, number (6) and weight are set at the top of this file. |
| `Assets/Scripts/Passthrough.cs` | Turns the real-room camera view on and off. |
| `Assets/Scripts/BrickControls.cs` | X and Y buttons; puts bricks back if they fall off the world. |
| `Assets/Scripts/TableCalibration.cs` | 4-point table calibration (B + trigger). |
| `Assets/Scripts/BrickSnap.cs` | Grid snap on release (one per brick): lying/standing, 90° turns, centred stacking or 5 cm table grid, lock in place, bricks above drop when one is removed. `BrickSnap.AlignToBelow` switches centred stacking off (half-brick offsets allowed). |
| `Assets/Materials/` | Colours for the bricks (Wood), table and floor. |
| `docs/DEVLOG.md` | Development log: every step, problem and decision. |
| `hardware/pickup-tray/` | 3D-printable tray that holds the 6 real bricks for the robot (STL, Rhino model and the script that makes it) |

**Settings and imported parts (normally don't touch):**
| Path | What it is |
| --- | --- |
| `Assets/Resources/PXR_*.asset` | PICO SDK settings (e.g. "Video Seethrough" on) |
| `Assets/Samples/XR Interaction Toolkit/` | Unity's ready-made controller rig (laser, grabbing). We switch off its walking/gravity for AR. |
| `Assets/TextMesh Pro/` | Text system needed by the controller rig |
| `Assets/XR/`, `Assets/XRI/` | XR plug-in settings (PICO enabled for Android) |
| `Assets/Settings/` | Graphics (URP) settings. The headset uses `Mobile_RPAsset`. |
| `Assets/TutorialInfo/`, `Assets/Readme.asset` | Leftovers from Unity's project template, can be ignored |
| `Packages/manifest.json` | List of packages: **PICO SDK 2.5.0** (not newer, see DEVLOG), XR Interaction Toolkit 3.4.0, … |
| `ProjectSettings/` | Unity project settings (Android, package name `com.lama.picobricks`, …) |

**Not on GitHub on purpose:** `Library/`, `Temp/`, `Logs/` (Unity rebuilds them), the built app (`.apk`) and the signing key (`user.keystore`, ask Jin).

---

## Getting started
1. Install **Unity Hub** and Unity **6000.3.8f1** with **Android Build Support + OpenJDK + Android SDK & NDK Tools**. Use exactly this version.
2. Get the project: GitHub Desktop → **File → Clone repository** → `jintecture/PicoBricks`.
3. Unity Hub → **Add → Add project from disk** → the cloned folder. The first open takes a while.
4. Ask Jin for `user.keystore`, put it in the project folder, then **Edit → Project Settings → Player → Android → Publishing Settings** → enter the passwords. Unity forgets them on every restart.
   *No keystore yet?* Untick **Custom Keystore** there; Unity signs with your laptop's debug key. Then (a) uninstall a teammate's PicoBricks from the headset before installing yours (different signatures can't overwrite each other) and (b) **never commit `ProjectSettings/ProjectSettings.asset`** with that box unticked.
5. Headset: developer mode on (Settings → General → About → tap *Software Version* 7×, then **USB Debug** on). Connect with the **cable that came with the Pico** (other cables may be charge-only).
6. In Unity: **PicoBricks → Setup Brick Scene**, then **Ctrl+B** (Build And Run). Click **Yes** on the "Missing Project ID" popup. The app opens in the headset.
   *"Could not find any valid targets" / "No Android devices connected"?* The build itself is fine (`001.apk` in the project folder); only the install failed. Keep the headset awake and USB debugging approved, then retry. Fallback: copy `001.apk` to the headset's **Download** folder via File Explorer and install it from the headset's file manager.

## Working together
- **Before you start:** pull the latest version (GitHub Desktop → *Fetch origin* → *Pull*).
- **When something works:** commit with a clear note (what you tested, what works, what doesn't), then push.
- **Unity scenes don't merge well.** Agree who edits `SampleScene` on a given day. Scripts are fine to edit in parallel.
- Add an entry to `docs/DEVLOG.md` for anything worth knowing.
