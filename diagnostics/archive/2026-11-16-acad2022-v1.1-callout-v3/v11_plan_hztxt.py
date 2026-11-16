import json, math, pathlib

src = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\v11-wall\plan-06e-parallel.json")
out = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\v11-wall\plan-06f-hztxt.json")
plan = json.loads(src.read_text(encoding="utf-8"))
widths = {
    "co-wall": 1.11410011248594,
    "co-plaster": 0.814775028121485,
    "co-coping": 0.5648031496062992,
    "co-footing": 0.47789088863892,
    "co-concrete": 0.5371991001124858,
    "co-ground": 0.4783970753655794,
}
for step in plan["steps"]:
    if step.get("op") != "callout":
        continue
    sid = step["id"]
    actual = widths[sid]
    width = round(actual + 0.04, 4)
    step["width"] = width
    b = step["vertices"][1]
    c = [round(b[0] - width, 4), b[1]]
    step["vertices"][2] = c
    step["position"] = c
    print(sid, "actual", actual, "width", width, "C", c)
out.write_text(json.dumps(plan, ensure_ascii=False, indent=1), encoding="utf-8")
print(out)
