#!/usr/bin/env python3
"""Counts non-blank, non-comment C# lines of both apps (obj/ and bin/ excluded). usage: loc.py"""
import glob, os
root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
def loc(project):
    total = 0
    for path in glob.glob(os.path.join(root, project, "**", "*.cs"), recursive=True):
        if os.sep + "obj" + os.sep in path or os.sep + "bin" + os.sep in path: continue
        block = False
        for line in open(path, encoding="utf-8"):
            s = line.strip()
            if block:
                block = "*/" not in s
            elif s.startswith("/*"):
                block = "*/" not in s
            elif s and not s.startswith("//"):
                total += 1
    return total
for p in ("Foundgine.Shop", "HotChocolate.Shop"): print(f"{p:20} {loc(p)}")
