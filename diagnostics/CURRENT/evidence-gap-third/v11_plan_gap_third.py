import json, pathlib

src = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\v11-wall\plan-06f-hztxt.json")
out = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\v11-wall\plan-06h-gap-third.json")
plan = json.loads(src.read_text(encoding="utf-8"))
for step in plan["steps"]:
    if step.get("op") != "callout":
        continue
    gap = round(float(step["height"]) / 3.0, 6)
    c = step["vertices"][2]
    step["position"] = [c[0], round(c[1] + gap, 6)]
    step["annotation_offset"] = [0, -gap]
    print(step["id"], "gap", gap, "C", c, "position", step["position"])
out.write_text(json.dumps(plan, ensure_ascii=False, indent=1), encoding="utf-8")
print("written", out)
