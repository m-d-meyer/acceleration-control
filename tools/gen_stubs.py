#!/usr/bin/env python3
"""Generate C# stubs of the Space Engineers programmable block API.

The stubs let tools/SyntaxCheck compile the script for real (type and member
checks) without the game's DLLs. They are generated from the API
documentation in the MDK-SE wiki (https://github.com/malware-dev/MDK-SE/wiki),
which lists every whitelisted type with its members. Operators are not part of
that documentation, so the common VRageMath operators are added by hand below.

The documentation is from around 2022; members added to the game later are
missing and show up as false errors.

Usage: python3 tools/gen_stubs.py [path to a clone of MDK-SE.wiki]
Without a path the wiki is cloned into tools/SyntaxCheck/obj/.
Writes tools/SyntaxCheck/obj/Stubs.cs (not checked in).
"""
import html
import os
import re
import subprocess
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OBJ = os.path.join(ROOT, "tools", "SyntaxCheck", "obj")
WIKI_URL = "https://github.com/malware-dev/MDK-SE.wiki.git"

HEADER_RE = re.compile(r"^#### (\S+) (Interface|Struct|Class|Enum|Delegate)\s*$", re.M)
NAMESPACE_RE = re.compile(r"\*\*Namespace:\*\* \[([\w.]+)\]")
DECL_RE = re.compile(r"```csharp\s*\n(.*?)\n```", re.S)
ENTRY_RE = re.compile(r"^\[(.+)\]\([^)]*\)\s*$")

OPERATORS = """
namespace VRageMath
{
    public partial struct Vector3D
    {
        public static Vector3D operator +(Vector3D a, Vector3D b) { throw null; }
        public static Vector3D operator -(Vector3D a, Vector3D b) { throw null; }
        public static Vector3D operator -(Vector3D a) { throw null; }
        public static Vector3D operator *(Vector3D a, double b) { throw null; }
        public static Vector3D operator *(double a, Vector3D b) { throw null; }
        public static Vector3D operator *(Vector3D a, Vector3D b) { throw null; }
        public static Vector3D operator /(Vector3D a, double b) { throw null; }
        public static Vector3D operator /(Vector3D a, Vector3D b) { throw null; }
        public static bool operator ==(Vector3D a, Vector3D b) { throw null; }
        public static bool operator !=(Vector3D a, Vector3D b) { throw null; }
        public static implicit operator Vector3D(Vector3 v) { throw null; }
    }
    public partial struct Vector3
    {
        public static Vector3 operator +(Vector3 a, Vector3 b) { throw null; }
        public static Vector3 operator -(Vector3 a, Vector3 b) { throw null; }
        public static Vector3 operator -(Vector3 a) { throw null; }
        public static Vector3 operator *(Vector3 a, float b) { throw null; }
        public static Vector3 operator *(float a, Vector3 b) { throw null; }
        public static Vector3 operator /(Vector3 a, float b) { throw null; }
        public static bool operator ==(Vector3 a, Vector3 b) { throw null; }
        public static bool operator !=(Vector3 a, Vector3 b) { throw null; }
        public static explicit operator Vector3(Vector3D v) { throw null; }
    }
    public partial struct Vector2
    {
        public static Vector2 operator +(Vector2 a, Vector2 b) { throw null; }
        public static Vector2 operator -(Vector2 a, Vector2 b) { throw null; }
        public static Vector2 operator -(Vector2 a) { throw null; }
        public static Vector2 operator *(Vector2 a, float b) { throw null; }
        public static Vector2 operator *(float a, Vector2 b) { throw null; }
        public static Vector2 operator *(Vector2 a, Vector2 b) { throw null; }
        public static Vector2 operator /(Vector2 a, float b) { throw null; }
        public static Vector2 operator /(Vector2 a, Vector2 b) { throw null; }
        public static bool operator ==(Vector2 a, Vector2 b) { throw null; }
        public static bool operator !=(Vector2 a, Vector2 b) { throw null; }
    }
    public partial struct Vector2D
    {
        public static Vector2D operator +(Vector2D a, Vector2D b) { throw null; }
        public static Vector2D operator -(Vector2D a, Vector2D b) { throw null; }
        public static Vector2D operator *(Vector2D a, double b) { throw null; }
        public static Vector2D operator /(Vector2D a, double b) { throw null; }
    }
    public partial struct Color
    {
        public static Color operator *(Color a, float b) { throw null; }
        public static bool operator ==(Color a, Color b) { throw null; }
        public static bool operator !=(Color a, Color b) { throw null; }
    }
    public partial struct MatrixD
    {
        public static MatrixD operator *(MatrixD a, MatrixD b) { throw null; }
    }
}
namespace VRage
{
    public partial struct MyFixedPoint
    {
        public static explicit operator float(MyFixedPoint v) { throw null; }
        public static explicit operator double(MyFixedPoint v) { throw null; }
        public static explicit operator int(MyFixedPoint v) { throw null; }
        public static explicit operator decimal(MyFixedPoint v) { throw null; }
        public static implicit operator MyFixedPoint(int v) { throw null; }
        public static implicit operator MyFixedPoint(float v) { throw null; }
        public static MyFixedPoint operator +(MyFixedPoint a, MyFixedPoint b) { throw null; }
        public static MyFixedPoint operator -(MyFixedPoint a, MyFixedPoint b) { throw null; }
        public static MyFixedPoint operator *(MyFixedPoint a, MyFixedPoint b) { throw null; }
        public static bool operator <(MyFixedPoint a, MyFixedPoint b) { throw null; }
        public static bool operator >(MyFixedPoint a, MyFixedPoint b) { throw null; }
        public static bool operator ==(MyFixedPoint a, MyFixedPoint b) { throw null; }
        public static bool operator !=(MyFixedPoint a, MyFixedPoint b) { throw null; }
    }
}
"""


