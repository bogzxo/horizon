#!/usr/bin/env python3
"""Builds the project-health-2 metrics workbook out of the JSON the other tools wrote.

usage: make_workbook.py <metrics-dir> <out.xlsx>
"""
import json, os, statistics, sys
from openpyxl import Workbook
from openpyxl.styles import Alignment, Font, PatternFill, Border, Side
from openpyxl.utils import get_column_letter

metrics, out = sys.argv[1], sys.argv[2]

def load(name):
    p = os.path.join(metrics, name)
    return json.load(open(p)) if os.path.exists(p) else None

base_scenes = load("baseline_scenes3.json")
ph2_scenes = load("ph2_scenes3.json")
base_struct = load("structure_baseline.json")
ph2_struct = load("structure_ph2.json")
base_bench = load("bench_baseline.json")
ph2_bench = load("bench_ph2.json")
build = load("build_times.json")
validation = load("validation.json")

HEAD = Font(bold=True, color="FFFFFF")
HEAD_FILL = PatternFill("solid", fgColor="1F3A5F")
SUB = Font(bold=True)
GOOD = PatternFill("solid", fgColor="D9EAD3")
BAD = PatternFill("solid", fgColor="F4CCCC")
NOTE = Font(italic=True, color="555555")
thin = Side(style="thin", color="BBBBBB")
BORDER = Border(left=thin, right=thin, top=thin, bottom=thin)

wb = Workbook()

def sheet(title, headers, rows, widths=None, notes=None, colour_delta_col=None, lower_is_better=True):
    ws = wb.create_sheet(title)
    r = 1
    if notes:
        for n in notes:
            ws.cell(row=r, column=1, value=n).font = NOTE
            r += 1
        r += 1
    for c, h in enumerate(headers, 1):
        cell = ws.cell(row=r, column=c, value=h)
        cell.font = HEAD; cell.fill = HEAD_FILL; cell.alignment = Alignment(wrap_text=True, vertical="center"); cell.border = BORDER
    ws.row_dimensions[r].height = 32
    head_row = r
    for row in rows:
        r += 1
        for c, v in enumerate(row, 1):
            cell = ws.cell(row=r, column=c, value=v)
            cell.border = BORDER
            if isinstance(v, float):
                cell.number_format = "0.00"
        if colour_delta_col is not None and isinstance(row[colour_delta_col], (int, float)):
            d = row[colour_delta_col]
            better = d < 0 if lower_is_better else d > 0
            if abs(d) >= 0.5:
                ws.cell(row=r, column=colour_delta_col + 1).fill = GOOD if better else BAD
    for c in range(1, len(headers) + 1):
        ws.column_dimensions[get_column_letter(c)].width = (widths[c - 1] if widths else 18)
    ws.freeze_panes = ws.cell(row=head_row + 1, column=2)
    return ws

# ---------------------------------------------------------------- Summary (filled at the end)
summary = wb.active
summary.title = "Summary"

# ---------------------------------------------------------------- Scenes
def median_of(runs, loop, key):
    vals = [run[loop][key] for run in runs if run.get(loop)]
    return statistics.median(vals) if vals else None

def steady_bytes(runs, loop):
    # The loops smooth their allocation figure, and the warm up of a slow scene (a few frames a second) bleeds into
    # it for the whole run: the lowest of the passes is nearest to what a steady frame really allocates
    vals = [run[loop]["bytes_per_turn"] for run in runs if run.get(loop)]
    return min(vals) if vals else None

