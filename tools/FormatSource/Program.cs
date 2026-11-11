using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Use the installed SDK's parser, never textual semicolon replacement: for-loop
// headers, strings and comments must keep their meaning. dotnet format follows.
string root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
bool apply = args.Contains("--apply");
int changed = 0;
foreach (string directory in new[] { "src", "tests", "tools" })
foreach (string file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
{
    if (file.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;
    string source = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
    var original = tree.GetRoot();
    var tokens = original.DescendantTokens().Where(token =>
        token.IsKind(SyntaxKind.SemicolonToken) && token.Parent is not ForStatementSyntax ||
        token.IsKind(SyntaxKind.OpenBraceToken) && token.Parent is BlockSyntax ||
        token.IsKind(SyntaxKind.CloseBraceToken) && token.Parent is BlockSyntax);
    var result = original.ReplaceTokens(tokens, (old, current) =>
    {
        if (current.IsKind(SyntaxKind.CloseBraceToken))
        {
            bool whitespaceOnly = current.LeadingTrivia.All(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia));
            if (whitespaceOnly)
                current = current.WithLeadingTrivia(old.GetPreviousToken().TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia))
                    ? default(SyntaxTriviaList) : SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed));
            var next = old.GetNextToken();
            if (next.IsKind(SyntaxKind.CloseParenToken) || next.IsKind(SyntaxKind.CommaToken) || next.IsKind(SyntaxKind.SemicolonToken))
                return current.WithTrailingTrivia(current.TrailingTrivia.Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)));
}
        if (!current.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
            current = current.WithTrailingTrivia(current.TrailingTrivia.Add(SyntaxFactory.ElasticCarriageReturnLineFeed));
        return current;
});
    string formatted = result.ToFullString();
    var reparsed = CSharpSyntaxTree.ParseText(formatted, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
    if (!SyntaxFactory.AreEquivalent(original, reparsed)) throw new InvalidOperationException("Formatting changed syntax: " + file);
    if (formatted == source) continue;
    changed++;
    if (apply) File.WriteAllText(file, formatted);
}
Console.WriteLine($"{(apply ? "Expanded" : "Would expand")} {changed} first-party files; syntax equivalence verified.");
