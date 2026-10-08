import json, os, re, subprocess, sys
root = sys.argv[1]; label = sys.argv[2]
def files(ext_re, exclude=("/bin/", "/obj/", "/.git/", "Build History")):
    out = []
    for d, _, fs in os.walk(root):
        if any(x in d for x in exclude): continue
        for f in fs:
            if re.search(ext_re, f): out.append(os.path.join(d, f))
    return out
def loc(paths):
    n = 0
    for p in paths:
        try: n += sum(1 for _ in open(p, encoding="utf-8", errors="ignore"))
        except: pass
    return n
cs = files(r"\.cs$")
engine_cs = [p for p in cs if not any(s in p for s in ("Horizon.Testing", "Horizon.Tests", "Horizon.Hex", "Horizon.HIDL.Editor", "Horizon.HIDL/"))]
shaders = files(r"\.(vert|frag|vs|fs|gs|comp|glsl)$")
shaders = [p for p in shaders if "Horizon.Testing" not in p]
versions = {}
for p in shaders:
    with open(p, errors="ignore") as f:
        first = f.readline().strip()
    m = re.match(r"#version\s+(\d+)", first)
    versions[m.group(1) if m else "none (include)"] = versions.get(m.group(1) if m else "none (include)", 0) + 1
csproj = files(r"\.csproj$")
engine_projects = [p for p in csproj if not any(s in p for s in ("Horizon.Testing", "Horizon.Tests", "Horizon.Hex", "Horizon.HIDL.Editor"))]
result = {
    "label": label,
    "engine_projects": len(engine_projects),
    "engine_project_names": sorted(os.path.basename(p)[:-7] for p in engine_projects),
    "all_projects": len(csproj),
    "engine_cs_files": len(engine_cs), "engine_cs_loc": loc(engine_cs),
    "all_cs_files": len(cs), "all_cs_loc": loc(cs),
    "shader_files": len(shaders), "shader_loc": loc(shaders), "shader_versions": versions,
    "glsl_versions_in_use": sorted(v for v in versions if v != "none (include)"),
    "bind_based_buffer_calls": sum(open(p, errors="ignore").read().count("VertexAttribPointer(") for p in engine_cs),
    "named_uniform_camera_sets": sum(len(re.findall(r'"uCamera(View|Projection)"', open(p, errors="ignore").read())) for p in engine_cs),
    "encodeMotion_copies": sum(open(p, errors="ignore").read().count("vec2 encodeMotion") for p in shaders),
    "project_references": sum(open(p, errors="ignore").read().count("<ProjectReference") for p in csproj),
    "package_references": sum(open(p, errors="ignore").read().count("<PackageReference") for p in engine_projects),
}
print(json.dumps(result, indent=1))
