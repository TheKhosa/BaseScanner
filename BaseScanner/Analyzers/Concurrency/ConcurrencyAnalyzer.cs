using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BaseScanner.Services;

namespace BaseScanner.Analyzers.Concurrency;

/// <summary>
/// Analyzes code for concurrency and threading issues.
/// </summary>
public class ConcurrencyAnalyzer
{
    private readonly List<IConcurrencyDetector> _detectors;

    public ConcurrencyAnalyzer()
    {
        _detectors = new List<IConcurrencyDetector>
        {
            new FloatingTaskDetector(),
            new AsyncVoidDetector(),
            new LockPatternDetector(),
            new RaceConditionDetector(),
            new ThreadSafetyDetector(),
            new DeadlockRiskDetector()
        };
    }

    public async Task<ConcurrencyAnalysisResult> AnalyzeAsync(
        Document document,
        SemanticModel semanticModel,
        SyntaxNode root)
    {
        var issues = new List<ConcurrencyIssue>();

        foreach (var detector in _detectors)
        {
            var detected = await detector.DetectAsync(document, semanticModel, root);
            issues.AddRange(detected);
        }

        return new ConcurrencyAnalysisResult
        {
            TotalIssues = issues.Count,
            CriticalCount = issues.Count(i => i.Severity == "Critical"),
            HighCount = issues.Count(i => i.Severity == "High"),
            MediumCount = issues.Count(i => i.Severity == "Medium"),
            Issues = issues,
            IssuesByType = issues.GroupBy(i => i.IssueType).ToDictionary(g => g.Key, g => g.Count())
        };
    }

    public async Task<ConcurrencyAnalysisResult> AnalyzeProjectAsync(Project project)
    {
        var allIssues = new List<ConcurrencyIssue>();

        foreach (var document in project.Documents)
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var root = await document.GetSyntaxRootAsync();

            if (semanticModel == null || root == null) continue;

            var result = await AnalyzeAsync(document, semanticModel, root);
            allIssues.AddRange(result.Issues);
        }

        return new ConcurrencyAnalysisResult
        {
            TotalIssues = allIssues.Count,
            CriticalCount = allIssues.Count(i => i.Severity == "Critical"),
            HighCount = allIssues.Count(i => i.Severity == "High"),
            MediumCount = allIssues.Count(i => i.Severity == "Medium"),
            Issues = allIssues,
            IssuesByType = allIssues.GroupBy(i => i.IssueType).ToDictionary(g => g.Key, g => g.Count())
        };
    }
}

public interface IConcurrencyDetector
{
    string Name { get; }
    Task<List<ConcurrencyIssue>> DetectAsync(Document document, SemanticModel model, SyntaxNode root);
}

public record ConcurrencyIssue
{
    public required string IssueType { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required int EndLine { get; init; }
    public string? CodeSnippet { get; init; }
    public string? SuggestedFix { get; init; }
    public string? CweId { get; init; }
}

public record ConcurrencyAnalysisResult
{
    public int TotalIssues { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public List<ConcurrencyIssue> Issues { get; init; } = [];
    public Dictionary<string, int> IssuesByType { get; init; } = [];
}

/// <summary>
/// Detects unawaited Task/ValueTask calls (floating tasks).
/// </summary>
public class FloatingTaskDetector : IConcurrencyDetector
{
    public string Name => "FloatingTask";

    public Task<List<ConcurrencyIssue>> DetectAsync(Document document, SemanticModel model, SyntaxNode root)
    {
        var issues = new List<ConcurrencyIssue>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var typeInfo = model.GetTypeInfo(invocation);
            if (!IsTaskType(typeInfo.Type)) continue;

            // Check if the task is properly handled
            if (IsAwaited(invocation)) continue;
            if (IsAssigned(invocation)) continue;
            if (IsReturned(invocation)) continue;
            if (IsPassedAsArgument(invocation)) continue;
            if (IsInExpressionBody(invocation)) continue;

            var location = invocation.GetLocation().GetLineSpan();
            var methodName = GetMethodName(invocation);

            issues.Add(new ConcurrencyIssue
            {
                IssueType = "FloatingTask",
                Severity = "High",
                Message = $"Task '{methodName}' is not awaited - exceptions may be silently swallowed and execution order may be incorrect",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                EndLine = location.EndLinePosition.Line + 1,
                CodeSnippet = invocation.ToString(),
                SuggestedFix = $"await {invocation};"
            });
        }

