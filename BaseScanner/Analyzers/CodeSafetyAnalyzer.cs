using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.Analyzers;

/// <summary>
/// Analyzes code safety: null safety issues, immutability opportunities, and logging coverage.
/// </summary>
public class CodeSafetyAnalyzer
{
    public record NullSafetyIssue
    {
        public required string Type { get; init; } // PossibleNull, MissingNullCheck, NullableReturn, etc.
        public required string Severity { get; init; }
        public required string ClassName { get; init; }
        public required string MemberName { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string Description { get; init; }
        public required string Suggestion { get; init; }
    }

    public record ImmutabilityIssue
    {
        public required string Type { get; init; } // MutableField, MutableProperty, MutableCollection, etc.
        public required string ClassName { get; init; }
        public required string MemberName { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string Description { get; init; }
        public required string Suggestion { get; init; }
    }

    public record LoggingGap
    {
        public required string ClassName { get; init; }
        public required string MethodName { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string GapType { get; init; } // NoLogging, NoCatchLogging, NoEntryLogging, etc.
        public required string Description { get; init; }
    }

    public record ClassLoggingCoverage
    {
        public required string ClassName { get; init; }
        public required string FilePath { get; init; }
        public required int TotalMethods { get; init; }
        public required int MethodsWithLogging { get; init; }
        public required int TotalCatchBlocks { get; init; }
        public required int CatchBlocksWithLogging { get; init; }
        public required double CoveragePercent { get; init; }
    }

    public async Task<(List<NullSafetyIssue> NullIssues, List<ImmutabilityIssue> ImmutabilityIssues,
        List<LoggingGap> LoggingGaps, List<ClassLoggingCoverage> LoggingCoverage)> AnalyzeAsync(Project project)
    {
        var nullIssues = new List<NullSafetyIssue>();
        var immutabilityIssues = new List<ImmutabilityIssue>();
        var loggingGaps = new List<LoggingGap>();
        var loggingCoverage = new List<ClassLoggingCoverage>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;
            if (document.FilePath.Contains(".Designer.cs")) continue;

            var syntaxRoot = await document.GetSyntaxRootAsync();
            var semanticModel = await document.GetSemanticModelAsync();
            if (syntaxRoot == null || semanticModel == null) continue;

            // Analyze null safety
            nullIssues.AddRange(AnalyzeNullSafety(syntaxRoot, semanticModel, document.FilePath));

            // Analyze immutability
            immutabilityIssues.AddRange(AnalyzeImmutability(syntaxRoot, semanticModel, document.FilePath));

            // Analyze logging coverage
            var (gaps, coverage) = AnalyzeLoggingCoverage(syntaxRoot, document.FilePath);
            loggingGaps.AddRange(gaps);
            loggingCoverage.AddRange(coverage);
        }

        return (nullIssues.OrderBy(n => n.FilePath).ThenBy(n => n.Line).ToList(),
                immutabilityIssues.OrderBy(i => i.FilePath).ThenBy(i => i.Line).ToList(),
                loggingGaps.OrderBy(l => l.FilePath).ThenBy(l => l.Line).ToList(),
                loggingCoverage.OrderBy(c => c.CoveragePercent).ToList());
    }

