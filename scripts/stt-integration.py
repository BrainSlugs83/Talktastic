"""
Whisper-based speech-to-text integration test for say.exe.

Drives the built AOT executable (dist/say.exe) across every audio code path
(SAPI / Piper / RVC x stream / pitch / device / file), captures the result
either from the WASAPI loopback of the default speaker (streamed/device cases)
or from the written file, transcribes it with faster-whisper, and asserts that
the spoken words match the input above a recall threshold.

Requires (installed into the conda env by test.ps1 -Stt):
    faster-whisper  soundcard  numpy

Machine-specific knobs (override via environment variables):
    STT_SAY     full path to say.exe        (default: ../dist/say.exe)
    STT_DEVICE  buffered -d device substring (default: "Speakers (High")

This test plays audio aloud and depends on local audio hardware, the 'amy'
Piper voice, and the 'homer' RVC model, so it is opt-in (test.bat --stt) and
is not part of the headless unit-test run.
"""

import os
import re
import sys
import time
import threading
import subprocess
import tempfile
import warnings

import numpy as np
import soundcard as sc

warnings.filterwarnings("ignore")
from faster_whisper import WhisperModel

_HERE = os.path.dirname(os.path.abspath(__file__))
SAY = os.environ.get("STT_SAY", os.path.join(_HERE, "..", "dist", "say.exe"))
TMP = tempfile.mkdtemp(prefix="say-stt-")
DEV = os.environ.get("STT_DEVICE", "Speakers (High")

SHORT = "The quick brown fox jumps over the lazy dog"
MEDIUM = ("The quick brown fox jumps over the lazy dog. "
          "She sells sea shells by the sea shore.")
LONG = ("The quick brown fox jumps over the lazy dog. She sells sea shells by the sea shore. "
        "Peter Piper picked a peck of pickled peppers. "
        "How much wood would a wood chuck chuck if a wood chuck could chuck wood.")
TINY = "Hello there friend."

THRESH = 0.6

model = WhisperModel("base.en", device="cpu", compute_type="int8")
spk = sc.default_speaker()
mic = sc.get_microphone(spk.name, include_loopback=True)


def norm(s):
    return re.sub(r'[^a-z0-9 ]', ' ', s.lower()).split()


def recall(expected, got):
    from collections import Counter
    e = norm(expected)
    g = norm(got)
    if not e:
        return 0.0
    gc = Counter(g)
    hit = 0
    for w in e:
        if gc[w] > 0:
            gc[w] -= 1
            hit += 1
    return hit / len(e)


def transcribe(source):
    segs, _ = model.transcribe(source, language="en", beam_size=1)
    return " ".join(s.text for s in segs).strip()


def run_capture(args, timeout=240, capture_stderr=False):
    frames = []
    stop = threading.Event()

    def rec():
        with mic.recorder(samplerate=16000, channels=1) as r:
            while not stop.is_set():
                frames.append(r.record(numframes=1600))

    t = threading.Thread(target=rec)
    t.start()
    time.sleep(0.5)
    p = subprocess.run([SAY] + args, capture_output=True, text=True, timeout=timeout)
    time.sleep(0.8)
    stop.set()
    t.join()
    a = np.concatenate(frames).flatten().astype(np.float32)
    if capture_stderr:
        return transcribe(a), p.returncode, p.stderr
    return transcribe(a), p.returncode