        return Task.FromResult(issues);
    }

    private bool IsTaskType(ITypeSymbol? type)
    {
        if (type == null) return false;
        var name = type.ToDisplayString();
        return name.StartsWith("System.Threading.Tasks.Task") ||
               name.StartsWith("System.Threading.Tasks.ValueTask") ||
               name == "System.Threading.Tasks.Task" ||
               name == "System.Threading.Tasks.ValueTask";
    }

    private bool IsAwaited(InvocationExpressionSyntax invocation)
    {
        return invocation.Parent is AwaitExpressionSyntax ||
               (invocation.Parent is MemberAccessExpressionSyntax ma &&
                ma.Parent is AwaitExpressionSyntax);
    }

    private bool IsAssigned(InvocationExpressionSyntax invocation)
    {
        return invocation.Parent is EqualsValueClauseSyntax ||
               invocation.Parent is AssignmentExpressionSyntax;
    }

    private bool IsReturned(InvocationExpressionSyntax invocation)
    {
        return invocation.Parent is ReturnStatementSyntax ||
               invocation.Parent is ArrowExpressionClauseSyntax;
    }

    private bool IsPassedAsArgument(InvocationExpressionSyntax invocation)
    {
        return invocation.Parent is ArgumentSyntax;
    }

    private bool IsInExpressionBody(InvocationExpressionSyntax invocation)
    {
        return invocation.Parent is ArrowExpressionClauseSyntax;
    }

    private string GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => invocation.Expression.ToString()
        };
    }
}

/// <summary>
/// Detects async void methods (except event handlers).
/// </summary>
public class AsyncVoidDetector : IConcurrencyDetector
{
    public string Name => "AsyncVoid";

    public Task<List<ConcurrencyIssue>> DetectAsync(Document document, SemanticModel model, SyntaxNode root)
    {
        var issues = new List<ConcurrencyIssue>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            // Check if async void
            if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword)) continue;
            if (method.ReturnType is not PredefinedTypeSyntax pts) continue;
            if (!pts.Keyword.IsKind(SyntaxKind.VoidKeyword)) continue;

            // Allow event handler pattern
            if (IsEventHandler(method)) continue;

            var location = method.GetLocation().GetLineSpan();

            issues.Add(new ConcurrencyIssue
            {
                IssueType = "AsyncVoid",
                Severity = "High",
                Message = $"Method '{method.Identifier.Text}' is async void - exceptions will crash the process and cannot be awaited",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                EndLine = location.EndLinePosition.Line + 1,
                CodeSnippet = $"async void {method.Identifier.Text}(...)",
                SuggestedFix = $"Change return type to Task: async Task {method.Identifier.Text}(...)"
            });
        }

        // Also check lambdas
        foreach (var lambda in root.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>())
        {
            if (!lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)) continue;

            var typeInfo = model.GetTypeInfo(lambda);
            if (typeInfo.ConvertedType is INamedTypeSymbol nts &&
                nts.DelegateInvokeMethod?.ReturnType.SpecialType == SpecialType.System_Void)
            {
                // Check if it's an event handler delegate
                if (IsEventHandlerDelegate(nts)) continue;

                var location = lambda.GetLocation().GetLineSpan();

                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "AsyncVoidLambda",
                    Severity = "High",
                    Message = "Async lambda with void return - exceptions may be swallowed",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = lambda.ToString().Substring(0, Math.Min(50, lambda.ToString().Length)) + "...",
                    SuggestedFix = "Use Func<Task> delegate instead of Action"
                });
            }
        }

        return Task.FromResult(issues);
    }

    private bool IsEventHandler(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count != 2) return false;

        var first = parameters[0].Type?.ToString() ?? "";
        var second = parameters[1].Type?.ToString() ?? "";

        return (first == "object" || first == "object?") &&
               second.EndsWith("EventArgs");
    }

    private bool IsEventHandlerDelegate(INamedTypeSymbol type)
    {
        return type.Name == "EventHandler" ||
               type.Name.EndsWith("EventHandler") ||
               type.ToDisplayString().Contains("EventHandler");
    }
}

