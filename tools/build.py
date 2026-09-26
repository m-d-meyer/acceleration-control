#!/usr/bin/env python3
"""Merge the MDK2 project into one paste-ready programmable block script.

The programmable block expects only the body of the Program class. This script
takes the body of every `partial class Program` file in the project, joins them
and writes dist/<Project>.cs. If the result exceeds the programmable block's
character limit, comments and indentation are stripped.

Usage: python3 tools/build.py [--check]
  --check  also run the C# syntax check (needs the .NET SDK)
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = "AccelerationControl"
PROJECT_DIR = os.path.join(ROOT, PROJECT)
OUTPUT = os.path.join(ROOT, "dist", PROJECT + ".cs")
CHAR_LIMIT = 100000
CLASS_RE = re.compile(r"^\s*(public\s+)?partial\s+class\s+Program\b")


def class_body(path):
    lines = open(path, encoding="utf-8-sig").read().splitlines()
    start = next((i for i, l in enumerate(lines) if CLASS_RE.match(l)), None)
    if start is None:
        return []
    # Body starts after the opening brace and ends at the class's closing brace.
    open_line = next(i for i in range(start, len(lines)) if lines[i].strip() == "{")
    end = max(i for i, l in enumerate(lines) if l.rstrip() == "    }")
    body = lines[open_line + 1:end]
    return [l[8:] if l.startswith("        ") else l.lstrip() for l in body]


def strip_comments_and_indent(source):
    """Remove comments and leading whitespace without touching string literals."""
    out, i, n = [], 0, len(source)
    while i < n:
        c = source[i]
        if source.startswith("//", i):
            while i < n and source[i] != "\n":
                i += 1
        elif source.startswith("/*", i):
            i = source.index("*/", i) + 2
        elif source.startswith('@"', i) or source.startswith('$@"', i) or source.startswith('@$"', i):
            j = source.index('"', i) + 1
            while True:
                if source[j] == '"' and j + 1 < n and source[j + 1] == '"':
                    j += 2
                elif source[j] == '"':
                    break
                else:
                    j += 1
            out.append(source[i:j + 1])
            i = j + 1
        elif c == '"' or c == "'":
            j = i + 1
            while source[j] != c:
                j += 2 if source[j] == "\\" else 1
            out.append(source[i:j + 1])
            i = j + 1
        else:
            out.append(c)
            i += 1
    lines = [l.strip() for l in "".join(out).splitlines()]
    return "\n".join(l for l in lines if l) + "\n"


def main():
    files = sorted(f for f in os.listdir(PROJECT_DIR) if f.endswith(".cs"))
    files.sort(key=lambda f: f != "Program.cs")

    header = []
    readme = os.path.join(PROJECT_DIR, "Instructions.readme")
    if os.path.exists(readme):
        header = ["// " + l if l else "//" for l in open(readme, encoding="utf-8-sig").read().splitlines()]

    parts = []
    for f in files:
        body = class_body(os.path.join(PROJECT_DIR, f))
        if body:
            parts.append("\n".join(["// ---- " + f + " ----"] + body).strip("\n"))
    source = "\n".join(header) + "\n\n" + "\n\n".join(parts) + "\n"

    if len(source) > CHAR_LIMIT:
        source = "\n".join(header) + "\n" + strip_comments_and_indent("\n\n".join(parts))

    os.makedirs(os.path.dirname(OUTPUT), exist_ok=True)
    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as out:
        out.write(source)
    print("Wrote %s: %d characters (limit %d)" % (os.path.relpath(OUTPUT, ROOT), len(source), CHAR_LIMIT))
    if len(source) > CHAR_LIMIT:
        print("ERROR: script exceeds the programmable block limit")
        return 1

    if "--check" in sys.argv:
        check = os.path.join(ROOT, "tools", "SyntaxCheck")
        return subprocess.call(["dotnet", "run", "--project", check, "-v", "q", "--", OUTPUT])
    return 0


if __name__ == "__main__":
    sys.exit(main())
