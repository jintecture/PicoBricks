# Makes the spoken lines of the app (docs/voice_script.md) with a natural Microsoft neural voice (edge-tts; needs
# internet while it runs, the app itself plays the files offline). Writes Assets/Resources/Voice/<key>.mp3 for
# VoiceGuide.cs and removes an older <key>.wav (two files with the same name would confuse Resources.Load).
#   C:\RoboDK\Python-Embedded\python.exe tools\make_voice.py              all lines, voice VOICE
#   C:\RoboDK\Python-Embedded\python.exe tools\make_voice.py --samples    the welcome line in a few voices to compare
# edge-tts is installed in Downloads\HelloEarth_intro\tts_lib (pip install --target ..., Jin OK'd it on 25 Oct).
import asyncio, os, sys

sys.path.insert(0, os.path.join(os.path.expanduser("~"), "Downloads", "HelloEarth_intro", "tts_lib"))
import edge_tts

VOICE = "en-US-JennyNeural"   # warm, natural woman's voice (Jin: "woman's voice, more natural")
RATE = "-5%"                  # a little slower: visitors are busy with the controller
OUT = os.path.join(os.path.dirname(__file__), "..", "Assets", "Resources", "Voice")
SAMPLES = os.path.join(os.path.expanduser("~"), "Downloads", "HelloEarth_intro", "voice_samples")

LINES = {   # key: line (keep in step with docs/voice_script.md)
    "menu_welcome": "Welcome to Print, Hello Earth.",
    "draw": "Draw a curve inside the black rectangle.",
    "ghost_start": "The ghost U R 5 will now start to move.",
    "real_start": "Caution! The physical U R 5 will now move.",
    "pavilion_start": "Design our pavilion on your own. Draw any curve, and get started!",
}
SAMPLE_VOICES = ["en-US-JennyNeural", "en-US-AriaNeural", "en-GB-SoniaNeural", "en-US-AvaNeural"]


async def speak(text, voice, path):
    await edge_tts.Communicate(text, voice, rate=RATE).save(path)
    print("saved", os.path.normpath(path))


async def main():
    if "--samples" in sys.argv:
        os.makedirs(SAMPLES, exist_ok=True)
        for v in SAMPLE_VOICES:
            await speak(LINES["menu_welcome"] + " " + LINES["pavilion_start"], v, os.path.join(SAMPLES, v + ".mp3"))
        return
    for key, text in LINES.items():
        await speak(text, VOICE, os.path.join(OUT, key + ".mp3"))
        for old in (key + ".wav", key + ".wav.meta"):
            p = os.path.join(OUT, old)
            if os.path.exists(p):
                os.remove(p)


asyncio.run(main())
