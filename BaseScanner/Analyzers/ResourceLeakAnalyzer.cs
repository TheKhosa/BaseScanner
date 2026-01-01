using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.Analyzers;

public class ResourceLeakAnalyzer
{
    private static readonly HashSet<string> DisposableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stream", "FileStream", "MemoryStream", "StreamReader", "StreamWriter",
        "BinaryReader", "BinaryWriter", "TextReader", "TextWriter",
        "SqlConnection", "SqlCommand", "SqlDataReader", "SqlDataAdapter",
        "OleDbConnection", "OleDbCommand", "OleDbDataReader",
        "HttpClient", "WebClient", "HttpWebRequest", "HttpWebResponse",
        "Socket", "TcpClient", "UdpClient", "NetworkStream",
        "Process", "Timer", "FileSystemWatcher",
        "Bitmap", "Image", "Graphics", "Pen", "Brush", "Font",
        "DbConnection", "DbCommand", "DbDataReader",
        "IDbConnection", "IDbCommand", "IDataReader",
        "TransactionScope", "CancellationTokenSource",
        "Mutex", "Semaphore", "EventWaitHandle", "ManualResetEvent", "AutoResetEvent"
    };

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
            var semanticModel = await document.GetSemanticModelAsync();
            if (syntaxRoot == null || semanticModel == null) continue;

            // Find disposable objects not in using statements
            issues.AddRange(FindDisposablesWithoutUsing(syntaxRoot, semanticModel, document.FilePath));

            // Find event handlers that are added but not removed
            issues.AddRange(FindEventHandlerLeaks(syntaxRoot, document.FilePath));

            // Find classes implementing IDisposable without proper disposal pattern
            issues.AddRange(FindIncompleteDisposablePattern(syntaxRoot, semanticModel, document.FilePath));
        }

        return issues;
    }

    private IEnumerable<Issue> FindDisposablesWithoutUsing(SyntaxNode root, SemanticModel semanticModel, string filePath)
    {
        // Find local variable declarations
        var localDeclarations = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Where(l => !l.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)); // Exclude "using var"

        foreach (var declaration in localDeclarations)
        {
            foreach (var variable in declaration.Declaration.Variables)
            {
                if (variable.Initializer?.Value is ObjectCreationExpressionSyntax creation)
                {
                    var typeName = GetSimpleTypeName(creation.Type.ToString());

                    if (IsDisposableType(typeName))
                    {
                        // Check if it's inside a using statement
                        var isInUsing = variable.Ancestors().Any(a => a is UsingStatementSyntax);

                        // Check if the variable is disposed later in the same method
                        var method = variable.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        var isDisposedLater = method != null && IsDisposedInScope(variable.Identifier.Text, method);

                        // Check if it's a field assignment (will be handled by class-level analysis)
                        var isFieldAssignment = variable.Ancestors().Any(a => a is FieldDeclarationSyntax);

                        if (!isInUsing && !isDisposedLater && !isFieldAssignment)
                        {
                            yield return new Issue
                            {
                                Type = "DisposableNotDisposed",
                                Severity = "Warning",
                                Message = $"'{typeName}' is IDisposable but not in a using statement or explicitly disposed",
                                FilePath = filePath,
                                Line = declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                                CodeSnippet = declaration.ToString().Substring(0, Math.Min(60, declaration.ToString().Length))
                            };
                        }
                    }
                }
            }
        }

        // Find object creations used directly (not assigned to variable)
        var directCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(c => c.Parent is not EqualsValueClauseSyntax &&
                       c.Parent is not AssignmentExpressionSyntax &&
                       c.Parent is not UsingStatementSyntax);

        foreach (var creation in directCreations)
        {
            var typeName = GetSimpleTypeName(creation.Type.ToString());

            if (IsDisposableType(typeName))
            {
                // Check if it's being returned (acceptable pattern)
                var isReturned = creation.Ancestors().Any(a => a is ReturnStatementSyntax or ArrowExpressionClauseSyntax);

                // Check if it's passed to a method (might be okay)
                var isArgument = creation.Parent is ArgumentSyntax;

                if (!isReturned && !isArgument)
                {
                    yield return new Issue
                    {
                        Type = "DisposableNotDisposed",
                        Severity = "Warning",
                        Message = $"'{typeName}' created but may not be disposed",
                        FilePath = filePath,
                        Line = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        CodeSnippet = $"new {typeName}(...)"
                    };
                }
            }
        }
    }

    private IEnumerable<Issue> FindEventHandlerLeaks(SyntaxNode root, string filePath)
    {
        // Find all event subscriptions (+=)
        var addHandlers = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression))
            .Select(a => new { Assignment = a, EventName = GetEventName(a.Left) })
            .Where(x => x.EventName != null)
            .ToList();

        // Find all event unsubscriptions (-=)
        var removeHandlers = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression))
            .Select(a => GetEventName(a.Left))
            .Where(e => e != null)
            .ToHashSet();

        foreach (var add in addHandlers)
        {
            // Check if there's a corresponding -= for this event
            if (!removeHandlers.Contains(add.EventName))
            {
                // Only report if it looks like an external event (not own events)
                var leftSide = add.Assignment.Left.ToString();
                if (leftSide.Contains(".") && !leftSide.StartsWith("this."))
                {
                    yield return new Issue
                    {
                        Type = "EventHandlerLeak",
                        Severity = "Info",
                        Message = $"Event '{add.EventName}' subscribed but never unsubscribed - potential memory leak",
                        FilePath = filePath,
                        Line = add.Assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        CodeSnippet = add.Assignment.ToString().Substring(0, Math.Min(50, add.Assignment.ToString().Length))
                    };
                }
            }
        }
    }

    private IEnumerable<Issue> FindIncompleteDisposablePattern(SyntaxNode root, SemanticModel semanticModel, string filePath)
    {
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            var symbol = semanticModel.GetDeclaredSymbol(classDecl);
            if (symbol == null) continue;

            // Check if class has disposable fields
            var disposableFields = classDecl.Members.OfType<FieldDeclarationSyntax>()
                .Where(f => IsDisposableType(GetSimpleTypeName(f.Declaration.Type.ToString())))
                .ToList();

            if (disposableFields.Count == 0) continue;

            // Check if class implements IDisposable
            var implementsDisposable = symbol.AllInterfaces.Any(i => i.Name == "IDisposable");

            if (!implementsDisposable)
            {
                yield return new Issue
                {
                    Type = "MissingIDisposable",
                    Severity = "Warning",
                    Message = $"Class '{classDecl.Identifier.Text}' has disposable fields but doesn't implement IDisposable",
                    FilePath = filePath,
                    Line = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    CodeSnippet = $"class {classDecl.Identifier.Text} has {disposableFields.Count} disposable field(s)"
                };
            }
            else
            {
                // Check if Dispose method actually disposes the fields
                var disposeMethod = classDecl.Members.OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == "Dispose");

                if (disposeMethod != null)
                {
                    var disposeBody = disposeMethod.Body?.ToString() ?? disposeMethod.ExpressionBody?.ToString() ?? "";

                    foreach (var field in disposableFields)
                    {
                        var fieldName = field.Declaration.Variables.First().Identifier.Text;
                        if (!disposeBody.Contains(fieldName))
                        {
                            yield return new Issue
                            {
                                Type = "FieldNotDisposed",
                                Severity = "Warning",
                                Message = $"Disposable field '{fieldName}' not disposed in Dispose() method",
                                FilePath = filePath,
                                Line = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                                CodeSnippet = field.ToString().Substring(0, Math.Min(50, field.ToString().Length))
                            };
                        }
                    }
                }
            }
        }
    }

    private bool IsDisposableType(string typeName)
    {
        return DisposableTypes.Contains(typeName) ||
               typeName.EndsWith("Stream") ||
               typeName.EndsWith("Reader") ||
               typeName.EndsWith("Writer") ||
               typeName.EndsWith("Connection") ||
               typeName.EndsWith("Client") ||
               typeName.Contains("Disposable");
    }

    private string GetSimpleTypeName(string fullTypeName)
    {
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot >= 0 ? fullTypeName.Substring(lastDot + 1) : fullTypeName;
    }

    private string? GetEventName(ExpressionSyntax expression)
    {
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Name.Identifier.Text;
        }
        if (expression is IdentifierNameSyntax identifier)
        {
            return identifier.Identifier.Text;
        }
        return null;
    }

    private bool IsDisposedInScope(string variableName, SyntaxNode scope)
    {
        // Check for .Dispose() call
        var disposeCall = scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(inv => inv.Expression is MemberAccessExpressionSyntax ma &&
                       ma.Name.Identifier.Text == "Dispose" &&
                       ma.Expression.ToString() == variableName);

        // Check for using statement with the variable
        var usingStatement = scope.DescendantNodes().OfType<UsingStatementSyntax>()
            .Any(u => u.Declaration?.Variables.Any(v => v.Identifier.Text == variableName) == true ||
                     u.Expression?.ToString() == variableName);

        return disposeCall || usingStatement;
    }
}