scene_rows = []
if base_scenes and ph2_scenes:
    # Scenes the branch added (quickstart, pathtraced) have no baseline, they get a row with the baseline side blank
    every = list(base_scenes["scenes"]) + [s for s in ph2_scenes["scenes"] if s not in base_scenes["scenes"]]
    for scene in every:
        b = base_scenes["scenes"].get(scene, []); p = ph2_scenes["scenes"].get(scene, [])
        if not p: continue
        b_ms, p_ms = median_of(b, "render", "ms"), median_of(p, "render", "ms")
        b_fps, p_fps = median_of(b, "render", "rate"), median_of(p, "render", "rate")
        b_worst, p_worst = median_of(b, "render", "worst_ms"), median_of(p, "render", "worst_ms")
        b_alloc, p_alloc = steady_bytes(b, "render"), steady_bytes(p, "render")
        b_setup = statistics.median([r["setup_ms"] for r in b if r["setup_ms"] is not None] or [0]) if b else None
        p_setup = statistics.median([r["setup_ms"] for r in p if r["setup_ms"] is not None] or [0])
        b_logic, p_logic = median_of(b, "logic", "ms"), median_of(p, "logic", "ms")
        b_gl = sum(r["gl_errors"] for r in b); p_gl = sum(r["gl_errors"] for r in p)
        b_checks = "; ".join(f"{a}/{t}" for r in b for a, t in r["checks"]) or ""
        p_checks = "; ".join(f"{a}/{t}" for r in p for a, t in r["checks"]) or ""
        delta = (p_ms - b_ms) / b_ms * 100.0 if b_ms and p_ms else None
        scene_rows.append([scene, b_ms, p_ms, delta, b_fps, p_fps, b_worst, p_worst, b_logic, p_logic,
                           b_alloc, p_alloc, b_setup, p_setup, b_gl, p_gl, b_checks, p_checks])

sheet("Scenes (headless)",
      ["Scene", "Render ms/frame before", "Render ms/frame after", "Render ms delta %", "FPS before", "FPS after",
       "Worst frame ms before", "Worst frame ms after", "Logic ms/tick before", "Logic ms/tick after",
       "Render bytes/frame before (steady)", "Render bytes/frame after (steady)", "Scene set-up ms before", "Scene set-up ms after",
       "GL debug errors before", "GL debug errors after", "Self-checks before", "Self-checks after"],
      scene_rows,
      widths=[22, 14, 14, 12, 10, 10, 12, 12, 12, 12, 16, 16, 12, 12, 10, 10, 14, 14],
      notes=["Every example scene of Horizon.Testing run for 10 seconds, 3 passes each, median of the passes. Before = development (9839639), after = project-health-2.",
             "Headless on this machine: Xvfb + Mesa llvmpipe (a software OpenGL 4.5 rasteriser forced to report 4.6), 1600x900. Frame times are dominated by software rasterisation,",
             "so they measure the CPU cost of the whole frame including the 'GPU' work; the relative change is what matters, absolute numbers are not what a real GPU gives.",
             "Render ms/frame is the render loop's smoothed work time per turn as the engine logs it (HORIZON_LOG_LOOPS). Logic is the simulation's logic step. The simulation ran at its 120 Hz target in every run on both builds.",
             "Bytes/frame is the engine's smoothed allocation figure for the render thread; a scene's set-up bleeds into it (slow scenes most), so the lowest pass is shown as the steady figure.",
             "GL debug errors: messages of severity high from the driver's debug output (KHR_debug) over the run. Self-checks: the scenes that check themselves (tile map, tweens, UI navigation) and how many checks passed."],
      colour_delta_col=3)

# ---------------------------------------------------------------- Self tests
if validation:
    rows = []
    for name, v in validation.get("selftests", {}).items():
        rows.append([name, v.get("before", ""), v.get("after", ""), v.get("before_gl", 0), v.get("after_gl", 0)])
    for name, v in validation.get("tools", {}).items():
        rows.append([name, v.get("before", ""), v.get("after", ""), "", ""])
    sheet("Validation", ["Run", "Before", "After", "GL errors before", "GL errors after"], rows, widths=[34, 40, 40, 14, 14],
          notes=["The self-driving tests of Horizon.Testing (they click through their own UI and check what happened), Horizon.Hex's self test, and the unit tests, before and after.",
                 "Run for 45 seconds headless; a result is 'passed/total checks'."])

