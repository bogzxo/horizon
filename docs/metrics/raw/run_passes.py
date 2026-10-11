#!/usr/bin/env python3
"""Runs example scenes (and optionally the game) and collects what the GPU spent on every pass, as the loop log
prints it with HORIZON_LOG_LOOPS. Works headless on lavapipe, where the numbers are big but the shares are honest.

usage: run_passes.py <label> <out.json> <exe> <scene> [<scene> ...]
       run_passes.py <label> <out.json> <path/to/Fighter2D> --map japan
The exe is run from its own folder. Set HORIZON_PASSES_SECONDS for how long each run goes (default 10).
"""
import json, os, re, statistics, subprocess, sys

label, out_path, exe = sys.argv[1], sys.argv[2], sys.argv[3]
rest = sys.argv[4:]
seconds = int(os.environ.get("HORIZON_PASSES_SECONDS", "10"))

bin_dir = os.path.dirname(os.path.abspath(exe))
script = os.path.join(os.path.dirname(os.path.abspath(out_path)), f"quit{seconds}.txt")
with open(script, "w") as f:
    f.write(f"{seconds} quit\n")

env = dict(os.environ)
env.update({"HORIZON_LOG_LOOPS": "2", "HORIZON_INPUT_SCRIPT": script})

gpu_re = re.compile(r"GPU ([\d.,]+) ms a frame(.*)")
pass_re = re.compile(r", (>*)([^,>][^,]*?) ([\d.,]+)(?=,|$)")
render_re = re.compile(r"Render at (\d+) a second, ([\d.,]+) ms a turn")

def number(text):
    return float(text.replace(",", "."))

# The game is one run with its arguments, the host is one run a scene
runs = [(" ".join(rest), rest)] if rest and rest[0].startswith("--") else [(scene, [scene]) for scene in rest]

results = {}
for name, args in runs:
    proc = subprocess.run([os.path.abspath(exe)] + args, cwd=bin_dir, env=env, capture_output=True, text=True, timeout=seconds + 240, encoding="utf-8", errors="replace")
    out = proc.stdout + proc.stderr
    frames = []
    passes = {}
    for line in out.splitlines():
        m = gpu_re.search(line)
        if not m:
            continue
        frames.append(number(m.group(1)))
        for depth, pname, ms in pass_re.findall(m.group(2)):
            key = (">" * len(depth)) + pname.strip()
            passes.setdefault(key, []).append(number(ms))
    renders = [(int(a), number(b)) for a, b in render_re.findall(out)]

    # The first log or two are warm up (the shaders compiling), the rest is steady
    def steady(samples):
        tail = samples[len(samples) // 2:] if len(samples) > 3 else samples
        return statistics.mean(tail) if tail else None
    results[name] = {
        "exit": proc.returncode,
        "gpu_ms": steady(frames),
        "passes": {k: steady(v) for k, v in passes.items()},
        "render_fps": steady([r[0] for r in renders]) if renders else None,
        "render_ms": steady([r[1] for r in renders]) if renders else None,
        "errors": [l for l in out.splitlines() if "[Error]" in l][:10],
    }
    print(f"{label} {name}: GPU {results[name]['gpu_ms']} ms, " + ", ".join(f"{k} {v:.1f}" for k, v in results[name]["passes"].items()), flush=True)

json.dump({"label": label, "seconds": seconds, "runs": results}, open(out_path, "w"), indent=1)
print(f"wrote {out_path}")
