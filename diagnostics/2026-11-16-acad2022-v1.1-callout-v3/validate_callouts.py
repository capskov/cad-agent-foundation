import json, math, pathlib, sys

MIN_ANGLE, MAX_ANGLE = 30.0, 80.0
RECOMMENDED = "35-45"


def angle_deg(a, b):
    dx = abs(b[0] - a[0])
    dy = abs(b[1] - a[1])
    if dx == 0:
        return 90.0
    return math.degrees(math.atan2(dy, dx))


def main(plan_path):
    plan = json.loads(pathlib.Path(plan_path).read_text(encoding="utf-8"))
    issues, checked = [], 0
    for step in plan.get("steps", []):
        if step.get("op") != "callout":
            continue
        checked += 1
        cid = step.get("id")
        a, b, c = step["vertices"]
        width = float(step["width"])
        pos = step["position"]
        att = step.get("attachment")
        off = step.get("annotation_offset", [0, 0])
        ang = angle_deg(a, b)
        straight = bool(step.get("leader_straight"))
        constrained = bool(step.get("leader_constrained"))
        text_gap = float(pos[1]) - float(c[1])
        if straight and ang < 0.5:
            pass  # 180° 共线是显式允许的最后手段
        elif ang < 30.0:
            if constrained:
                issues.append(f"{cid}: WARN constrained slope {ang:.1f} < 30 (interior {180-ang:.1f})")
            else:
                issues.append(f"{cid}: A->B slope {ang:.1f} < 30 and not marked leader_constrained")
        elif ang > 80.0:
            issues.append(f"{cid}: A->B angle {ang:.1f} > 80")
        interior = 180.0 - ang
        if ang >= 0.5 and interior <= 90.0:
            issues.append(f"{cid}: bend interior {interior:.1f} is not obtuse")
        if abs(b[1] - c[1]) > 0.001:
            issues.append(f"{cid}: B/C not horizontal ({b[1]} vs {c[1]})")
        if att == "BottomLeft":
            if abs((b[0] - width) - c[0]) > 0.002:
                issues.append(f"{cid}: C.x != B.x-width")
            if abs(pos[0] - c[0]) > 0.002:
                issues.append(f"{cid}: position.x != C.x")
        elif att == "BottomRight":
            if abs((b[0] + width) - c[0]) > 0.002:
                issues.append(f"{cid}: C.x != B.x+width")
            if abs(pos[0] - c[0]) > 0.002:
                issues.append(f"{cid}: position.x != C.x")
        else:
            issues.append(f"{cid}: attachment must be BottomLeft or BottomRight")
        if text_gap < -0.001 or text_gap > 0.02:
            issues.append(f"{cid}: text gap {text_gap:.4f} outside 0..0.02")
        if text_gap > 0.001:
            # SHX font descent compensation: line is below text bottom by text_gap.
            if abs(off[0]) > 1e-6 or abs(off[1] + text_gap) > 1e-6:
                issues.append(f"{cid}: font-gap annotation_offset must be (0,-{text_gap:.4f}), got {off}")
        else:
            if abs(pos[1] - c[1]) > 0.002:
                issues.append(f"{cid}: position.y != C.y")
            if abs(off[0]) > 1e-6 or abs(off[1]) > 1e-6:
                issues.append(f"{cid}: annotation_offset must be (0,0), got {off}")
        if abs(b[1] - (pos[1] - max(text_gap, 0))) > 0.001:
            issues.append(f"{cid}: horizontal line y != text bottom")
    report = {"plan": str(plan_path), "checked": checked,
              "recommended_angle": RECOMMENDED, "allowed": [MIN_ANGLE, MAX_ANGLE],
              "issues": issues, "status": "pass" if not issues else "fail"}
    print(json.dumps(report, ensure_ascii=False, indent=1))
    return 0 if not issues else 1


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: validate_callouts.py plan.json")
    sys.exit(main(sys.argv[1]))