    private IEnumerable<NullSafetyIssue> AnalyzeNullSafety(SyntaxNode root, SemanticModel semanticModel, string filePath)
    {
        var issues = new List<NullSafetyIssue>();

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            var className = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";
            var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (body == null) continue;

            // Check for null returns without nullable return type
            if (method.ReturnType.ToString() != "void")
            {
                var returnStatements = body.DescendantNodes().OfType<ReturnStatementSyntax>();
                foreach (var ret in returnStatements)
                {
                    if (ret.Expression is LiteralExpressionSyntax literal &&
                        literal.IsKind(SyntaxKind.NullLiteralExpression))
                    {
                        var returnType = method.ReturnType.ToString();
                        if (!returnType.EndsWith("?") && !returnType.StartsWith("Nullable"))
                        {
                            issues.Add(new NullSafetyIssue
                            {
                                Type = "NullReturn",
                                Severity = "Warning",
                                ClassName = className,
                                MemberName = method.Identifier.Text,
                                FilePath = filePath,
                                Line = ret.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                                Description = $"Method returns null but return type '{returnType}' is not nullable",
                                Suggestion = $"Change return type to '{returnType}?' or return a default value"
                            });
                        }
                    }
                }
            }

            // Check for member access without null checks
            var memberAccesses = body.DescendantNodes().OfType<MemberAccessExpressionSyntax>();
            foreach (var access in memberAccesses)
            {
                // Check if accessing a parameter that could be null
                if (access.Expression is IdentifierNameSyntax identifier)
                {
                    var param = method.ParameterList.Parameters
                        .FirstOrDefault(p => p.Identifier.Text == identifier.Identifier.Text);

                    if (param != null)
                    {
                        var paramType = param.Type?.ToString() ?? "";
                        // Check if it's a reference type without null check
                        if (!paramType.EndsWith("?") &&
                            !IsPrimitiveType(paramType) &&
                            !HasNullCheck(body, identifier.Identifier.Text))
                        {
                            // This is a potential issue, but we need to be careful not to over-report
                            // Only report if the parameter is used in multiple places without checking
                            var usages = body.DescendantNodes().OfType<IdentifierNameSyntax>()
                                .Count(i => i.Identifier.Text == identifier.Identifier.Text);

                            if (usages >= 3)
                            {
                                issues.Add(new NullSafetyIssue
                                {
                                    Type = "MissingNullCheck",
                                    Severity = "Info",
                                    ClassName = className,
                                    MemberName = method.Identifier.Text,
                                    FilePath = filePath,
                                    Line = access.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                                    Description = $"Parameter '{identifier.Identifier.Text}' accessed without null check",
                                    Suggestion = "Add null check or use null-conditional operator (?.)"
                                });
                                break; // Only report once per parameter
                            }
                        }
                    }
                }
            }

            // Check for potential null dereference after assignment
            var assignments = body.DescendantNodes().OfType<AssignmentExpressionSyntax>();
            foreach (var assignment in assignments)
            {
                if (assignment.Right is LiteralExpressionSyntax lit &&
                    lit.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    var varName = assignment.Left.ToString();

                    // Check if this variable is used after without null check
                    var laterAccesses = body.DescendantNodes()
                        .SkipWhile(n => n != assignment)
                        .OfType<MemberAccessExpressionSyntax>()
                        .Where(m => m.Expression.ToString() == varName)
                        .ToList();

                    if (laterAccesses.Count > 0)
                    {
                        issues.Add(new NullSafetyIssue
                        {
                            Type = "NullAssignmentThenAccess",
                            Severity = "Warning",
                            ClassName = className,
                            MemberName = method.Identifier.Text,
                            FilePath = filePath,
                            Line = laterAccesses.First().GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            Description = $"Variable '{varName}' may be null here (assigned null earlier)",
                            Suggestion = "Add null check before accessing members"
                        });
                    }
                }
            }
        }

        return issues;
    }