def parse_members(text):
    """Yield (section, signature, inherited) for each member entry of a type page."""
    section = None
    lines = text.splitlines()
    for i, line in enumerate(lines):
        if line.startswith("#### "):
            section = line[5:].strip()
            continue
        if section not in ("Fields", "Properties", "Constructors", "Methods", "Events"):
            continue
        m = ENTRY_RE.match(line)
        if m:
            signature = m.group(1)
        elif line.strip() and line[0] not in ">#*`[":
            signature = line.strip()  # members without their own page are not linked
        else:
            continue
        inherited = False
        for follow in lines[i + 1:i + 8]:
            if follow.strip() and follow[0] not in ">":
                break
            if "_Inherited from" in follow:
                inherited = True
        yield section, html.unescape(signature.replace("\\", "")), inherited


def member_code(kind, name, section, sig):
    sig = sig.strip()
    is_static = sig.startswith("static ")
    if kind == "Enum":
        return sig.split()[-1] + ","
    if kind == "Interface":
        if is_static:
            return None
        if section == "Properties":
            return sig
        if section == "Events":
            return "event " + sig + ";"
        if section == "Methods":
            return sig + ";"
        return None
    # classes and structs
    if section == "Fields":
        return "public " + sig + ";"
    if section == "Properties":
        body = sig.replace("get;", "get { throw null; }").replace("set;", "set { }")
        return "public " + body
    if section == "Events":
        return "public event " + sig + ";"
    if section == "Constructors":
        # the documentation names constructors like the type (without generics)
        return "public " + name + sig[sig.index("("):] + " { throw null; }"
    if section == "Methods":
        abstract = sig.startswith("abstract ") or " abstract " in sig
        sig = sig.replace("abstract ", "").replace("virtual ", "")
        if abstract and kind == "Class":
            return "public virtual " + sig + " { throw null; }"
        return "public " + sig + " { throw null; }"
    return None


def main():
    wiki = sys.argv[1] if len(sys.argv) > 1 else os.path.join(OBJ, "MDK-SE.wiki")
    if not os.path.isdir(wiki):
        os.makedirs(OBJ, exist_ok=True)
        subprocess.check_call(["git", "clone", "-q", "--depth", "1", WIKI_URL, wiki])
    api = os.path.join(wiki, "api")

    namespaces = defaultdict(list)
    for fname in sorted(os.listdir(api)):
        if not fname.endswith(".md") or "+" in fname:
            continue
        text = open(os.path.join(api, fname), encoding="utf-8").read()
        header = HEADER_RE.search(text)
        ns = NAMESPACE_RE.search(text)
        decl = DECL_RE.search(text)
        if not header or not ns or not decl:
            continue
        namespace = ns.group(1)
        if namespace.startswith("System") or namespace.startswith("Microsoft"):
            continue
        name, kind = html.unescape(header.group(1)), header.group(2)
        declaration = html.unescape(decl.group(1).strip())
        if kind == "Delegate":
            namespaces[namespace].append(declaration + ";")
            continue
        declaration = declaration.replace("sealed ", "").replace("static class", "class")
        if kind in ("Struct", "Class", "Interface"):
            declaration = re.sub(r"\b(struct|class|interface)\b", r"partial \1", declaration, count=1)
        simple = re.sub(r"<.*", "", name)
        members = []
        for section, sig, inherited in parse_members(text):
            if inherited or "*" in sig:  # pointer overloads need unsafe code
                continue
            code = member_code(kind, simple, section, sig)
            if code:
                members.append("        " + code)
        namespaces[namespace].append(declaration + "\n    {\n" + "\n".join(members) + "\n    }")

    out = ["// Generated by tools/gen_stubs.py - do not edit.",
           "#pragma warning disable",
           "using System; using System.Collections; using System.Collections.Generic; using System.Collections.Immutable; using System.Text; using System.Xml.Serialization;"]
    for namespace in sorted(namespaces):
        out.append("namespace " + namespace + "\n{")
        out.append("    using " + "; using ".join(sorted(namespaces)) + ";")
        for t in namespaces[namespace]:
            out.append("    " + t)
        out.append("}")
    out.append(OPERATORS)

    os.makedirs(OBJ, exist_ok=True)
    path = os.path.join(OBJ, "Stubs.cs")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(out))
    print("Wrote %s (%d namespaces)" % (os.path.relpath(path, ROOT), len(namespaces)))


if __name__ == "__main__":
    main()
