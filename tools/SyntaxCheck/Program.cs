using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Checks the merged programmable block script.
//  - Always: C# 6 syntax check (the language version of the programmable block).
//  - If obj/Stubs.cs exists (python3 tools/gen_stubs.py): full compile against
//    generated stubs of the game API, which also finds unknown members and type
//    errors. Only errors inside the script are reported.

const string Usings =
    "using Sandbox.Game.EntityComponents; using Sandbox.ModAPI.Ingame; using Sandbox.ModAPI.Interfaces; " +
    "using SpaceEngineers.Game.ModAPI.Ingame; using System; using System.Collections; using System.Collections.Generic; " +
    "using System.Collections.Immutable; using System.Linq; using System.Text; using VRage; using VRage.Collections; " +
    "using VRage.Game; using VRage.Game.Components; using VRage.Game.GUI.TextPanel; using VRage.Game.ModAPI.Ingame; " +
    "using VRage.Game.ModAPI.Ingame.Utilities; using VRage.Game.ObjectBuilders.Definitions; using VRageMath;";

var scriptPath = args[0];
var body = File.ReadAllText(scriptPath);
var stubsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "obj", "Stubs.cs");
bool semantic = File.Exists(stubsPath);

// The programmable block wraps the script in a class body; the wrapper is kept
// on one line so line numbers match the merged file.
var source = (semantic ? Usings + " public partial class Program : MyGridProgram {" : "class Program {") + "\n" + body + "\n}";
var script = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp6), path: scriptPath);

var diagnostics = script.GetDiagnostics().ToList();
if (semantic && diagnostics.All(d => d.Severity != DiagnosticSeverity.Error))
{
    var stubs = CSharpSyntaxTree.ParseText(File.ReadAllText(stubsPath), new CSharpParseOptions(LanguageVersion.CSharp6));
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p));
    var compilation = CSharpCompilation.Create("Script", new[] { stubs, script }, references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    diagnostics = compilation.GetDiagnostics().Where(d => d.Location.SourceTree == script).ToList();
}

var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
foreach (var d in errors)
{
    var line = d.Location.GetLineSpan().StartLinePosition.Line; // 0-based + wrapper line = merged file line
    var text = script.GetText().Lines[d.Location.GetLineSpan().StartLinePosition.Line].ToString().Trim();
    Console.WriteLine($"{Path.GetFileName(scriptPath)}({line}): {d.Id} {d.GetMessage()}\n    {text}");
}
Console.WriteLine((semantic ? "Full check against API stubs: " : "Syntax check (run tools/gen_stubs.py for a full check): ")
    + (errors.Count == 0 ? "OK" : errors.Count + " error(s)"));
return errors.Count == 0 ? 0 : 1;
