#!/usr/bin/env python3
"""Runs the Horizon example scenes on Windows, on the real GPU, and collects loop metrics into JSON. The same
numbers run_scenes.py collects headless on Linux, so the two can sit side by side in the workbook.

usage: run_scenes_windows.py <testing-bin-dir> <label> <out.json> [seconds] [passes]
"""
import json, os, re, subprocess, sys, statistics, time

bin_dir, label, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
seconds = int(sys.argv[4]) if len(sys.argv) > 4 else 15
passes = int(sys.argv[5]) if len(sys.argv) > 5 else 2

# The ids of Host/TestCatalog.cs. The examples changed names in October 2026 (primitives is shapes, ui-screens is
# ui-layouts, the self tests are the check- ones), the old ids still start the nearest thing, so a build from before
# and a build from after can be run with either list, but the numbers of a scene that was rewritten in between
# (sprites, tilemap, lighting, post, the ui ones) are not the numbers of the same scene
SCENES = ["quickstart", "sprites", "shapes", "input", "camera", "entities", "tweens", "transitions", "tilemap",
          "particles", "fluid", "ui", "ui-layouts", "ui-controls", "ui-skin", "lighting", "pathtraced", "post",
          "town", "pacing",
          "check-ui", "check-ui-layout", "check-ui-controls", "check-ui-navigation", "check-tilemap"]

script = os.path.join(os.path.dirname(os.path.abspath(out_path)), f"quit{seconds}.txt")
with open(script, "w") as f:
    f.write(f"{seconds} quit\n")

env = dict(os.environ)
env.update({
    "HORIZON_LOG_LOOPS": "1",
    "HORIZON_INPUT_SCRIPT": script,
    "HORIZON_SHADER_CACHE": "off",
})

render_re = re.compile(r"Render at (\d+) a second, ([\d.,]+) ms a turn \(([\d.,]+) at worst\), (\d+) bytes a turn")
logic_re = re.compile(r"Logic at (\d+) a second, ([\d.,]+) ms a turn \(([\d.,]+) at worst\), (\d+) bytes a turn")
physics_re = re.compile(r"Physics at (\d+) a second, ([\d.,]+) ms a turn \(([\d.,]+) at worst\), (\d+) bytes a turn")
setup_re = re.compile(r"Set up '(\w+)' in (\d+) ms: (\d+) ms making it, (\d+) ms warming it up")
final_re = re.compile(r"Successfully finalized (\d+) (\w+)!")
checks_re = re.compile(r"(\d+) of (\d+) checks passed")
gl_err_re = re.compile(r"\[(Error|Warning)\] \[(DebugSource\w+)\]")
exe = os.path.join(bin_dir, "Horizon.Testing.exe")

def number(text):
    # The log is written in whatever the machine's culture is, which here has a comma for a decimal point
    return float(text.replace(",", "."))

results = {}
for scene in SCENES:
    runs = []
    for p in range(passes):
        t0 = time.time()
        try:
            proc = subprocess.run([exe, scene], cwd=bin_dir, env=env, capture_output=True, text=True, timeout=seconds + 90, encoding="utf-8", errors="replace")
            out = proc.stdout + proc.stderr
            code = proc.returncode
        except subprocess.TimeoutExpired as e:
            out = (e.stdout or "") + (e.stderr or "")
            code = "timeout"
        wall = time.time() - t0
        renders = [tuple(map(number, m)) for m in render_re.findall(out)]
        logics = [tuple(map(number, m)) for m in logic_re.findall(out)]
        physics = [tuple(map(number, m)) for m in physics_re.findall(out)]

        # The first couple of logs include warm-up (and the shaders compiling), use the second half of the samples
        def steady(samples):
            if not samples:
                return None
            tail = samples[len(samples) // 2:] if len(samples) > 3 else samples
            return {
                "rate": statistics.mean(s[0] for s in tail),
                "ms": statistics.mean(s[1] for s in tail),
                "worst_ms": max(s[2] for s in tail),
                "bytes_per_turn": statistics.mean(s[3] for s in tail),
            }
        setup = setup_re.search(out)
        finals = {kind: int(n) for n, kind in final_re.findall(out)}
        checks = checks_re.findall(out)
        gl_errors = len([m for m in gl_err_re.findall(out)])
        runs.append({
            "exit": code, "wall_s": wall,
            "render": steady(renders), "logic": steady(logics), "physics": steady(physics),
            "setup_ms": int(setup.group(2)) if setup else None,
            "setup_make_ms": int(setup.group(3)) if setup else None,
            "setup_warm_ms": int(setup.group(4)) if setup else None,
            "finalized": finals,
            "checks": [(int(a), int(b)) for a, b in checks],
            "gl_errors": gl_errors,
            "errors": [l for l in out.splitlines() if "[Error]" in l][:10],
        })
        r = runs[-1]["render"]
        print(f"{label} {scene} pass {p + 1}: exit {code}, {r['rate']:.0f} fps {r['ms']:.2f} ms" if r else f"{label} {scene} pass {p + 1}: exit {code}, no render samples", flush=True)
    results[scene] = runs

json.dump({"label": label, "seconds": seconds, "scenes": results}, open(out_path, "w"), indent=1)
print(f"wrote {out_path}")