# (name, args, expected, mode, outfile)
CASES = [
    ("sapi-stream",      [SHORT, "-v", "David"],                                          SHORT, "dev",  None),
    ("sapi-pitch-buf",   [SHORT, "-v", "David", "-p", "low"],                             SHORT, "dev",  None),
    ("sapi-device-buf",  [SHORT, "-v", "David", "-d", DEV],                               SHORT, "dev",  None),
    ("sapi-file",        [SHORT, "-v", "David", "-o", os.path.join(TMP, "v_sapi.wav")],   SHORT, "file", os.path.join(TMP, "v_sapi.wav")),
    ("piper-stream",     [SHORT, "-v", "amy"],                                            SHORT, "dev",  None),
    ("piper-pitch-buf",  [SHORT, "-v", "amy", "-p", "low"],                               SHORT, "dev",  None),
    ("piper-device-buf", [SHORT, "-v", "amy", "-d", DEV],                                 SHORT, "dev",  None),
    ("piper-file",       [SHORT, "-v", "amy", "-o", os.path.join(TMP, "v_piper.wav")],    SHORT, "file", os.path.join(TMP, "v_piper.wav")),
    ("rvc-stream-short", [SHORT, "-v", "David", "--rvc", "homer"],                        SHORT, "dev",  None),
    ("rvc-tiny",         [TINY,  "-v", "David", "--rvc", "homer"],                        TINY,  "dev",  None),
    ("rvc-medium",       [MEDIUM,"-v", "David", "--rvc", "homer"],                        MEDIUM,"dev",  None),
    ("rvc-stream-long",  [LONG,  "-v", "David", "--rvc", "homer"],                        LONG,  "dev",  None),
    ("rvc-stream-long-cpu",  [LONG, "-v", "David", "--rvc", "homer", "--no-gpu"],         LONG,  "dev",  None),
    ("rvc-cli-flags",    [LONG,  "-v", "David", "--rvc", "homer", "--rvc-chunk-size", "1.0", "--rvc-padding-length", "0.2"], LONG, "dev", None),
    ("rvc-device-buf",   [SHORT, "-v", "David", "--rvc", "homer", "-d", DEV],             SHORT, "dev",  None),
    ("rvc-file",         [SHORT, "-v", "David", "--rvc", "homer", "-o", os.path.join(TMP, "v_rvc.wav")], SHORT, "file", os.path.join(TMP, "v_rvc.wav")),
]

results = []
for name, args, expected, mode, outfile in CASES:
    print(f"\n=== {name} ===", flush=True)
    try:
        if mode == "file":
            p = subprocess.run([SAY] + args, capture_output=True, text=True, timeout=240)
            rc = p.returncode
            tx = transcribe(outfile)
        else:
            tx, rc = run_capture(args)
        r = recall(expected, tx)
        ok = rc == 0 and r >= THRESH
        print(f"rc={rc} recall={r:.2f} -> {'PASS' if ok else 'FAIL'}")
        print(f"  expected: {expected}")
        print(f"  got     : {tx}")
        results.append((name, ok, r, tx))
    except Exception as e:
        print(f"  ERROR: {e}")
        results.append((name, False, 0.0, f"ERROR {e}"))
    time.sleep(0.4)

print("\n\n================ SUMMARY ================")
for name, ok, r, tx in results:
    print(f"  [{'PASS' if ok else 'FAIL'}] {name:18s} recall={r:.2f}")
fails = [n for n, ok, r, tx in results if not ok]

# Extra: verify the corrupt-output detector + ZCR speech-range + auto-retry-on-CPU instrumentation
# on a long GPU run. The retry path SHOULD be present and warm even if it never fires.
print("\n=== rvc-stream-long-verbose (stderr inspection) ===", flush=True)
try:
    tx, rc, err = run_capture([LONG, "-v", "David", "--rvc", "homer", "--verbose"], capture_stderr=True)
    # The final "warning: RVC output looks corrupt" line is what we DON'T want; the per-chunk
    # "GPU output looks corrupt; retrying on CPU" lines and the "retries=N/M" token are
    # diagnostics that should always be present (N=0 is the healthy case).
    final_corrupt_warned = "looks corrupt even after cpu retry" in err.lower()
    zcr_match = re.search(r"\[rvc\] output:.*zcr=(\d+(?:\.\d+)?)/s", err)
    zcr = float(zcr_match.group(1)) if zcr_match else None
    retries_match = re.search(r"retries=(\d+)/(\d+)", err)
    retries = (int(retries_match.group(1)), int(retries_match.group(2))) if retries_match else None
    r = recall(LONG, tx)
    speech_zcr = zcr is not None and zcr >= 1500.0
    retries_instrumented = retries is not None and retries[1] > 0
    ok = (
        rc == 0
        and not final_corrupt_warned
        and speech_zcr
        and retries_instrumented
        and r >= THRESH
    )
    print(f"rc={rc} recall={r:.2f} zcr={zcr} retries={retries} final_warned={final_corrupt_warned} -> {'PASS' if ok else 'FAIL'}")
    if not ok:
        fails.append("rvc-stream-long-verbose")
        print(f"  expected: zcr>=1500, retries instrumented (N/M with M>0), no final corrupt-output warning")
        print(f"  stderr:\n{err}")
except Exception as e:
    print(f"  ERROR: {e}")
    fails.append("rvc-stream-long-verbose")

print(f"\n{len(results) + 1 - len(fails)}/{len(results) + 1} passed")
if fails:
    print("FAILURES:", ", ".join(fails))
sys.exit(1 if fails else 0)
