# PicoBricks

AR app for the **Pico 4 Ultra Enterprise** headset: design a timber-brick wall on a real table in augmented reality, preview it, and (later) let a **UR5 robot** build it.
Part of the Creative Robotics group project (TU Delft, ARIT1507, Oct 2026).

| Who | Part |
| --- | --- |
| Parjanya Singh + Jin Kim | AR on the Pico headset (this repo) |
| Matthias Nahum | Robot arm moves blocks to the correct location |
| Suzanne Minkels | Computer vision + Raspberry Pi camera |

📖 **Full history of what we did, what worked, what didn't and why:** [`docs/DEVLOG.md`](docs/DEVLOG.md)

**Versions**
- **v1: manual placement** (tag [`v1-manual-placement`](../../tree/v1-manual-placement), 15 Oct 2026): grab and place bricks by hand, grid snap, table calibration, pickup tray.
- **v2: wall from a drawn curve** (in progress, branch `wall-builder`): draw a curve on the table, choose 1–3 layers, the 6 bricks build the wall. Iteration 1 = **rectangle** (6 × 1, 3 × 2, 2 × 3); iteration 2 = **triangle** (3 + 2 + 1, later). Then preview with an AR robot, then the real robot builds it.

---

## What it does right now (v2, iteration 1: rectangle along a curve)
- Shows your **real room** through the headset cameras (passthrough).
- **Calibrate** the virtual table onto the real table by touching 4 points. A white outline shows the **build area** (the table minus a 3 cm margin).
- **Draw a curve** (or a straight line) on the table with the controller's laser. The bricks **mimic the curve**: it is scaled to the wall length, the bricks follow it in 10 cm steps and **always touch at one corner** (the one on the inside of each bend). The curve's start point is the wall's first corner. Logic explained in the DEVLOG.
- **Choose the number of layers (1–3).** With 6 bricks: 1 layer = 6 bricks long (60 cm), 2 layers = 3 × 2 (30 cm), 3 layers = 2 × 3 (20 cm).
- Press **OK (A)** and the bricks build the wall: lying flat, along the curve, stacked straight up. If it doesn't fit on the table, you're asked to draw again.
- Every built wall is saved as a **brick list for the robot** (`wall_plan.json` on the headset: build order, layer, position and angle of each brick in table coordinates).
- The 6 bricks start in the **pickup tray** at the table's front-left corner: the same tray we 3D-print ([`hardware/pickup-tray`](hardware/pickup-tray)). They can be grabbed; building a wall takes them from the tray.
- Bricks are **brown, 70 % see-through, with black edges**, so each brick is readable in a built wall.

## Controls (in the headset)
| Button | Action |
| --- | --- |
| **B** (right controller) | Start table calibration |
| **Trigger** (right controller) | During calibration: record a point (front-left corner, front-right corner, back-left corner, floor). Otherwise: **point at the table and hold to draw the wall curve**; press again to draw a new wall |
| **Thumbstick left/right** (right) | Number of layers (1–3) |
| **A** (right controller) | OK: build the wall |
| **X** (left controller) | Put all bricks back in the tray |
| **Y** (left controller) | Switch passthrough ↔ VR (re-calibrate afterwards) |
| **Grip** | Grab a loose brick (bricks in a built wall can't be grabbed) |

The **white ball** in front of the right controller is the measuring tip for calibration. The text floating above the table says what to do next.

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
| `Assets/Scripts/WallBuilder.cs` | **v2:** draw the curve (trigger), choose layers (thumbstick), build the wall (A), save `wall_plan.json` for the robot. Wall patterns are planned here (rectangle now, triangle next). |
| `Assets/Editor/TrayMeshImporter.cs` | Turns the tray STL into a Unity mesh, so the scene uses exactly the printed tray. |
| `Assets/Scripts/BrickSnap.cs` | v1 only, **switched off in v2** (`AddGridSnap` in `BrickSceneSetup.cs`). Grid snap on release (one per brick): lying/standing, 90° turns, centred stacking or 5 cm table grid, lock in place, bricks above drop when one is removed. `BrickSnap.AlignToBelow` switches centred stacking off (half-brick offsets allowed). |
| `Assets/Materials/` | Colours for the bricks (Wood), table, floor, wall line (cyan) and build area (white). |
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
| `Packages/manifest.json` | List of packages: **PICO SDK 3.4.0** (for the Pico 4 Ultra; v1 used 2.5.0 for the old Pico 4, see DEVLOG), XR Interaction Toolkit 3.4.0, … |
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
