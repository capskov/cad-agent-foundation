import json, hashlib
style = {"handle": "1B8", "font_file": "txt.shx", "big_font_file": "hztxt.shx",
         "width_factor": 1.0, "fixed_height": 0.0, "is_vertical": False,
         "resolved_font": {"typeface": "", "bold": False, "italic": False,
                           "charset": 1, "pitch_family": 0}}
s = json.dumps(style, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
print(s)
print(hashlib.sha256(s.encode("utf-8")).hexdigest().upper())
