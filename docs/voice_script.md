# Voice script (the visitor's spoken guide)

One short line per moment. Jin writes the lines; `tools/make_voice.py` turns each line into an audio file
(`Assets/Resources/Voice/<key>.mp3`), which the app plays at that moment (`Assets/Scripts/VoiceGuide.cs`).
A moment without a line stays silent. Keep lines short: the visitor is reading the text and using the controller at the same time.

| key | Moment | On-screen text (already in the app) | Voice line (Jin) |
| --- | --- | --- | --- |
| intro_welcome | End of the opening, under the logo (app start) | A = Build a wall with the robot · How we built the pavilion (coming soon) | e.g. "Welcome to Print Hello Earth, Creative Robotics 2026. Press A to build a wall with the robot." |
| menu_welcome | Home screen opens (after the intro, and after B) | the three options | Welcome to Print, Hello Earth. |
| draw | Robot mode opens / ready to draw | Draw a curve inside the black rectangle. Point at the table and HOLD the TRIGGER | Draw a curve inside the black rectangle. |
| too_short | The line was too short | That line was too short. HOLD the TRIGGER and draw a longer line | |
| preview | The bricks show the wall | Your wall: … THUMBSTICK left / right = longer / taller. A = build it, TRIGGER = draw again | |
| no_fit | Wall too big for the robot | Too big for the robot to reach. TRIGGER = draw a smaller curve, THUMBSTICK = make it taller | |
| ghost_start | The see-through robot starts its rehearsal | Rehearsal: this is how I will build your wall | The ghost UR5 will now start to move. |
| real_start | Just before the REAL robot moves (sent by the laptop bridge after "y", 3 s before it moves) | (none) | Caution! The physical UR5 will now move. |
| ghost_done | Rehearsal finished | Rehearsal done. Our team will now start the real robot | |
| error | Something failed | Something went wrong. TRIGGER = draw your wall again | |
| pavilion_start | "Design our pavilion" opens | Design our pavilion... | Design our pavilion on your own. Draw any curve, and get started! |

Made with `tools/make_voice.py` in the natural Microsoft neural voice **en-GB-SoniaNeural** (British; Jin chose it from 4 samples) (edge-tts, 25 Oct) as `Assets/Resources/Voice/<key>.mp3`. To change a line or the voice: edit LINES / VOICE in the script and run it again (needs internet).
