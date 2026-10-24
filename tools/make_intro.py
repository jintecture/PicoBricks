r"""
Makes the app's opening film: one 360-degree video (equirectangular, 4096 x 2048) of short snippets from the
group's videos, on a ring of screens all around the viewer, cutting faster and faster ("film roll"), no sound
(the sound effects play in the app), fading to black at the end, where the app shows the logo.

Rules (Jin, 23 Oct): no video on two screens at the same time; show faces; many videos at once.

  python make_intro.py                 # all steps
  python make_intro.py --preview-only  # only the small flat preview of an existing film

Input : Downloads\HelloEarth_intro\source\*.MOV   (copied from Google Drive "Creative Robotos")
Output: Downloads\HelloEarth_intro\intro_360.mp4  (for the headset)
        Downloads\HelloEarth_intro\intro_preview.mp4 + intro_frame_*.jpg (to look at on the laptop)
Needs ffmpeg (Downloads\ffmpeg) and OpenCV (comes with RoboDK's Python). Settings at the top.
"""
import argparse
import glob
import json
import os
import random
import subprocess
import sys

HOME = os.path.expanduser("~")
BASE = os.path.join(HOME, "Downloads", "HelloEarth_intro")
SOURCE = os.path.join(BASE, "source")
WORK = os.path.join(BASE, "work")
OUT = os.path.join(BASE, "intro_360.mp4")
PREVIEW = os.path.join(BASE, "intro_preview.mp4")
FF_DIR = glob.glob(os.path.join(HOME, "Downloads", "ffmpeg", "ffmpeg-*", "bin"))[0]
FFMPEG, FFPROBE = os.path.join(FF_DIR, "ffmpeg.exe"), os.path.join(FF_DIR, "ffprobe.exe")

# ---------------- settings ----------------
LENGTH = 9.5             # s, film; + 2.5 s logo = 12 s intro (Jin, 24 Oct)
FADE_OUT = 0.25          # s, fade to black at the end (the app then shows the logo)
W, H = 4096, 2048        # equirectangular 360 video
# Dome of screens all around the viewer (Jin: "360 all around, like a dome, in the VR"): DOME_ROWS rows from
# LAT_MAX above the horizon to LAT_MAX below; EQ_COLS screens around at eye level, fewer towards the top and
# bottom (cos of the latitude), so every screen looks about the same size from inside. Rows are offset like bricks.
DOME_ROWS = 9             # Jin 24 Oct: much smaller screens; 72 in front (+ their twins behind), at most ~76 for 79 videos
LAT_MAX = 70.0           # deg; above/below that the dome stays black (small caps)
EQ_COLS = 20
GAP = 8                  # px black gap between screens
SNIP_W, SNIP_H = 320, 240  # snippet size before it is fitted to its screen
FPS = 30
SNIPPET = 1.6            # s cut from a source (longest a screen ever shows one clip)
FIRST, LAST = 1.8, 0.45  # s per clip on a screen: slow at the start, fast at the end (film roll speeding up)
SNIP_EVERY = 2.5         # s of source video per snippet (more snippets from long videos) ...
MAX_SNIPS = 5            # ... at most this many per video
FACE_WEIGHT = 2          # snippets with faces are shown this many times as often
SEED = 7                 # change for a different random order
# ------------------------------------------


