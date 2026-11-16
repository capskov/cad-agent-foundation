import json, math, pathlib

src = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\v11-wallAI\plan-06d-tight.json")
out = pathlib.Path(r"F:\AI\cad-agent-foundation-acad2022\cad2022-test\v11-wall\plan-06e-parallel.json")
plan = json.loads(src.read_text(encoding="utf-8"))

# Keep the bottom two labels in anchor order and put them on a common B.x
# so all six diagonals are parallel 45-degree lines.
updates = {
    "co-concrete": {"B": [2.79, 0.86], "C": [2.1771, 0.86]},
    "co-ground": {"B": [2.79, 1.03], "C": [2.258, 1.03]},
}
for step in plan["steps"]:
    if step.get("id") in updates:
        step["vertices"][1] = updates[step["id"]]["B"]
        step["vertices"][2] = updates[step["id"]]["C"]
        step["position"] = updates[step["id"]]["C"]

for step in plan["steps"]:
    if step.get("op") != "callout":
        continue
    A, B, C = step["vertices"]
    dx, dy = abs(A[0] - B[0]), abs(A[1] - B[1])
    angle = math.degrees(math.atan2(dy, dx))
    interior = 180 - angle
    assert abs(angle - 45.0) < 0.01, (step["id"], angle)
    assert interior > 90.0, (step["id"], interior)
    assert abs((B[0] - C[0]) - step["width"]) < 0.002, (step["id"], B[0] - C[0], step["width"])
    print(step["id"], "A", A, "B", B, "C", C, "angle", round(angle, 2), "interior", round(interior, 2))

out.write_text(json.dumps(plan, ensure_ascii=False, indent=1), encoding="utf-8")
print(out)
