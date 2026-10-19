# What we use from HANDZONe

[HANDZONe](https://github.com/newmedia-centre/handzone) is TU Delft New Media Centre's system for controlling and watching UR robots from VR (Unity app for Meta Quest + a server in Docker + a Grasshopper plugin). Our lecturer pointed us to it for the "see the robot build it before the real robot moves" part.

We don't run HANDZONe itself. We reuse **only its 3D models**: the **UR5 with its gripper**, and the **LAMA table** the robot is mounted on, in the same position relative to each other as in HANDZONe's main scene. They become the **ghost robot and table** in our AR scene.

## Where it is in this project
`Assets/ThirdParty/HANDZONe/`
| Path | What |
| --- | --- |
| `Robot/LAMA_UR5.prefab` | Table + UR5 + gripper, ready to place. **No HANDZONe scripts** (server, menus, gizmos stripped) |
| `Robot/LAMA_UR5_joints.json` | Which 6 parts of the model are the UR5's joints, which way each turns, and their starting rotation |
| `Meshes/Polyscope/Polyscope_hp_joined.fbx` | UR5 model (one FBX, joints as nested parts) |
| `Meshes/Gripper.fbx`, `Meshes/Table.fbx` | Gripper and LAMA table models |
| `Meshes/Materials`, `Materials/` | Their materials and textures |

## How the joints work (from HANDZONe's `RobotManager.cs`)
- The 6 joints are nested parts of the UR5 model, base to wrist: `1_Body_reduced` → `2_BodyEnd_reduced` → `3_BodyEnd_reduced` → `4_Body_reduced` → `5_Body_reduced` → `6_MetalBodyEnd_reduced`.
- HANDZONe turns joint *i* like this: `localRotation = Euler(startRotation + direction × angle)`, where the angle is the real robot's joint angle (UR5 joint values, radians → degrees) and `direction` is ±1 around the part's local z axis: `[-1, -1, +1, -1, -1, +1]`.
- So the model can be posed with exactly the same 6 joint angles the real UR5 (or RoboDK) uses. That's what the ghost robot preview needs.
- HANDZONe gets those angles from its server (live from the robot or from URSim), and for "grab the robot" interactions asks the server for inverse kinematics. We'll get them from RoboDK through the robot bridge instead (route B, see DEVLOG).

## How it was brought over (2026-10-19)
Copying the prefab files by hand would also bring HANDZONe's scripts (server connection, Socket.IO, VR menus), which don't compile here. Instead:
1. A temporary editor script was run inside HANDZONe (Unity batch mode, no window): it opened `MainMenu.unity`, copied `RobotArmClean` (UR5 + gripper) and the `Table.fbx` instance into one new prefab **keeping their relative positions**, removed all scripts, animators and gizmos, read the joint list from `RobotArm.prefab`'s `RobotManager`, and exported a Unity package (models, materials, textures only).
2. The package was imported into PicoBricks and moved into `Assets/ThirdParty/HANDZONe/` (files moved with their `.meta` files, so all references stay intact).
3. The temporary script was deleted from the local HANDZONe copy again. Nothing was changed in or pushed to the lecturer's repository.

## Facts from HANDZONe's scene
- In `MainMenu.unity`: robot at (0, −0.003, −1.707), rotated 180° around y; table at (0, −0.916, −1.241). The robot stands at one end of the table, facing along it.
- The table model is rotated (270°, 90°, 0°) inside the prefab (Blender-style axes). Our scene setup will measure its top surface and front edge from the model itself rather than rely on these numbers.
