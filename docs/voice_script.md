# Voice script (the visitor's spoken guide)

One short line per moment. Jin writes the lines; `tools/make_voice.py` turns each line into an audio file
(`Assets/Resources/Voice/<key>.mp3`), which the app plays at that moment (`Assets/Scripts/VoiceGuide.cs`).
A moment without a line stays silent. Keep lines short: the visitor is reading the text and using the controller at the same time.

| key | Moment | On-screen text (already in the app) | Voice line (Jin) |
| --- | --- | --- | --- |
| welcome | App starts / next visitor | Welcome! Press A to start | |
| draw | Ready to draw | Draw your wall. Point at the table and HOLD the TRIGGER (index finger) | |
| too_short | The line was too short | That line was too short. HOLD the TRIGGER and draw a longer line | |
| preview | The bricks show the wall | Your wall: … THUMBSTICK left / right = longer / taller. A = build it, TRIGGER = draw again | |
| no_fit | Wall too big for the robot | Too big for the robot to reach. TRIGGER = draw a smaller curve, THUMBSTICK = make it taller | |
| ghost_start | The see-through robot starts its rehearsal | Rehearsal: this is how I will build your wall | |
| ghost_done | Rehearsal finished | Rehearsal done. Our team will now start the real robot | |
| error | Something failed | Something went wrong. TRIGGER = draw your wall again | |