/// <summary>
/// Detects problematic lock patterns.
/// </summary>
public class LockPatternDetector : IConcurrencyDetector
{
    public string Name => "LockPattern";

    public Task<List<ConcurrencyIssue>> DetectAsync(Document document, SemanticModel model, SyntaxNode root)
    {
        var issues = new List<ConcurrencyIssue>();

        foreach (var lockStatement in root.DescendantNodes().OfType<LockStatementSyntax>())
        {
            var expression = lockStatement.Expression;
            var location = lockStatement.GetLocation().GetLineSpan();

            // Lock on this
            if (expression is ThisExpressionSyntax)
            {
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "LockOnThis",
                    Severity = "Medium",
                    Message = "Locking on 'this' is dangerous - external code can also lock on this instance",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = lockStatement.ToString().Split('\n')[0],
                    SuggestedFix = "Use a private readonly object field: private readonly object _lock = new();",
                    CweId = "CWE-667"
                });
            }

            // Lock on string literal
            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "LockOnString",
                    Severity = "High",
                    Message = "Locking on string literal is dangerous - string interning means any code with same string can deadlock",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = lockStatement.ToString().Split('\n')[0],
                    SuggestedFix = "Use a private readonly object field instead",
                    CweId = "CWE-667"
                });
            }

            // Lock on Type object
            if (expression is TypeOfExpressionSyntax)
            {
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "LockOnType",
                    Severity = "High",
                    Message = "Locking on Type object is dangerous - any code can lock on the same Type",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = lockStatement.ToString().Split('\n')[0],
                    SuggestedFix = "Use a private static readonly object field for static locks",
                    CweId = "CWE-667"
                });
            }

            // Lock on value type
            var typeInfo = model.GetTypeInfo(expression);
            if (typeInfo.Type?.IsValueType == true)
            {
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "LockOnValueType",
                    Severity = "Critical",
                    Message = "Locking on value type causes boxing - each lock is on a different object!",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = lockStatement.ToString().Split('\n')[0],
                    SuggestedFix = "Use a reference type for locking",
                    CweId = "CWE-667"
                });
            }

            // Lock on new object()
            if (expression is ObjectCreationExpressionSyntax)
            {
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "LockOnNewObject",
                    Severity = "Critical",
                    Message = "Locking on new object() creates a unique lock each time - no synchronization occurs!",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = lockStatement.ToString().Split('\n')[0],
                    SuggestedFix = "Store the lock object in a field",
                    CweId = "CWE-667"
                });
            }
        }

        // Check for double-checked locking issues
        foreach (var ifStatement in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (IsDoubleCheckedLocking(ifStatement))
            {
                // Check if the field is volatile
                var fieldAccess = GetFieldAccess(ifStatement.Condition);
                if (fieldAccess != null)
                {
                    var symbol = model.GetSymbolInfo(fieldAccess).Symbol;
                    if (symbol is IFieldSymbol field && !field.IsVolatile)
                    {
                        var location = ifStatement.GetLocation().GetLineSpan();
                        issues.Add(new ConcurrencyIssue
                        {
                            IssueType = "DoubleCheckedLocking",
                            Severity = "High",
                            Message = "Double-checked locking without volatile field may fail due to memory model",
                            FilePath = document.FilePath ?? "",
                            Line = location.StartLinePosition.Line + 1,
                            EndLine = location.EndLinePosition.Line + 1,
                            CodeSnippet = ifStatement.ToString().Split('\n')[0],
                            SuggestedFix = "Use Lazy<T> or add volatile modifier to field",
                            CweId = "CWE-609"
                        });
                    }
                }
            }
        }

        return Task.FromResult(issues);
    }

    private bool IsDoubleCheckedLocking(IfStatementSyntax ifStatement)
    {
        // Pattern: if (field == null) { lock (...) { if (field == null) { ... } } }
        if (ifStatement.Statement is not BlockSyntax block) return false;

        foreach (var statement in block.Statements)
        {
            if (statement is LockStatementSyntax lockStmt &&
                lockStmt.Statement is BlockSyntax lockBlock)
            {
                foreach (var inner in lockBlock.Statements)
                {
                    if (inner is IfStatementSyntax innerIf)
                    {
                        // Check if same condition
                        return ifStatement.Condition.ToString() == innerIf.Condition.ToString();
                    }
                }
            }
        }
        return false;
    }

    private ExpressionSyntax? GetFieldAccess(ExpressionSyntax condition)
    {
        if (condition is BinaryExpressionSyntax binary)
        {
            if (binary.Left is IdentifierNameSyntax || binary.Left is MemberAccessExpressionSyntax)
                return binary.Left;
            if (binary.Right is IdentifierNameSyntax || binary.Right is MemberAccessExpressionSyntax)
                return binary.Right;
        }
        return null;
    }
}

