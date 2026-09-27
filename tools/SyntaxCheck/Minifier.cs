using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Shortens the names of everything the script declares itself (fields,
// methods, properties, locals, parameters, nested types, enum members) and
// removes comments and unneeded whitespace. Names are resolved with the
// compiler's semantic model, so only the script's own symbols are renamed;
// game API members and .NET names stay untouched.
static class Minifier
{
    static readonly HashSet<string> Keep = new HashSet<string> { "Program", "Main", "Save" };

    public static string Minify(CSharpCompilation compilation, SyntaxTree tree, int bodyStart)
    {
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        // 1. Symbols declared by the script
        var declared = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var node in root.DescendantNodes())
        {
            ISymbol symbol = null;
            if (node is VariableDeclaratorSyntax || node is MethodDeclarationSyntax || node is PropertyDeclarationSyntax
                || node is ParameterSyntax || node is ForEachStatementSyntax || node is EnumMemberDeclarationSyntax
                || node is BaseTypeDeclarationSyntax || node is CatchDeclarationSyntax)
                symbol = model.GetDeclaredSymbol(node);
            if (symbol == null || Keep.Contains(symbol.Name) || symbol.IsOverride || symbol.IsImplicitlyDeclared)
                continue;
            if (symbol is IMethodSymbol method && method.MethodKind == MethodKind.Constructor)
                continue;
            declared.Add(symbol);
        }

        // 2. Every identifier token that refers to one of them
        var tokens = new List<(SyntaxToken Token, ISymbol Symbol)>();
        var otherNames = new HashSet<string>();
        foreach (var token in root.DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken))
                continue;
            ISymbol symbol = model.GetDeclaredSymbol(token.Parent);
            if (symbol == null || !IsOwnDeclaration(token))
            {
                var info = model.GetSymbolInfo(token.Parent);
                symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            }
            symbol = symbol?.OriginalDefinition;
            if (symbol != null && declared.Contains(symbol) && symbol.Name == token.ValueText)
                tokens.Add((token, symbol));
            else
                otherNames.Add(token.ValueText);
        }

        // 3. Short unique names, avoiding every name that stays as it is
        var names = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        int counter = 0;
        foreach (var symbol in tokens.Select(t => t.Symbol).GroupBy(s => s, SymbolEqualityComparer.Default)
                     .OrderByDescending(g => g.Count()).Select(g => g.Key))
        {
            string name;
            do name = ShortName(counter++);
            while (otherNames.Contains(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None
                   || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None);
            names[symbol] = name;
        }

        // 4. Apply the renames to the script body
        var text = tree.GetText().ToString();
        var sb = new StringBuilder(text);
        foreach (var (token, symbol) in tokens.OrderByDescending(t => t.Token.SpanStart))
            if (token.SpanStart >= bodyStart)
            {
                sb.Remove(token.SpanStart, token.Span.Length);
                sb.Insert(token.SpanStart, names[symbol]);
            }
        var renamed = sb.ToString();
        var body = renamed.Substring(bodyStart, renamed.LastIndexOf('}') - bodyStart);
        return Compact(CSharpSyntaxTree.ParseText(body, new CSharpParseOptions(LanguageVersion.CSharp6, kind: SourceCodeKind.Script)));
    }

    static bool IsOwnDeclaration(SyntaxToken token)
    {
        var p = token.Parent;
        return p is VariableDeclaratorSyntax v && v.Identifier == token
            || p is MethodDeclarationSyntax m && m.Identifier == token
            || p is PropertyDeclarationSyntax pr && pr.Identifier == token
            || p is ParameterSyntax pa && pa.Identifier == token
            || p is ForEachStatementSyntax f && f.Identifier == token
            || p is EnumMemberDeclarationSyntax e && e.Identifier == token
            || p is BaseTypeDeclarationSyntax t && t.Identifier == token
            || p is CatchDeclarationSyntax c && c.Identifier == token;
    }

    static string ShortName(int i)
    {
        const string first = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string rest = first + "0123456789";
        var name = first[i % first.Length].ToString();
        i /= first.Length;
        while (i > 0)
        {
            i--;
            name += rest[i % rest.Length];
            i /= rest.Length;
        }
        return name;
    }

    // Tokens joined with a space only where two words would otherwise merge.
    static string Compact(SyntaxTree tree)
    {
        var sb = new StringBuilder();
        SyntaxToken previous = default;
        int lineLength = 0;
        foreach (var token in tree.GetRoot().DescendantTokens())
        {
            var text = token.ToString();
            if (text.Length == 0)
                continue;
            if (sb.Length > 0 && NeedsSpace(previous.ToString(), text))
            {
                // Break long lines at a space to keep the editor responsive.
                if (lineLength > 500) { sb.Append('\n'); lineLength = 0; }
                else { sb.Append(' '); lineLength++; }
            }
            sb.Append(text);
            lineLength += text.Length;
            previous = token;
        }
        return sb.ToString();
    }

    static bool NeedsSpace(string a, string b)
    {
        char x = a[a.Length - 1], y = b[0];
        bool wordX = char.IsLetterOrDigit(x) || x == '_' || x == '@' || x == '"' || x == '\'';
        bool wordY = char.IsLetterOrDigit(y) || y == '_' || y == '@' || y == '"' || y == '\'' || y == '$';
        if (wordX && wordY)
            return true;
        // Keep "a - -b", "a + +b", "/ /" and "< <"-style pairs apart
        return (x == y && "+-/&|<>=".IndexOf(x) >= 0) || (x == '/' && y == '*');
    }
}