    private IEnumerable<ImmutabilityIssue> AnalyzeImmutability(SyntaxNode root, SemanticModel semanticModel, string filePath)
    {
        var issues = new List<ImmutabilityIssue>();

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            var className = classDecl.Identifier.Text;

            // Check fields that could be readonly
            var fields = classDecl.Members.OfType<FieldDeclarationSyntax>();
            foreach (var field in fields)
            {
                if (field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) ||
                    field.Modifiers.Any(SyntaxKind.ConstKeyword))
                    continue;

                var fieldName = field.Declaration.Variables.First().Identifier.Text;

                // Check if field is ever assigned outside constructor
                var isAssignedOutsideConstructor = classDecl.Members
                    .OfType<MethodDeclarationSyntax>()
                    .Any(m => m.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                        .Any(a => a.Left.ToString() == fieldName || a.Left.ToString() == $"this.{fieldName}"));

                if (!isAssignedOutsideConstructor)
                {
                    issues.Add(new ImmutabilityIssue
                    {
                        Type = "CouldBeReadonly",
                        ClassName = className,
                        MemberName = fieldName,
                        FilePath = filePath,
                        Line = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Description = $"Field '{fieldName}' is only assigned in constructor",
                        Suggestion = "Add 'readonly' modifier to prevent accidental modification"
                    });
                }

                // Check for mutable collections
                var fieldType = field.Declaration.Type.ToString();
                if (IsMutableCollectionType(fieldType) && !field.Modifiers.Any(SyntaxKind.PrivateKeyword))
                {
                    issues.Add(new ImmutabilityIssue
                    {
                        Type = "MutableCollection",
                        ClassName = className,
                        MemberName = fieldName,
                        FilePath = filePath,
                        Line = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Description = $"Public/protected field '{fieldName}' exposes mutable collection '{fieldType}'",
                        Suggestion = "Return IReadOnlyList/IReadOnlyCollection or make a copy"
                    });
                }
            }

            // Check properties that expose mutable state
            var properties = classDecl.Members.OfType<PropertyDeclarationSyntax>();
            foreach (var prop in properties)
            {
                var propType = prop.Type.ToString();

                // Check for auto-properties with public setters that could be init-only
                if (prop.AccessorList != null)
                {
                    var setter = prop.AccessorList.Accessors
                        .FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));

                    if (setter != null && !setter.Modifiers.Any(SyntaxKind.PrivateKeyword))
                    {
                        // Check if set is only called in constructor
                        var isSetOutsideConstructor = classDecl.Members
                            .OfType<MethodDeclarationSyntax>()
                            .Any(m => m.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                                .Any(a => a.Left.ToString() == prop.Identifier.Text));

                        if (!isSetOutsideConstructor)
                        {
                            issues.Add(new ImmutabilityIssue
                            {
                                Type = "CouldBeInitOnly",
                                ClassName = className,
                                MemberName = prop.Identifier.Text,
                                FilePath = filePath,
                                Line = prop.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                                Description = $"Property '{prop.Identifier.Text}' setter is only used in construction",
                                Suggestion = "Use 'init' accessor instead of 'set' for immutability"
                            });
                        }
                    }
                }

