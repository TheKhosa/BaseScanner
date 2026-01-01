using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.Analyzers.Frameworks;

/// <summary>
/// Analyzes Entity Framework Core specific issues and anti-patterns.
/// </summary>
public class EntityFrameworkAnalyzer
{
    public async Task<FrameworkAnalysisResult> AnalyzeAsync(Project project)
    {
        var issues = new List<FrameworkIssue>();

        foreach (var document in project.Documents)
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var root = await document.GetSyntaxRootAsync();

            if (semanticModel == null || root == null) continue;

            issues.AddRange(await AnalyzeDocumentAsync(document, semanticModel, root));
        }

        return new FrameworkAnalysisResult
        {
            Framework = "Entity Framework Core",
            TotalIssues = issues.Count,
            CriticalCount = issues.Count(i => i.Severity == "Critical"),
            HighCount = issues.Count(i => i.Severity == "High"),
            MediumCount = issues.Count(i => i.Severity == "Medium"),
            Issues = issues,
            IssuesByType = issues.GroupBy(i => i.IssueType).ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private async Task<List<FrameworkIssue>> AnalyzeDocumentAsync(
        Document document, SemanticModel model, SyntaxNode root)
    {
        var issues = new List<FrameworkIssue>();

        issues.AddRange(DetectNPlusOneQueries(root, document, model));
        issues.AddRange(DetectMissingAsNoTracking(root, document, model));
        issues.AddRange(DetectCartesianExplosion(root, document, model));
        issues.AddRange(DetectRawSqlInjection(root, document, model));
        issues.AddRange(DetectClientEvaluation(root, document, model));
        issues.AddRange(DetectDisposedContextAccess(root, document, model));
        issues.AddRange(DetectLazyLoadingInLoop(root, document, model));
        issues.AddRange(DetectLongRunningTransaction(root, document, model));
        issues.AddRange(DetectMissingConcurrencyToken(root, document, model));

        return issues;
    }

    private List<FrameworkIssue> DetectNPlusOneQueries(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();

        // Look for navigation property access inside loops
        foreach (var loop in root.DescendantNodes().Where(n =>
            n is ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax))
        {
            var loopBody = loop switch
            {
                ForEachStatementSyntax f => f.Statement,
                ForStatementSyntax f => f.Statement,
                WhileStatementSyntax w => w.Statement,
                _ => null
            };

            if (loopBody == null) continue;

            foreach (var access in loopBody.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var symbol = model.GetSymbolInfo(access).Symbol;
                if (symbol is IPropertySymbol property)
                {
                    // Check if it's a navigation property (collection or reference)
                    var propType = property.Type;
                    if (IsNavigationProperty(propType) || IsCollectionType(propType))
                    {
                        // Check if the parent expression is from the loop variable
                        var location = access.GetLocation().GetLineSpan();
                        issues.Add(new FrameworkIssue
                        {
                            IssueType = "NPlusOneQuery",
                            Severity = "High",
                            Message = $"Potential N+1 query: accessing navigation property '{property.Name}' inside a loop",
                            FilePath = document.FilePath ?? "",
                            Line = location.StartLinePosition.Line + 1,
                            SuggestedFix = "Use .Include() to eager load the navigation property before the loop",
                            CodeSnippet = access.ToString()
                        });
                    }
                }
            }
        }

        return issues;
    }

    private List<FrameworkIssue> DetectMissingAsNoTracking(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            // Look for read-only patterns (Get, Find, List, Search in name)
            var methodName = method.Identifier.Text.ToLower();
            if (!methodName.StartsWith("get") && !methodName.StartsWith("find") &&
                !methodName.StartsWith("list") && !methodName.StartsWith("search") &&
                !methodName.StartsWith("read"))
                continue;

            // Check if any DbSet query lacks AsNoTracking
            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!IsDbSetQuery(invocation, model)) continue;

                // Check if AsNoTracking is called in the chain
                var parent = invocation.Parent;
                var hasAsNoTracking = false;
                while (parent != null && parent is not StatementSyntax)
                {
                    if (parent.ToString().Contains("AsNoTracking"))
                    {
                        hasAsNoTracking = true;
                        break;
                    }
                    parent = parent.Parent;
                }

                if (!hasAsNoTracking)
                {
                    var location = invocation.GetLocation().GetLineSpan();
                    issues.Add(new FrameworkIssue
                    {
                        IssueType = "MissingAsNoTracking",
                        Severity = "Medium",
                        Message = $"Query in read-only method '{method.Identifier.Text}' may benefit from .AsNoTracking()",
                        FilePath = document.FilePath ?? "",
                        Line = location.StartLinePosition.Line + 1,
                        SuggestedFix = "Add .AsNoTracking() for read-only queries to improve performance",
                        CodeSnippet = invocation.ToString().Substring(0, Math.Min(60, invocation.ToString().Length))
                    });
                }
            }
        }

        return issues;
    }

    private List<FrameworkIssue> DetectCartesianExplosion(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetMethodName(invocation);
            if (methodName != "Include" && methodName != "ThenInclude") continue;

            // Count consecutive includes
            var includeCount = CountIncludes(invocation);

            if (includeCount >= 3)
            {
                var location = invocation.GetLocation().GetLineSpan();
                issues.Add(new FrameworkIssue
                {
                    IssueType = "CartesianExplosion",
                    Severity = "Medium",
                    Message = $"Query has {includeCount} includes - may cause Cartesian explosion and poor performance",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    SuggestedFix = "Consider using split queries with .AsSplitQuery() or loading related data separately"
                });
            }
        }

        return issues;
    }

    private List<FrameworkIssue> DetectRawSqlInjection(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();
        var sqlMethods = new[] { "FromSqlRaw", "ExecuteSqlRaw", "FromSql" };

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetMethodName(invocation);
            if (!sqlMethods.Contains(methodName)) continue;

            var args = invocation.ArgumentList.Arguments;
            if (args.Count == 0) continue;

            var sqlArg = args[0].Expression;

            // Check for string concatenation or interpolation
            if (sqlArg is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
            {
                var location = invocation.GetLocation().GetLineSpan();
                issues.Add(new FrameworkIssue
                {
                    IssueType = "RawSqlInjection",
                    Severity = "Critical",
                    Message = $"SQL injection vulnerability: string concatenation in {methodName}",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    CweId = "CWE-89",
                    SuggestedFix = "Use parameterized queries: FromSqlInterpolated() or pass parameters separately",
                    CodeSnippet = sqlArg.ToString().Substring(0, Math.Min(80, sqlArg.ToString().Length))
                });
            }

            // Check for non-constant string
            if (sqlArg is IdentifierNameSyntax)
            {
                var location = invocation.GetLocation().GetLineSpan();
                issues.Add(new FrameworkIssue
                {
                    IssueType = "RawSqlInjection",
                    Severity = "High",
                    Message = $"SQL from variable in {methodName} - ensure input is validated",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    CweId = "CWE-89",
                    SuggestedFix = "Use FromSqlInterpolated() for parameterized queries"
                });
            }
        }

        return issues;
    }

    private List<FrameworkIssue> DetectClientEvaluation(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();

        // Methods that typically can't be translated
        var clientOnlyMethods = new[] {
            "ToString", "GetType", "GetHashCode", "Format",
            "Parse", "TryParse", "Split", "Join"
        };

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // Check if inside a LINQ query on DbSet
            if (!IsInsideLinqQuery(invocation)) continue;

            var methodName = GetMethodName(invocation);
            if (clientOnlyMethods.Contains(methodName))
            {
                var location = invocation.GetLocation().GetLineSpan();
                issues.Add(new FrameworkIssue
                {
                    IssueType = "ClientEvaluation",
                    Severity = "Medium",
                    Message = $"Method '{methodName}' in LINQ query may cause client evaluation",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    SuggestedFix = "Move the operation outside the query or use an equivalent that can be translated"
                });
            }
        }

        return issues;
    }

    private List<FrameworkIssue> DetectDisposedContextAccess(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();

        foreach (var usingStatement in root.DescendantNodes().OfType<UsingStatementSyntax>())
        {
            // Check for DbContext in using
            var declaration = usingStatement.Declaration;
            if (declaration == null) continue;

            var typeName = declaration.Type.ToString();
            if (!typeName.Contains("Context") && !typeName.Contains("DbContext")) continue;

            var variableName = declaration.Variables.FirstOrDefault()?.Identifier.Text;
            if (variableName == null) continue;

            // Look for access after the using block
            var parent = usingStatement.Parent;
            if (parent is BlockSyntax block)
            {
                var usingIndex = block.Statements.IndexOf(usingStatement);
                for (int i = usingIndex + 1; i < block.Statements.Count; i++)
                {
                    var statement = block.Statements[i];
                    if (statement.ToString().Contains(variableName))
                    {
                        var location = statement.GetLocation().GetLineSpan();
                        issues.Add(new FrameworkIssue
                        {
                            IssueType = "DisposedContextAccess",
                            Severity = "Critical",
                            Message = $"DbContext '{variableName}' accessed after disposal",
                            FilePath = document.FilePath ?? "",
                            Line = location.StartLinePosition.Line + 1,
                            SuggestedFix = "Ensure all data access completes before the context is disposed"
                        });
                    }
                }
            }
        }

        return issues;
    }

    private List<FrameworkIssue> DetectLazyLoadingInLoop(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();

        // Check for virtual navigation properties accessed in loops
        foreach (var loop in root.DescendantNodes().Where(n =>
            n is ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax))
        {
            var loopBody = loop switch
            {
                ForEachStatementSyntax f => f.Statement,
                ForStatementSyntax f => f.Statement,
                WhileStatementSyntax w => w.Statement,
                _ => null
            };

            if (loopBody == null) continue;

            foreach (var access in loopBody.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var symbol = model.GetSymbolInfo(access).Symbol;
                if (symbol is IPropertySymbol property && property.IsVirtual)
                {
                    var propType = property.Type.ToDisplayString();
                    if (propType.StartsWith("System.Collections") || IsNavigationProperty(property.Type))
                    {
                        var location = access.GetLocation().GetLineSpan();
                        issues.Add(new FrameworkIssue
                        {
                            IssueType = "LazyLoadingInLoop",
                            Severity = "High",
                            Message = $"Virtual navigation property '{property.Name}' accessed in loop may trigger N+1 lazy loads",
                            FilePath = document.FilePath ?? "",
                            Line = location.StartLinePosition.Line + 1,
                            SuggestedFix = "Use .Include() to eager load, or disable lazy loading"
                        });
                    }
                }
            }
        }

        return issues;
    }

    private List<FrameworkIssue> DetectLongRunningTransaction(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();

        foreach (var usingStatement in root.DescendantNodes().OfType<UsingStatementSyntax>())
        {
            // Check for TransactionScope or BeginTransaction
            var text = usingStatement.ToString();
            if (!text.Contains("TransactionScope") && !text.Contains("BeginTransaction")) continue;

            // Check for await inside the transaction
            var awaitCount = usingStatement.Statement.DescendantNodes().OfType<AwaitExpressionSyntax>().Count();
            var methodCalls = usingStatement.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>().Count();

            if (awaitCount > 3 || methodCalls > 10)
            {
                var location = usingStatement.GetLocation().GetLineSpan();
                issues.Add(new FrameworkIssue
                {
                    IssueType = "LongRunningTransaction",
                    Severity = "Medium",
                    Message = "Transaction scope appears to be long-running - may cause lock contention",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    SuggestedFix = "Keep transactions short and move non-transactional work outside"
                });
            }
        }

        return issues;
    }

    private List<FrameworkIssue> DetectMissingConcurrencyToken(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();

        // Look for entity classes
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!IsEntityClass(classDecl, model)) continue;

            var hasRowVersion = classDecl.Members.OfType<PropertyDeclarationSyntax>()
                .Any(p => HasAttribute(p, "Timestamp") || HasAttribute(p, "ConcurrencyCheck") ||
                          p.Identifier.Text == "RowVersion" || p.Identifier.Text == "Version");

            if (!hasRowVersion)
            {
                var location = classDecl.Identifier.GetLocation().GetLineSpan();
                issues.Add(new FrameworkIssue
                {
                    IssueType = "MissingConcurrencyToken",
                    Severity = "Low",
                    Message = $"Entity '{classDecl.Identifier.Text}' lacks concurrency token - concurrent updates may overwrite each other",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    SuggestedFix = "Add a [Timestamp] byte[] RowVersion property for optimistic concurrency"
                });
            }
        }

        return issues;
    }

    // Helper methods
    private bool IsNavigationProperty(ITypeSymbol type)
    {
        if (type == null) return false;
        var name = type.ToDisplayString();
        return !name.StartsWith("System.") && !type.IsValueType &&
               type.TypeKind == TypeKind.Class && name != "string";
    }

    private bool IsCollectionType(ITypeSymbol type)
    {
        var name = type.ToDisplayString();
        return name.StartsWith("System.Collections") ||
               name.Contains("ICollection") ||
               name.Contains("IList") ||
               name.Contains("IEnumerable");
    }

    private bool IsDbSetQuery(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var expr = invocation.Expression;
        while (expr is MemberAccessExpressionSyntax ma)
        {
            var typeInfo = model.GetTypeInfo(ma.Expression);
            var typeName = typeInfo.Type?.ToDisplayString() ?? "";
            if (typeName.Contains("DbSet") || typeName.Contains("DbContext"))
                return true;
            expr = ma.Expression;
        }
        return false;
    }

    private string GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => ""
        };
    }

    private int CountIncludes(InvocationExpressionSyntax invocation)
    {
        var count = 0;
        SyntaxNode? current = invocation;

        while (current != null)
        {
            if (current is InvocationExpressionSyntax inv)
            {
                var name = GetMethodName(inv);
                if (name == "Include" || name == "ThenInclude")
                    count++;
            }
            current = current.Parent;
        }

        return count;
    }

    private bool IsInsideLinqQuery(InvocationExpressionSyntax invocation)
    {
        var current = invocation.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax parentInv)
            {
                var name = GetMethodName(parentInv);
                if (name is "Where" or "Select" or "OrderBy" or "GroupBy" or "Any" or "All" or "First" or "FirstOrDefault")
                    return true;
            }
            current = current.Parent;
        }
        return false;
    }

    private bool IsEntityClass(ClassDeclarationSyntax classDecl, SemanticModel model)
    {
        // Check for [Table] attribute
        if (HasAttribute(classDecl, "Table")) return true;

        // Check for Id property with [Key]
        var hasKey = classDecl.Members.OfType<PropertyDeclarationSyntax>()
            .Any(p => HasAttribute(p, "Key") || p.Identifier.Text == "Id");

        // Check for navigation properties
        var hasNavigation = classDecl.Members.OfType<PropertyDeclarationSyntax>()
            .Any(p => p.Modifiers.Any(SyntaxKind.VirtualKeyword));

        return hasKey && hasNavigation;
    }

    private bool HasAttribute(MemberDeclarationSyntax member, string attributeName)
    {
        foreach (var attrList in member.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (name == attributeName || name == attributeName + "Attribute")
                    return true;
            }
        }
        return false;
    }
}
