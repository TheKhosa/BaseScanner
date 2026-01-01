using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.Analyzers;

public class ExceptionHandlingAnalyzer
{
    public record Issue
    {
        public required string Type { get; init; }
        public required string Severity { get; init; }
        public required string Message { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string CodeSnippet { get; init; }
    }

    public async Task<List<Issue>> AnalyzeAsync(Project project)
    {
        var issues = new List<Issue>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;
            if (document.FilePath.Contains(".Designer.cs")) continue;

            var syntaxRoot = await document.GetSyntaxRootAsync();
            if (syntaxRoot == null) continue;

            // Find all try-catch blocks
            var tryCatches = syntaxRoot.DescendantNodes().OfType<TryStatementSyntax>();

            foreach (var tryCatch in tryCatches)
            {
                issues.AddRange(AnalyzeTryCatch(tryCatch, document.FilePath));
            }

            // Find throw statements that lose stack trace
            issues.AddRange(FindRethrowIssues(syntaxRoot, document.FilePath));
        }

        return issues;
    }

    private IEnumerable<Issue> AnalyzeTryCatch(TryStatementSyntax tryCatch, string filePath)
    {
        foreach (var catchClause in tryCatch.Catches)
        {
            var catchType = catchClause.Declaration?.Type.ToString() ?? "Exception";
            var catchBody = catchClause.Block;

            // Check for empty catch blocks
            if (IsEmptyOrTrivialBlock(catchBody))
            {
                yield return new Issue
                {
                    Type = "EmptyCatch",
                    Severity = "Critical",
                    Message = $"Empty catch block swallows {catchType} - exceptions are silently ignored",
                    FilePath = filePath,
                    Line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    CodeSnippet = $"catch ({catchType}) {{ /* empty */ }}"
                };
            }

            // Check for catching generic Exception
            if (catchType == "Exception" || catchType == "System.Exception")
            {
                // Check if it's at least logged or rethrown
                var bodyText = catchBody.ToString();
                var hasLogging = bodyText.Contains("Log") || bodyText.Contains("log") ||
                                bodyText.Contains("Console.") || bodyText.Contains("Debug.") ||
                                bodyText.Contains("Trace.");
                var hasRethrow = bodyText.Contains("throw");

                if (!hasLogging && !hasRethrow)
                {
                    yield return new Issue
                    {
                        Type = "GenericCatch",
                        Severity = "Warning",
                        Message = "Catching generic Exception without logging or rethrowing - consider specific exception types",
                        FilePath = filePath,
                        Line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        CodeSnippet = $"catch (Exception) {{ ... }}"
                    };
                }
            }

            // Check for catch-all that only returns/continues
            var statements = catchBody.Statements;
            if (statements.Count == 1)
            {
                var stmt = statements[0];
                if (stmt is ReturnStatementSyntax returnStmt)
                {
                    var returnValue = returnStmt.Expression?.ToString() ?? "void";
                    if (returnValue is "null" or "false" or "default" or "-1" or "0")
                    {
                        yield return new Issue
                        {
                            Type = "SwallowedException",
                            Severity = "Warning",
                            Message = $"Exception caught and {returnValue} returned - error information lost",
                            FilePath = filePath,
                            Line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            CodeSnippet = $"catch {{ return {returnValue}; }}"
                        };
                    }
                }
                else if (stmt is ContinueStatementSyntax or BreakStatementSyntax)
                {
                    yield return new Issue
                    {
                        Type = "SwallowedException",
                        Severity = "Warning",
                        Message = "Exception caught and silently skipped with continue/break",
                        FilePath = filePath,
                        Line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        CodeSnippet = $"catch {{ {stmt}; }}"
                    };
                }
            }

            // Check for catching and wrapping without inner exception
            var throwStatements = catchBody.DescendantNodes().OfType<ThrowStatementSyntax>();
            foreach (var throwStmt in throwStatements)
            {
                if (throwStmt.Expression is ObjectCreationExpressionSyntax creation)
                {
                    var args = creation.ArgumentList?.Arguments;
                    var exceptionVarName = catchClause.Declaration?.Identifier.Text;

                    if (args.HasValue && exceptionVarName != null)
                    {
                        var argsText = string.Join(", ", args.Value.Select(a => a.ToString()));
                        if (!argsText.Contains(exceptionVarName))
                        {
                            yield return new Issue
                            {
                                Type = "LostInnerException",
                                Severity = "Warning",
                                Message = "New exception thrown without preserving inner exception - stack trace lost",
                                FilePath = filePath,
                                Line = throwStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                                CodeSnippet = throwStmt.ToString().Substring(0, Math.Min(60, throwStmt.ToString().Length))
                            };
                        }
                    }
                }
            }
        }
    }

    private bool IsEmptyOrTrivialBlock(BlockSyntax block)
    {
        if (block.Statements.Count == 0)
            return true;

        // Check for only comments or empty statements
        if (block.Statements.All(s => s is EmptyStatementSyntax))
            return true;

        // Check for only a comment assignment like: // ignored
        var code = block.ToString();
        var withoutComments = System.Text.RegularExpressions.Regex.Replace(code, @"//.*|/\*[\s\S]*?\*/", "");
        var withoutWhitespace = System.Text.RegularExpressions.Regex.Replace(withoutComments, @"\s+", "");

        return withoutWhitespace == "{}";
    }

    private IEnumerable<Issue> FindRethrowIssues(SyntaxNode root, string filePath)
    {
        var catchClauses = root.DescendantNodes().OfType<CatchClauseSyntax>();

        foreach (var catchClause in catchClauses)
        {
            var exceptionVarName = catchClause.Declaration?.Identifier.Text;
            if (string.IsNullOrEmpty(exceptionVarName)) continue;

            var throwStatements = catchClause.Block.DescendantNodes().OfType<ThrowStatementSyntax>();

            foreach (var throwStmt in throwStatements)
            {
                // Check for "throw ex;" instead of "throw;"
                if (throwStmt.Expression is IdentifierNameSyntax identifier)
                {
                    if (identifier.Identifier.Text == exceptionVarName)
                    {
                        yield return new Issue
                        {
                            Type = "RethrowLosesStackTrace",
                            Severity = "Critical",
                            Message = $"'throw {exceptionVarName};' resets stack trace - use 'throw;' to preserve it",
                            FilePath = filePath,
                            Line = throwStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            CodeSnippet = throwStmt.ToString()
                        };
                    }
                }
            }
        }
    }
}
