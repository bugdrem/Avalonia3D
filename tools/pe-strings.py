import re
import sys

def strings(path, min_len=6, unicode_too=True):
    data = open(path, "rb").read()
    out = []
    # ASCII
    for m in re.finditer(rb"[\x20-\x7e]{%d,}" % min_len, data):
        out.append(m.group().decode("ascii", "replace"))
    if unicode_too:
        for m in re.finditer(rb"(?:[\x20-\x7e]\x00){%d,}" % min_len, data):
            s = m.group().decode("utf-16-le", "replace")
            if s.isprintable():
                out.append(s)
    return out

path = sys.argv[1]
patterns = sys.argv[2].split(",")
hits = {}
for s in strings(path):
    for p in patterns:
        if p.lower() in s.lower():
            hits.setdefault(p, set()).add(s.strip()[:120])

for p in patterns:
    vals = sorted(hits.get(p, []))[:8]
    print(f"--- {p} ({len(hits.get(p, []))} 条) ---")
    for v in vals:
        print("   ", v)