# ---------------------------------------------------------------- Structure
if base_struct and ph2_struct:
    def row(name, a, b, fmt=str):
        return [name, a, b]
    rows = [
        ["Engine projects (assemblies)", base_struct["engine_projects"], ph2_struct["engine_projects"]],
        ["Projects in the repository", base_struct["all_projects"], ph2_struct["all_projects"]],
        ["Project references between projects", base_struct["project_references"], ph2_struct["project_references"]],
        ["NuGet package references of the engine projects", base_struct["package_references"], ph2_struct["package_references"]],
        ["Engine C# files", base_struct["engine_cs_files"], ph2_struct["engine_cs_files"]],
        ["Engine C# lines", base_struct["engine_cs_loc"], ph2_struct["engine_cs_loc"]],
        ["All C# files (engine, tools, tests)", base_struct["all_cs_files"], ph2_struct["all_cs_files"]],
        ["All C# lines", base_struct["all_cs_loc"], ph2_struct["all_cs_loc"]],
        ["Shader files shipped with the engine", base_struct["shader_files"], ph2_struct["shader_files"]],
        ["Shader lines", base_struct["shader_loc"], ph2_struct["shader_loc"]],
        ["GLSL versions in use", ", ".join(base_struct["glsl_versions_in_use"]), ", ".join(ph2_struct["glsl_versions_in_use"])],
        ["Shaders per GLSL version", json.dumps(base_struct["shader_versions"]), json.dumps(ph2_struct["shader_versions"])],
        ["Copies of encodeMotion() across shaders", base_struct["encodeMotion_copies"], ph2_struct["encodeMotion_copies"]],
        ["C# sites setting uCameraView/uCameraProjection uniforms", base_struct["named_uniform_camera_sets"], ph2_struct["named_uniform_camera_sets"]],
        ["Engine assembly names", ", ".join(base_struct["engine_project_names"]), ", ".join(ph2_struct["engine_project_names"])],
    ]
    if build:
        rows.append(["Clean Release build of the engine and its apps (Testing, Hex, Tests; Fighter2D left out), seconds, this machine", build.get("baseline_s"), build.get("ph2_s")])
        rows.append(["Clean Release build of Horizon.Testing alone, seconds", build.get("baseline_testing_s"), build.get("ph2_testing_s")])
    sheet("Structure", ["Metric", "Before (development)", "After (project-health-2)"], rows, widths=[58, 46, 46],
          notes=["Counted over the working trees of both branches, excluding bin/obj. Shader counts exclude Horizon.Testing's own example shaders."])