                // Check for properties returning mutable collections
                if (IsMutableCollectionType(propType))
                {
                    issues.Add(new ImmutabilityIssue
                    {
                        Type = "MutableCollectionProperty",
                        ClassName = className,
                        MemberName = prop.Identifier.Text,
                        FilePath = filePath,
                        Line = prop.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Description = $"Property '{prop.Identifier.Text}' returns mutable collection '{propType}'",
                        Suggestion = "Return IReadOnlyList<T> or IEnumerable<T> instead"
                    });
                }
            }
        }

        return issues;
    }

    private (List<LoggingGap> Gaps, List<ClassLoggingCoverage> Coverage) AnalyzeLoggingCoverage(SyntaxNode root, string filePath)
    {
        var gaps = new List<LoggingGap>();
        var coverage = new List<ClassLoggingCoverage>();

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            var className = classDecl.Identifier.Text;
            var methods = classDecl.Members.OfType<MethodDeclarationSyntax>().ToList();

            int totalMethods = 0;
            int methodsWithLogging = 0;
            int totalCatchBlocks = 0;
            int catchBlocksWithLogging = 0;

            foreach (var method in methods)
            {
                var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body == null) continue;

                totalMethods++;

                var hasLogging = HasLoggingCall(body);
                if (hasLogging) methodsWithLogging++;

                // Check catch blocks
                var catchClauses = body.DescendantNodes().OfType<CatchClauseSyntax>();
                foreach (var catchClause in catchClauses)
                {
                    totalCatchBlocks++;

                    if (HasLoggingCall(catchClause.Block))
                    {
                        catchBlocksWithLogging++;
                    }
                    else
                    {
                        gaps.Add(new LoggingGap
                        {
                            ClassName = className,
                            MethodName = method.Identifier.Text,
                            FilePath = filePath,
                            Line = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            GapType = "NoCatchLogging",
                            Description = "Catch block without logging - exceptions may be silently handled"
                        });
                    }
                }

                // Check public methods without any logging
                if (method.Modifiers.Any(SyntaxKind.PublicKeyword) && !hasLogging)
                {
                    var stmtCount = body.DescendantNodes().OfType<StatementSyntax>().Count();
                    if (stmtCount > 10) // Only flag substantial methods
                    {
                        gaps.Add(new LoggingGap
                        {
                            ClassName = className,
                            MethodName = method.Identifier.Text,
                            FilePath = filePath,
                            Line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            GapType = "NoMethodLogging",
                            Description = $"Public method with {stmtCount} statements has no logging"
                        });
                    }
                }
            }

            if (totalMethods > 0)
            {
                var totalLoggable = totalMethods + totalCatchBlocks;
                var totalWithLogging = methodsWithLogging + catchBlocksWithLogging;
                var coveragePercent = totalLoggable > 0 ? (double)totalWithLogging / totalLoggable * 100 : 100;

                coverage.Add(new ClassLoggingCoverage
                {
                    ClassName = className,
                    FilePath = filePath,
                    TotalMethods = totalMethods,
                    MethodsWithLogging = methodsWithLogging,
                    TotalCatchBlocks = totalCatchBlocks,
                    CatchBlocksWithLogging = catchBlocksWithLogging,
                    CoveragePercent = coveragePercent
                });
            }
        }

        return (gaps, coverage);
    }

    private bool HasLoggingCall(SyntaxNode node)
    {
        var invocations = node.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var inv in invocations)
        {
            var text = inv.ToString().ToLower();
            if (text.Contains("log.") || text.Contains("logger.") ||
                text.Contains("_log.") || text.Contains("_logger.") ||
                text.Contains("console.write") || text.Contains("debug.write") ||
                text.Contains("trace.") || text.Contains("logerror") ||
                text.Contains("loginfo") || text.Contains("logwarning") ||
                text.Contains("logdebug") || text.Contains(".log("))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasNullCheck(SyntaxNode scope, string identifier)
    {
        // Check for various forms of null checks
        var conditionals = scope.DescendantNodes()
            .Where(n => n is IfStatementSyntax or ConditionalExpressionSyntax or BinaryExpressionSyntax);

        foreach (var cond in conditionals)
        {
            var text = cond.ToString();
            if (text.Contains($"{identifier} == null") ||
                text.Contains($"{identifier} != null") ||
                text.Contains($"{identifier} is null") ||
                text.Contains($"{identifier} is not null") ||
                text.Contains($"{identifier}?.") ||
                text.Contains($"{identifier} ??"))
            {
                return true;
            }
        }

        // Check for ArgumentNullException.ThrowIfNull
        var invocations = scope.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var inv in invocations)
        {
            var text = inv.ToString();
            if (text.Contains("ThrowIfNull") && text.Contains(identifier))
                return true;
            if (text.Contains("ArgumentNullException") && text.Contains(identifier))
                return true;
        }

        return false;
    }

    private bool IsPrimitiveType(string typeName)
    {
        return typeName is "int" or "long" or "short" or "byte" or "bool" or "float" or "double" or "decimal" or
            "char" or "Int32" or "Int64" or "Boolean" or "DateTime" or "Guid" or "TimeSpan" or
            "uint" or "ulong" or "ushort" or "sbyte";
    }

    private bool IsMutableCollectionType(string typeName)
    {
        return typeName.StartsWith("List<") ||
               typeName.StartsWith("Dictionary<") ||
               typeName.StartsWith("HashSet<") ||
               typeName.StartsWith("Queue<") ||
               typeName.StartsWith("Stack<") ||
               typeName == "ArrayList" ||
               typeName == "Hashtable" ||
               typeName.EndsWith("[]");
    }
}