def run(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit("ffmpeg failed:\n" + " ".join(cmd) + "\n" + r.stderr[-2000:])
    return r.stdout


def duration(path):
    out = run([FFPROBE, "-v", "error", "-show_entries", "format=duration", "-of", "json", path])
    return float(json.loads(out)["format"]["duration"])


def layout():
    """[(x, y, w, h, twin_x)] of every screen on the 360 frame (equirectangular pixels). Every row has an even
    number of screens; the screen half a turn away (twin_x, behind the viewer) shows the same video, so only the
    front half needs its own videos (Jin, 24 Oct: nobody turns around)."""
    import math
    rows, step = [], 2 * LAT_MAX / DOME_ROWS
    for r in range(DOME_ROWS):
        top = LAT_MAX - r * step
        mid = top - step / 2
        cols = max(4, 2 * round(EQ_COLS * math.cos(math.radians(mid)) / 2))
        y = int((90 - top) / 180 * H)
        h = int(step / 180 * H)
        w = W / cols
        offset = (w / 2) * (r % 2)                       # brick pattern
        for c in range(cols // 2):
            x = (c * w + offset + W / 4) % W             # the half from 90 deg left to 90 deg right is in front
            rows.append((int(x), y, int(w), h, int((x + W / 2) % W)))
    return rows


def face_times(src):
    """[(time s, number of faces)] where faces are visible: frames sampled 3x per second, OpenCV face detector."""
    import cv2
    tmp = os.path.join(WORK, "frames")
    os.makedirs(tmp, exist_ok=True)
    for f in glob.glob(os.path.join(tmp, "*.jpg")):
        os.remove(f)
    run([FFMPEG, "-y", "-i", src, "-vf", "fps=3,scale=480:-2", "-q:v", "4", os.path.join(tmp, "f_%04d.jpg")])
    cascade = cv2.CascadeClassifier(os.path.join(cv2.data.haarcascades, "haarcascade_frontalface_default.xml"))
    hits = []
    for i, f in enumerate(sorted(glob.glob(os.path.join(tmp, "*.jpg")))):
        g = cv2.cvtColor(cv2.imread(f), cv2.COLOR_BGR2GRAY)
        faces = cascade.detectMultiScale(g, scaleFactor=1.1, minNeighbors=6, minSize=(40, 40))
        if len(faces):
            hits.append((i / 3.0, len(faces)))
    return hits


def cut_snippets():
    """Several short snippets per source video (more from long videos; one at the moment with the most faces),
    all the same size, no sound. Returns [(file, source index, has_face)]."""
    os.makedirs(WORK, exist_ok=True)
    tw, th = SNIP_W, SNIP_H
    snippets = []
    sources = sorted(set(os.path.normcase(x) for x in
                         glob.glob(os.path.join(SOURCE, "*.mov")) + glob.glob(os.path.join(SOURCE, "*.MOV"))))
    for i, src in enumerate(sources):
        d = duration(src)
        n = max(1, min(MAX_SNIPS, int(d // SNIP_EVERY)))
        faces = face_times(src)
        times = [d * (0.1 + 0.8 * (k + 0.5) / n) for k in range(n)]          # spread over the video
        if faces:
            face_t = max(faces, key=lambda h: h[1])[0]                       # the moment with the most faces
            times[min(range(n), key=lambda k: abs(times[k] - face_t))] = face_t - SNIPPET / 2
        for k, t in enumerate(times):
            t = max(0.0, min(t, d - SNIPPET - 0.05))
            has_face = any(t <= ft <= t + SNIPPET for ft, _ in faces)
            out = os.path.join(WORK, "snip_%02d_%d_%dx%d.mp4" % (i, k, tw, th))
            if not os.path.exists(out):
                run([FFMPEG, "-y", "-ss", "%.2f" % t, "-i", src, "-t", "%.2f" % SNIPPET, "-an",
                     "-vf", "scale=%d:%d:force_original_aspect_ratio=increase,crop=%d:%d,fps=%d,setsar=1"
                     % (tw, th, tw, th, FPS),
                     "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", out])
            snippets.append((out, i, has_face))
        print("  %-14s %5.1f s -> %d snippet(s)%s" % (os.path.basename(src), d, n, "  faces" if faces else ""))
    print("%d snippets from %d videos, %d with faces" % (len(snippets), len(sources), sum(1 for x in snippets if x[2])))
    return snippets, (tw, th)


def schedule(snippets):
    """Which snippet each screen shows when. Every screen: black until it switches on, then clips that get shorter
    and shorter. Rule: a source video is never on two screens at the same time. Face snippets are preferred
    (FACE_WEIGHT) and the least-used snippets go first, so everything gets shown."""
    rnd = random.Random(SEED)
    slots = []                                            # (start, end, screen)
    n_screens = len(layout())
    for scr in range(n_screens):
        t = rnd.uniform(0.0, 1.6)                         # screens switch on one by one
        while t < LENGTH - 1e-3:
            f = (t / LENGTH) ** 1.4                       # speeding up
            dur = min((FIRST + (LAST - FIRST) * f) * rnd.uniform(0.8, 1.2), LENGTH - t)
            slots.append((t, t + dur, scr))
            t += dur
    slots.sort()
    busy, used = {}, {}
    plan = {scr: [] for scr in range(n_screens)}
    for t0, t1, scr in slots:
        def free(src):
            return all(e <= t0 + 1e-6 or s0 >= t1 - 1e-6 for s0, e in busy.get(src, []))
        cands = [x for x in snippets if free(x[1])] or snippets
        cands.sort(key=lambda x: (used.get(x[0], 0) / (FACE_WEIGHT if x[2] else 1), rnd.random()))
        clip = cands[0]
        used[clip[0]] = used.get(clip[0], 0) + 1
        busy.setdefault(clip[1], []).append((t0, t1))
        plan[scr].append((t0, t1, clip[0], clip[1]))
    # double-check the rule
    clashes = 0
    for scr in plan:
        for t0, t1, _, src in plan[scr]:
            clashes += sum(1 for o in plan if o != scr for a, b, _, s2 in plan[o]
                           if s2 == src and a < t1 - 1e-6 and b > t0 + 1e-6)
    print("schedule: %d clips on %d screens, %d moments with the same video twice"
          % (len(slots), n_screens, clashes // 2))
    return plan


def screen_videos(snippets, size):
    """One video per screen, following the schedule, fitted to the screen's size on the dome."""
    plan = schedule(snippets)
    screens = []
    for scr, (_, _, sw, sh, _) in enumerate(layout()):
        tw, th = sw - GAP, sh - GAP
        tw, th = tw - tw % 2, th - th % 2
        parts = plan[scr]
        start = parts[0][0] if parts else LENGTH
        out = os.path.join(WORK, "screen_%02d.mp4" % scr)
        inputs = ["-f", "lavfi", "-t", "%.3f" % max(start, 1.0 / FPS), "-i", "color=black:s=%dx%d:r=%d" % (tw, th, FPS)]
        chains = ["[0:v]setsar=1[c0]"]
        for j, (t0, t1, clip, _) in enumerate(parts):
            inputs += ["-i", clip]
            chains.append("[%d:v]trim=0:%.3f,setpts=PTS-STARTPTS,scale=%d:%d,setsar=1[c%d]"
                          % (j + 1, max(t1 - t0, 1.0 / FPS), tw, th, j + 1))
        chains.append("".join("[c%d]" % j for j in range(len(parts) + 1)) +
                      "concat=n=%d:v=1:a=0,trim=0:%.3f[v]" % (len(parts) + 1, LENGTH))
        run([FFMPEG, "-y"] + inputs + ["-filter_complex", ";".join(chains), "-map", "[v]",
             "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-r", str(FPS), out])
        screens.append(out)
    print("%d screens made" % len(screens))
    return screens


def assemble(screens, size):
    """All screens on one 360 frame, fade in/out, H.264 for the headset."""
    inputs = ["-f", "lavfi", "-t", str(LENGTH), "-i", "color=black:s=%dx%d:r=%d" % (W, H, FPS)]
    chains, last = [], "[0:v]"
    n = 0
    for i, (scr, (x0, y, sw, sh, twin)) in enumerate(zip(screens, layout())):
        inputs += ["-i", scr]
        # the screen in front and its twin behind; one crossing the seam is drawn twice (wrapped around)
        spots = [x + dx for x in (x0, twin) for dx in ((0, -W) if x + sw > W else (0,))]
        chains.append("[%d:v]split=%d%s" % (i + 1, len(spots), "".join("[s%d_%d]" % (i, k) for k in range(len(spots)))))
        for k, x in enumerate(spots):
            tag = "[o%d]" % n
            chains.append("%s[s%d_%d]overlay=%d:%d%s" % (last, i, k, x + GAP // 2, y + GAP // 2, tag))
            last, n = tag, n + 1
    chains.append("%sfade=t=in:st=0:d=0.3,fade=t=out:st=%.2f:d=%.2f,format=yuv420p[v]"
                  % (last, LENGTH - FADE_OUT, FADE_OUT))
    run([FFMPEG, "-y"] + inputs + ["-filter_complex", ";".join(chains), "-map", "[v]", "-an",
         "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-profile:v", "high", "-level", "5.2",
         "-r", str(FPS), "-movflags", "+faststart", OUT])
    print("film:", OUT)


def preview():
    run([FFMPEG, "-y", "-i", OUT, "-vf", "scale=1280:640", "-c:v", "libx264", "-crf", "26", "-an", PREVIEW])
    for t in (2, 5, 8):
        run([FFMPEG, "-y", "-ss", str(t), "-i", OUT, "-frames:v", "1", "-vf", "scale=1600:800",
             os.path.join(BASE, "intro_frame_%ds.jpg" % t)])
    print("preview:", PREVIEW)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview-only", action="store_true")
    a = ap.parse_args()
    if not a.preview_only:
        snips, size = cut_snippets()
        screens = screen_videos(snips, size)
        assemble(screens, size)
    preview()