# ---------------------------------------------------------------- GL calls per draw (static analysis)
gl_rows = [
    ["SpriteBatchMesh.DrawItems (one run of sprites)", 33, 9,
     "UseProgram, 6 camera/model/nearness uniforms, 4x BindTextureUnit, 4x sampler uniform, 4x texel-size uniform, data-offset uniform, BindBufferBase, VAO+VBO+EBO bind, draw, 3 unbinds, UseProgram(0); plus BindSampler per sampler",
     "CameraBlock.Use (writes 304 bytes only when the camera changes), UseProgram, model + nearness + one texel-size array uniform, one glBindTextures, glBindBufferRange, VAO bind, DrawElementsInstancedBaseInstance; glBindSamplers only when a sampler is used"],
    ["TileMapBatch.Draw (one batch of tiles)", 16, 6,
     "3x BindTextureUnit, 3 sampler uniforms, hasNormal/hasSpecular/texel uniforms, VAO+VBO+EBO bind, draw, 3 unbinds; plus 2 camera matrix uniforms per map",
     "one glBindTextures, hasNormal/hasSpecular/texel uniforms, VAO bind, DrawElementsInstanced; camera from the block"],
    ["ParticleRenderer2D.Render", 22, 11,
     "UseProgram, 9 uniforms incl. view/projection/velocity/motion scale, 4 material uniforms, VAO+VBO+EBO bind, draw, 3 unbinds, UseProgram(0)",
     "CameraBlock.Use, UseProgram, 4 material + 3 motion uniforms + nearness, VAO bind, DrawElementsInstancedBaseInstance"],
    ["PrimitiveRenderer.Render (all shapes)", 7, 7,
     "UseProgram, model + view uniforms, VAO bind, DrawArrays(points) through a geometry shader emitting up to 38 vertices per shape, unbind, UseProgram(0); plus a NamedBufferSubData of the whole shape array 60x a second",
     "CameraBlock.Use, UseProgram, model/nearness/emissive uniforms, glBindBufferRange into a persistent mapped stream, VAO bind, DrawElementsInstanced with a signed-distance-field fragment shader; no upload call at all"],
    ["Physics debug outlines per frame", 9, 7,
     "2x NamedBufferData (vertex + index arrays reallocated every frame), UseProgram, 2 camera uniforms, VAO+VBO+EBO bind, DrawElements(lines), UseProgram(0)",
     "Drawn by the PrimitiveRenderer above: no buffer reallocation, one instanced draw, antialiased lines of any width"],
    ["Renderer2D output to screen", 11, 4,
     "UseProgram, albedo sampler uniform (deferred: +2 samplers, inverse view projection, lights...), VAO+VBO+EBO bind, DrawElements (2 triangles), 3 unbinds, UseProgram(0)",
     "UseProgram, (samplers bound by layout), empty VAO bind, DrawArrays(3) of a vertex-less triangle, UseProgram(0)"],
    ["Post processing pass (typical)", 5, 3,
     "UseProgram, source sampler uniform, 1-2 more sampler uniforms, effect uniforms, draw",
     "UseProgram, effect uniforms, draw: samplers say their unit in the shader"],
    ["GL objects per SpriteBatchMesh", 4, 1,
     "VAO + vertex buffer + element buffer per mesh, plus the persistent SSBO",
     "one StreamBuffer per mesh; the quad (VAO + 2 buffers) is shared by every sprite and shape renderer (UnitQuad)"],
]
sheet("GL calls per draw (static)", ["Draw path", "GL calls before (approx.)", "GL calls after (approx.)", "Before", "After"],
      [[r[0], r[1], r[2], r[3], r[4]] for r in gl_rows], widths=[40, 14, 14, 70, 70],
      notes=["Counted from the source of both branches: the OpenGL calls one draw of each renderer makes on the CPU, not counting the per-frame camera block bind (1 call per frame).",
             "Uniform sets by name are one glUniform* each plus a dictionary lookup; the camera block removes two matrix uploads per draw per shader and replaces them with one 304 byte upload per camera change per frame."],
      colour_delta_col=None)

# ---------------------------------------------------------------- Micro benchmarks
if base_bench and ph2_bench:
    rows = []
    for name in base_bench:
        if name in ph2_bench:
            b, p = base_bench[name], ph2_bench[name]
            rows.append([name, b, p, (p - b) / b * 100.0 if b else None])
    for name in ph2_bench:
        if name not in base_bench:
            rows.append([name, None, ph2_bench[name], None])
    sheet("CPU micro-benchmarks", ["Benchmark", "ms before", "ms after", "delta %"], rows, widths=[36, 14, 14, 12],
          notes=["The same harness compiled against both branches (Release, tiered PGO), run with nothing else going on. Milliseconds per repetition, no GPU involved.",
                 "sprite_blend: blending 200k sprite quads between two ticks the way a frame drawn alongside the simulation does. entity_tree: one UpdateState over 2000 entities with a transform and a component each.",
                 "hidl_loop: the scripting runtime evaluating a 2000-iteration loop. packed_colour_lerp: a million interpolations of packed RGBA colours."],
          colour_delta_col=3)