/// <summary>
/// Detects race condition patterns.
/// </summary>
public class RaceConditionDetector : IConcurrencyDetector
{
    public string Name => "RaceCondition";

    public Task<List<ConcurrencyIssue>> DetectAsync(Document document, SemanticModel model, SyntaxNode root)
    {
        var issues = new List<ConcurrencyIssue>();

        // Check for check-then-act on collections
        foreach (var ifStatement in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            // Pattern: if (dict.ContainsKey(key)) { var v = dict[key]; }
            if (IsCheckThenActOnCollection(ifStatement, model))
            {
                var location = ifStatement.GetLocation().GetLineSpan();
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "CheckThenActRace",
                    Severity = "High",
                    Message = "Check-then-act pattern on collection may cause race condition in multithreaded context",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = ifStatement.Condition.ToString(),
                    SuggestedFix = "Use TryGetValue or ConcurrentDictionary.GetOrAdd",
                    CweId = "CWE-362"
                });
            }

            // Pattern: if (collection.Count > 0) { var first = collection[0]; }
            if (IsCheckCountThenAccess(ifStatement))
            {
                var location = ifStatement.GetLocation().GetLineSpan();
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "CheckCountThenAccess",
                    Severity = "Medium",
                    Message = "Checking Count then accessing by index may race in multithreaded context",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = ifStatement.Condition.ToString(),
                    SuggestedFix = "Use lock or ConcurrentQueue.TryDequeue pattern"
                });
            }
        }

        // Check for non-atomic operations on shared state
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            // Pattern: field = field + 1 (should be Interlocked.Increment)
            if (IsCompoundAssignmentOnField(assignment, model))
            {
                var location = assignment.GetLocation().GetLineSpan();
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "NonAtomicIncrement",
                    Severity = "Medium",
                    Message = "Non-atomic increment/decrement on field - consider Interlocked operations",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = assignment.ToString(),
                    SuggestedFix = "Use Interlocked.Increment or Interlocked.Add",
                    CweId = "CWE-366"
                });
            }
        }

        return Task.FromResult(issues);
    }

    private bool IsCheckThenActOnCollection(IfStatementSyntax ifStatement, SemanticModel model)
    {
        var condition = ifStatement.Condition.ToString();
        if (!condition.Contains("ContainsKey") && !condition.Contains("Contains")) return false;

        // Check if the body accesses the collection with indexer
        if (ifStatement.Statement is BlockSyntax block)
        {
            foreach (var statement in block.Statements)
            {
                var text = statement.ToString();
                if (text.Contains("[") && text.Contains("]"))
                    return true;
            }
        }
        return false;
    }

    private bool IsCheckCountThenAccess(IfStatementSyntax ifStatement)
    {
        var condition = ifStatement.Condition.ToString();
        if (!condition.Contains(".Count") && !condition.Contains(".Length")) return false;

        if (ifStatement.Statement is BlockSyntax block)
        {
            foreach (var statement in block.Statements)
            {
                var text = statement.ToString();
                if (text.Contains("[0]") || text.Contains("[^1]") || text.Contains(".First()"))
                    return true;
            }
        }
        return false;
    }

    private bool IsCompoundAssignmentOnField(AssignmentExpressionSyntax assignment, SemanticModel model)
    {
        if (assignment.IsKind(SyntaxKind.AddAssignmentExpression) ||
            assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
        {
            var symbol = model.GetSymbolInfo(assignment.Left).Symbol;
            if (symbol is IFieldSymbol field && !field.IsConst && !field.IsReadOnly)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Detects thread safety issues with static state.
/// </summary>
public class ThreadSafetyDetector : IConcurrencyDetector
{
    public string Name => "ThreadSafety";

    public Task<List<ConcurrencyIssue>> DetectAsync(Document document, SemanticModel model, SyntaxNode root)
    {
        var issues = new List<ConcurrencyIssue>();

        // Check for mutable static fields
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
            if (field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)) continue;
            if (field.Modifiers.Any(SyntaxKind.ConstKeyword)) continue;

            var typeInfo = model.GetTypeInfo(field.Declaration.Type);
            if (IsMutableType(typeInfo.Type))
            {
                var location = field.GetLocation().GetLineSpan();
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "MutableStaticField",
                    Severity = "Medium",
                    Message = "Mutable static field may cause thread safety issues",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = field.ToString().Trim(),
                    SuggestedFix = "Use ConcurrentDictionary, lock, or make readonly"
                });
            }
        }

        // Check for Task.Run in constructor
        foreach (var constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var invocation in constructor.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var text = invocation.Expression.ToString();
                if (text.Contains("Task.Run") || text.Contains("Task.Factory.StartNew"))
                {
                    var location = invocation.GetLocation().GetLineSpan();
                    issues.Add(new ConcurrencyIssue
                    {
                        IssueType = "TaskRunInConstructor",
                        Severity = "Medium",
                        Message = "Starting tasks in constructor can cause issues with partially constructed objects",
                        FilePath = document.FilePath ?? "",
                        Line = location.StartLinePosition.Line + 1,
                        EndLine = location.EndLinePosition.Line + 1,
                        CodeSnippet = invocation.ToString(),
                        SuggestedFix = "Move task creation to a factory method or initialization method"
                    });
                }
            }
        }

        // Check for accessing shared collection without synchronization
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;

            foreach (var access in method.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                var symbol = model.GetSymbolInfo(access.Expression).Symbol;
                if (symbol is IFieldSymbol field && field.IsStatic && !IsThreadSafeCollection(field.Type))
                {
                    // Check if inside lock
                    if (!IsInsideLock(access))
                    {
                        var location = access.GetLocation().GetLineSpan();
                        issues.Add(new ConcurrencyIssue
                        {
                            IssueType = "UnsynchronizedCollectionAccess",
                            Severity = "High",
                            Message = "Static collection access in static method without synchronization",
                            FilePath = document.FilePath ?? "",
                            Line = location.StartLinePosition.Line + 1,
                            EndLine = location.EndLinePosition.Line + 1,
                            CodeSnippet = access.ToString(),
                            SuggestedFix = "Use ConcurrentDictionary or add lock statement"
                        });
                    }
                }
            }
        }

        return Task.FromResult(issues);
    }

    private bool IsMutableType(ITypeSymbol? type)
    {
        if (type == null) return false;
        var name = type.ToDisplayString();
        return name.StartsWith("System.Collections.Generic.List") ||
               name.StartsWith("System.Collections.Generic.Dictionary") ||
               name.StartsWith("System.Collections.Generic.HashSet") ||
               name.StartsWith("System.Collections.ArrayList") ||
               name.StartsWith("System.Collections.Hashtable");
    }

    private bool IsThreadSafeCollection(ITypeSymbol type)
    {
        var name = type.ToDisplayString();
        return name.StartsWith("System.Collections.Concurrent.");
    }

    private bool IsInsideLock(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is LockStatementSyntax) return true;
            current = current.Parent;
        }
        return false;
    }
}

