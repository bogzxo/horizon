#!/usr/bin/env python3
"""Runs the self-driving tests of both builds headless and writes validation.json.

usage: run_validation.py <baseline-testing-bin> <ph2-testing-bin> <baseline-hex-bin> <ph2-hex-bin> <out.json>
"""
import json, os, re, subprocess, sys

b_test, p_test, b_hex, p_hex, out = sys.argv[1:6]
seconds = 45
script = os.path.join(os.path.dirname(out), f"quit{seconds}.txt")
open(script, "w").write(f"{seconds} quit\n")

def env_for(with_script=True):
    e = dict(os.environ)
    e.update({"DISPLAY": ":99", "MESA_GL_VERSION_OVERRIDE": "4.6", "MESA_GLSL_VERSION_OVERRIDE": "460", "HORIZON_SHADER_CACHE": "off"})
    if with_script: e["HORIZON_INPUT_SCRIPT"] = script
    return e

checks_re = re.compile(r"(\d+) of (\d+) checks passed")
fail_re = re.compile(r"FAIL")
gl_err_re = re.compile(r"\[Error\] \[DebugSource")

def run(exe_dir, exe, args, with_script=True, timeout=seconds + 120):
    try:
        proc = subprocess.run([os.path.join(exe_dir, exe)] + args, cwd=exe_dir, env=env_for(with_script), capture_output=True, text=True, timeout=timeout)
        out_text = proc.stdout + proc.stderr
    except subprocess.TimeoutExpired as e:
        out_text = (e.stdout or "") + (e.stderr or "") + "\n[timeout]"
    checks = checks_re.findall(out_text)
    summary = "; ".join(f"{a}/{t}" for a, t in checks) if checks else ("no checks reported" + (" (timeout)" if "[timeout]" in out_text else ""))
    fails = len([l for l in out_text.splitlines() if "FAIL" in l and "checks passed" not in l])
    if fails: summary += f" ({fails} FAIL lines)"
    return summary, len(gl_err_re.findall(out_text)), out_text

result = {"selftests": {}, "tools": {}}
for scene in ["ui-selftest", "ui-layout-selftest", "ui-controls-selftest", "ui-navigation-selftest", "tilemap", "tweens"]:
    b, bgl, _ = run(b_test, "Horizon.Testing", [scene])
    p, pgl, ptext = run(p_test, "Horizon.Testing", [scene])
    result["selftests"][f"Horizon.Testing {scene}"] = {"before": b, "after": p, "before_gl": bgl, "after_gl": pgl}
    print(scene, "before:", b, bgl, "after:", p, pgl, flush=True)

# Hex clicks through itself and leaves when it is done
for label, d in [("before", b_hex), ("after", p_hex)]:
    s, gl, text = run(d, "Horizon.Hex", ["--selftest", "--exit"], with_script=False, timeout=240)
    result["tools"].setdefault("Horizon.Hex --selftest", {})[label] = s
    result["tools"]["Horizon.Hex --selftest"][label + "_gl"] = gl
    print("hex", label, s, gl, flush=True)

json.dump(result, open(out, "w"), indent=1)
print("wrote", out)