# ---------------------------------------------------------------- Summary
s = summary
s.column_dimensions["A"].width = 60; s.column_dimensions["B"].width = 26; s.column_dimensions["C"].width = 26; s.column_dimensions["D"].width = 14
s["A1"] = "Horizon project-health-2: before and after"; s["A1"].font = Font(bold=True, size=14)
s["A2"] = "development (9839639) vs project-health-2, measured on 2026-10-08 in a headless Linux container (Mesa llvmpipe software OpenGL)."; s["A2"].font = NOTE
r = 4
for c, h in enumerate(["Headline", "Before", "After", "Change"], 1):
    cell = s.cell(row=r, column=c, value=h); cell.font = HEAD; cell.fill = HEAD_FILL; cell.border = BORDER
def put(label, a, b, change=None, fmt=None):
    global r
    r += 1
    for c, v in enumerate([label, a, b, change], 1):
        cell = s.cell(row=r, column=c, value=v); cell.border = BORDER
        if isinstance(v, float): cell.number_format = fmt or "0.00"
if base_struct and ph2_struct:
    put("Engine assemblies", base_struct["engine_projects"], ph2_struct["engine_projects"], ph2_struct["engine_projects"] - base_struct["engine_projects"])
    put("Project references to maintain", base_struct["project_references"], ph2_struct["project_references"], ph2_struct["project_references"] - base_struct["project_references"])
    put("GLSL versions in use", ", ".join(base_struct["glsl_versions_in_use"]), ", ".join(ph2_struct["glsl_versions_in_use"]), "")
    put("Shader files", base_struct["shader_files"], ph2_struct["shader_files"], ph2_struct["shader_files"] - base_struct["shader_files"])
    put("Engine C# lines", base_struct["engine_cs_loc"], ph2_struct["engine_cs_loc"], ph2_struct["engine_cs_loc"] - base_struct["engine_cs_loc"])
if scene_rows:
    deltas = [row[3] for row in scene_rows if row[3] is not None]
    put("Scenes measured headless (3 passes each)", len(scene_rows), len(scene_rows), "")
    put("Median change in render ms/frame across scenes (%)", "", "", statistics.median(deltas), "0.0")
    put("Scenes with zero GL debug errors", sum(1 for row in scene_rows if row[14] == 0), sum(1 for row in scene_rows if row[15] == 0), "")
    allocs_b = [row[10] for row in scene_rows if row[10] is not None]; allocs_p = [row[11] for row in scene_rows if row[11] is not None]
    put("Median steady render allocation, bytes/frame", statistics.median(allocs_b), statistics.median(allocs_p), "", "0")
for rr in gl_rows[:6]:
    put(f"GL calls per draw: {rr[0]}", rr[1], rr[2], rr[2] - rr[1])
if base_bench and ph2_bench:
    for name in base_bench:
        if name in ph2_bench:
            put(f"CPU: {name} (ms)", base_bench[name], ph2_bench[name], (ph2_bench[name] - base_bench[name]) / base_bench[name] * 100.0 if base_bench[name] else None, "0.0")
if build:
    put("Clean Release build, engine and apps (s)", build.get("baseline_s"), build.get("ph2_s"), "", "0.0")
if validation and "unit_tests" in validation:
    put("Unit tests passing", validation["unit_tests"]["before"], validation["unit_tests"]["after"], "")
r += 2
s.cell(row=r, column=1, value="Sheets: Scenes (headless) has every example scene; Validation the self-tests; Structure the code metrics; GL calls per draw (static) the per-draw call counts read off the source; CPU micro-benchmarks the harness results.").font = NOTE
r += 1
s.cell(row=r, column=1, value="Changes in the Change column are after minus before; for percentages, (after - before) / before. Negative is better for times, calls and bytes.").font = NOTE

wb.save(out)
print("wrote", out)
