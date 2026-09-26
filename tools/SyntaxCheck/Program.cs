using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// The programmable block wraps the script in a class body, so do the same here.
var body = File.ReadAllText(args[0]);
var source = "class Program {\n" + body + "\n}";
var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp6));
var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
foreach (var d in errors)
{
    // Line numbers match the merged file (the wrapper adds one line, lines are 0-based).
    var line = d.Location.GetLineSpan().StartLinePosition.Line;
    Console.WriteLine($"{Path.GetFileName(args[0])}({line}): {d.Id} {d.GetMessage()}");
}
Console.WriteLine(errors.Count == 0 ? "OK: no syntax errors" : $"{errors.Count} error(s)");
return errors.Count == 0 ? 0 : 1;
