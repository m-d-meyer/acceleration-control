using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

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

        // 4. Further shortening without changing behaviour:
        //    - 'readonly' on fields is dropped,
        //    - explicit local types become 'var' where the initializer has exactly
        //      that type (also foreach variables with the collection's element type).
        var edits = new List<(int Start, int Length, string Text)>();
        foreach (var node in root.DescendantNodes())
        {
            if (node is FieldDeclarationSyntax field)
                foreach (var modifier in field.Modifiers)
                    if (modifier.IsKind(SyntaxKind.ReadOnlyKeyword))
                        edits.Add((modifier.SpanStart, modifier.Span.Length, ""));
            if (node is LocalDeclarationStatementSyntax local && !local.IsConst && local.Declaration.Variables.Count == 1
                && !local.Declaration.Type.IsVar)
            {
                var init = local.Declaration.Variables[0].Initializer;
                var declaredType = model.GetTypeInfo(local.Declaration.Type).Type;
                var initType = init == null ? null : model.GetTypeInfo(init.Value).Type;
                if (declaredType != null && initType != null && SymbolEqualityComparer.Default.Equals(declaredType, initType))
                    edits.Add((local.Declaration.Type.SpanStart, local.Declaration.Type.Span.Length, "var"));
            }
            if (node is ForEachStatementSyntax loop && !loop.Type.IsVar)
            {
                var element = model.GetForEachStatementInfo(loop).ElementType;
                var declaredType = model.GetTypeInfo(loop.Type).Type;
                if (element != null && SymbolEqualityComparer.Default.Equals(element, declaredType))
                    edits.Add((loop.Type.SpanStart, loop.Type.Span.Length, "var"));
            }
        }
        foreach (var (token, symbol) in tokens)
            if (!edits.Any(e => e.Text == "var" && token.SpanStart >= e.Start && token.SpanStart < e.Start + e.Length))
                edits.Add((token.SpanStart, token.Span.Length, names[symbol]));

        // 5. Frequent static calls of API types (Math.Max, Vector3D.Distance, ...)
        //    go through short wrapper methods: 'Vector3D.Distance(a,b)' -> 'x(a,b)'.
        var wrappers = new StringBuilder();
        var calls = new Dictionary<IMethodSymbol, List<MemberAccessExpressionSyntax>>(SymbolEqualityComparer.Default);
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var access = call.Expression as MemberAccessExpressionSyntax;
            var method = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
            if (access == null || method == null || !method.IsStatic || method.IsGenericMethod || method.ReturnsVoid
                || declared.Contains(method.OriginalDefinition) || call.SpanStart < bodyStart
                || method.Parameters.Any(p => p.RefKind != RefKind.None || p.IsParams || p.HasExplicitDefaultValue)
                || call.ArgumentList.Arguments.Count != method.Parameters.Length
                || !(model.GetSymbolInfo(access.Expression).Symbol is INamedTypeSymbol))
                continue;
            if (!calls.TryGetValue(method, out var list))
                calls[method] = list = new List<MemberAccessExpressionSyntax>();
            list.Add(access);
        }
        foreach (var pair in calls)
        {
            var method = pair.Key;
            string target = pair.Value[0].ToString();
            if (pair.Value.Count * (target.Length - 3) < 60 + target.Length)
                continue;       // not worth a wrapper
            if (!Allowed(method.ReturnType, tree, bodyStart) || method.Parameters.Any(p => !Allowed(p.Type, tree, bodyStart)))
                continue;
            string name;
            do name = ShortName(counter++);
            while (otherNames.Contains(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None
                   || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None);
            var format = SymbolDisplayFormat.MinimallyQualifiedFormat.RemoveMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
            var parameters = string.Join(",", method.Parameters.Select((p, i) => p.Type.ToDisplayString(format) + " p" + i));
            var arguments = string.Join(",", method.Parameters.Select((p, i) => "p" + i));
            wrappers.Append("static " + method.ReturnType.ToDisplayString(format) + " " + name + "(" + parameters + ")=>"
                + target + "(" + arguments + ");\n");
            foreach (var access in pair.Value)
                edits.Add((access.SpanStart, access.Span.Length, name));
        }

        // 5b. Frequent static values of API types (enum members, constants, static
        //     fields, CultureInfo.InvariantCulture) are read once into a short
        //     field: 'TextAlignment.RIGHT' -> 'x' with 'const TextAlignment x=TextAlignment.RIGHT;'.
        var values = new Dictionary<ISymbol, List<MemberAccessExpressionSyntax>>(SymbolEqualityComparer.Default);
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var symbol = model.GetSymbolInfo(access).Symbol;
            bool constant = symbol is IFieldSymbol f && f.IsConst;
            bool field = symbol is IFieldSymbol sf && sf.IsStatic;
            bool property = symbol is IPropertySymbol pr && pr.IsStatic && pr.Name == "InvariantCulture";
            if (!field && !property || declared.Contains(symbol.OriginalDefinition) || access.SpanStart < bodyStart
                || !(model.GetSymbolInfo(access.Expression).Symbol is INamedTypeSymbol)
                || access.Parent is InvocationExpressionSyntax inv && inv.Expression == access
                || access.Parent is AssignmentExpressionSyntax asg && asg.Left == access
                || access.Parent is ArgumentSyntax arg && !arg.RefKindKeyword.IsKind(SyntaxKind.None)
                || edits.Any(e => access.SpanStart < e.Start + e.Length && e.Start < access.Span.End))
                continue;
            if (!values.TryGetValue(symbol, out var list))
                values[symbol] = list = new List<MemberAccessExpressionSyntax>();
            list.Add(access);
        }
        foreach (var pair in values)
        {
            string target = pair.Value[0].ToString();
            if (pair.Value.Count * (target.Length - 2) < 30 + 2 * target.Length)
                continue;
            if (!Allowed(pair.Key is IFieldSymbol ft ? ft.Type : ((IPropertySymbol)pair.Key).Type, tree, bodyStart))
                continue;
            string name;
            do name = ShortName(counter++);
            while (otherNames.Contains(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None
                   || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None);
            var format = SymbolDisplayFormat.MinimallyQualifiedFormat.RemoveMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
            var type = pair.Key is IFieldSymbol fs ? fs.Type : ((IPropertySymbol)pair.Key).Type;
            bool isConst = pair.Key is IFieldSymbol c && c.IsConst;
            wrappers.Append((isConst ? "const " : "static ") + (pair.Key is IPropertySymbol ? type.ToDisplayString() : type.ToDisplayString(format)) + " " + name + "=" + target + ";\n");
            foreach (var access in pair.Value)
                edits.Add((access.SpanStart, access.Span.Length, name));
        }

        // 6. Apply the edits to the script body
        var text = tree.GetText().ToString();
        var sb = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(e => e.Start))
            if (edit.Start >= bodyStart)
            {
                sb.Remove(edit.Start, edit.Length);
                sb.Insert(edit.Start, edit.Text);
            }
        var renamed = sb.ToString();
        var body = renamed.Substring(bodyStart, renamed.LastIndexOf('}') - bodyStart) + "\n" + wrappers;
        return Compact(CSharpSyntaxTree.ParseText(MergeFields(body), new CSharpParseOptions(LanguageVersion.CSharp6, kind: SourceCodeKind.Script)));
    }

    // 7. Fields of the script class with the same type are declared together:
    //    'double a;double b=1;' -> 'double a,b=1;' (consts likewise). Instance
    //    field initializers cannot refer to other instance fields, and consts
    //    are resolved by the compiler, so the order does not matter. Static
    //    fields keep their place (their initializers run in order).
    static string MergeFields(string body)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp6, kind: SourceCodeKind.Script);
        var root = CSharpSyntaxTree.ParseText(body, options).GetRoot();
        var groups = new Dictionary<string, List<string>>();
        var remove = new List<TextSpan>();
        foreach (var field in root.ChildNodes().OfType<FieldDeclarationSyntax>())
        {
            var modifiers = field.Modifiers.Select(m => m.Text).ToList();
            if (field.AttributeLists.Count > 0 || modifiers.Any(m => m != "const"))
                continue;
            string key = (modifiers.Count > 0 ? "const " : "") + field.Declaration.Type.ToString();
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new List<string>();
            list.AddRange(field.Declaration.Variables.Select(v => v.ToString()));
            remove.Add(field.Span);
        }
        var sb = new StringBuilder(body);
        foreach (var span in remove.OrderByDescending(r => r.Start))
            sb.Remove(span.Start, span.Length);
        foreach (var group in groups)
            sb.Append("\n" + group.Key + " " + string.Join(",", group.Value) + ";");
        return sb.ToString();
    }

    // The game only accepts whitelisted types (e.g. IFormatProvider is
    // prohibited, CultureInfo is not). Generated code may only name types the
    // script itself already names, which the game has accepted.
    static bool Allowed(ITypeSymbol type, SyntaxTree tree, int bodyStart)
    {
        return type.SpecialType != SpecialType.None
            || Regex.IsMatch(tree.GetText().ToString().Substring(bodyStart), @"\b" + Regex.Escape(type.Name) + @"\b");
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
