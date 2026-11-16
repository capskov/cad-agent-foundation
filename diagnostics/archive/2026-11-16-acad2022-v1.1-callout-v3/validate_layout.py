#!/usr/bin/env python3
"""Check multi-callout layout: leader crossing, leader-through-text, text overlap, same-side order."""
import json, pathlib, sys

EPS = 1e-6


def orient(a, b, c):
    return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])


def seg_cross(p1, p2, p3, p4):
    d1 = orient(p3, p4, p1)
    d2 = orient(p3, p4, p2)
    d3 = orient(p1, p2, p3)
    d4 = orient(p1, p2, p4)
    return ((d1 > EPS and d2 < -EPS) or (d1 < -EPS and d2 > EPS)) and \
           ((d3 > EPS and d4 < -EPS) or (d3 < -EPS and d4 > EPS))


def point_in_rect(p, r):
    return r[0] + EPS < p[0] < r[1] - EPS and r[2] + EPS < p[1] < r[3] - EPS


def seg_in_rect(p1, p2, r):
    if point_in_rect(p1, r) or point_in_rect(p2, r):
        return True
    corners = [(r[0], r[2]), (r[1], r[2]), (r[1], r[3]), (r[0], r[3])]
    for i in range(4):
        if seg_cross(p1, p2, corners[i], corners[(i + 1) % 4]):
            return True
    return False


def box_of(step):
    x, y = step["position"]
    w, h = float(step["width"]), float(step.get("height", 0.06))
    if step.get("attachment") == "BottomLeft":
        return (x, x + w, y, y + h)
    if step.get("attachment") == "BottomRight":
        return (x - w, x, y, y + h)
    raise ValueError("bad attachment")


def main(path):
    plan = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    groups = []
    for s in plan.get("steps", []):
        if s.get("op") != "callout":
            continue
        a, b, c = s["vertices"]
        groups.append({"id": s["id"], "A": a, "B": b, "C": c, "box": box_of(s),
                       "att": s.get("attachment"), "straight": bool(s.get("leader_straight"))})

    issues = []
    for i, g in enumerate(groups):
        for j, h in enumerate(groups):
            if i >= j:
                continue
            # leader-leader crossing (skip horizontal tails that are parallel)
            if not g["straight"] and not h["straight"]:
                if seg_cross(g["A"], g["B"], h["A"], h["B"]):
                    issues.append(f"{g['id']} diagonal crosses {h['id']} diagonal")
            for label, seg in ((g["id"], (g["A"], g["B"])), (h["id"], (h["A"], h["B"]))):
                other = h if label == g["id"] else g
                if seg_in_rect(seg[0], seg[1], other["box"]):
                    issues.append(f"{label} leader passes through {other['id']} text box")
            # text boxes must not overlap
            r1, r2 = g["box"], h["box"]
            if min(r1[1], r2[1]) - max(r1[0], r2[0]) > EPS and \
               min(r1[3], r2[3]) - max(r1[2], r2[2]) > EPS:
                issues.append(f"{g['id']} text overlaps {h['id']} text")

    # same-side ordering: lower anchor must have lower text line
    for i, g in enumerate(groups):
        for j, h in enumerate(groups):
            if i >= j or g["att"] != h["att"]:
                continue
            da = g["A"][1] - h["A"][1]
            dt = g["C"][1] - h["C"][1]
            if abs(da) > 0.01 and abs(dt) > 0.01 and da * dt < 0:
                issues.append(f"{g['id']}/{h['id']} anchor order reversed vs text row order")

    report = {"plan": str(path), "checked": len(groups), "issues": issues,
              "status": "pass" if not issues else "fail"}
    print(json.dumps(report, ensure_ascii=False, indent=1))
    return 0 if not issues else 1


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: validate_layout.py plan.json")
    sys.exit(main(sys.argv[1]))