/// <summary>
/// Detects potential deadlock patterns.
/// </summary>
public class DeadlockRiskDetector : IConcurrencyDetector
{
    public string Name => "DeadlockRisk";

    public Task<List<ConcurrencyIssue>> DetectAsync(Document document, SemanticModel model, SyntaxNode root)
    {
        var issues = new List<ConcurrencyIssue>();

        // Check for .Result or .Wait() on Task (sync over async)
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var name = access.Name.Identifier.Text;
            if (name != "Result" && name != "Wait" && name != "GetAwaiter") continue;

            var typeInfo = model.GetTypeInfo(access.Expression);
            if (!IsTaskType(typeInfo.Type)) continue;

            // GetAwaiter().GetResult() pattern
            if (name == "GetAwaiter" && access.Parent is InvocationExpressionSyntax inv)
            {
                if (inv.Parent is MemberAccessExpressionSyntax getResult &&
                    getResult.Name.Identifier.Text == "GetResult")
                {
                    var location = access.GetLocation().GetLineSpan();
                    issues.Add(new ConcurrencyIssue
                    {
                        IssueType = "SyncOverAsync",
                        Severity = "High",
                        Message = ".GetAwaiter().GetResult() can cause deadlock in synchronization context",
                        FilePath = document.FilePath ?? "",
                        Line = location.StartLinePosition.Line + 1,
                        EndLine = location.EndLinePosition.Line + 1,
                        CodeSnippet = access.Parent?.Parent?.ToString() ?? access.ToString(),
                        SuggestedFix = "Use await instead, or ensure ConfigureAwait(false) is used throughout"
                    });
                }
                continue;
            }

            if (name == "Result" || name == "Wait")
            {
                var location = access.GetLocation().GetLineSpan();
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "SyncOverAsync",
                    Severity = "High",
                    Message = $".{name} can cause deadlock in synchronization context",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = access.ToString(),
                    SuggestedFix = "Use await instead"
                });
            }
        }

        // Check for nested locks (potential deadlock)
        foreach (var lockStatement in root.DescendantNodes().OfType<LockStatementSyntax>())
        {
            var nestedLocks = lockStatement.Statement.DescendantNodes().OfType<LockStatementSyntax>();
            if (nestedLocks.Any())
            {
                var location = lockStatement.GetLocation().GetLineSpan();
                issues.Add(new ConcurrencyIssue
                {
                    IssueType = "NestedLocks",
                    Severity = "Medium",
                    Message = "Nested locks can lead to deadlock if locks are acquired in different order elsewhere",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    EndLine = location.EndLinePosition.Line + 1,
                    CodeSnippet = lockStatement.ToString().Split('\n')[0],
                    SuggestedFix = "Ensure consistent lock ordering throughout the codebase"
                });
            }
        }

        return Task.FromResult(issues);
    }

    private bool IsTaskType(ITypeSymbol? type)
    {
        if (type == null) return false;
        var name = type.ToDisplayString();
        return name.StartsWith("System.Threading.Tasks.Task") ||
               name.StartsWith("System.Threading.Tasks.ValueTask");
    }
}
