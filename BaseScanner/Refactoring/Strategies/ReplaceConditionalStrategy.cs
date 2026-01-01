using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using BaseScanner.Refactoring.Models;

namespace BaseScanner.Refactoring.Strategies;

/// <summary>
/// Replaces complex conditional logic with polymorphism.
/// </summary>
public class ReplaceConditionalStrategy : RefactoringStrategyBase
{
    public override string Name => "Replace Conditional with Polymorphism";
    public override string Category => "Refactoring";
    public override string Description => "Replaces switch-on-type patterns with polymorphic class hierarchy";
    public override RefactoringType RefactoringType => RefactoringType.ReplaceConditional;

    public override IReadOnlyList<CodeSmellType> AddressesSmells => new[]
    {
        CodeSmellType.SwitchStatement,
        CodeSmellType.LongMethod
    };

    private const int MinSwitchCases = 3;

    public override async Task<bool> CanApplyAsync(Document document)
    {
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return false;

        return root.DescendantNodes()
            .OfType<SwitchStatementSyntax>()
            .Any(s => IsTypeBasedSwitch(s)) ||
               root.DescendantNodes()
            .OfType<SwitchExpressionSyntax>()
            .Any(s => s.Arms.Count >= MinSwitchCases);
    }

    public override async Task<Solution> ApplyAsync(Solution solution, DocumentId documentId)
    {
        var document = solution.GetDocument(documentId);
        if (document == null)
            return solution;

        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
            return solution;

        // Find the first applicable switch
        var targetSwitch = root.DescendantNodes()
            .OfType<SwitchStatementSyntax>()
            .FirstOrDefault(IsTypeBasedSwitch);

        if (targetSwitch != null)
        {
            return await ReplaceWithPolymorphism(solution, document, targetSwitch, model);
        }

        return solution;
    }

    public override async Task<Solution> ApplyAsync(Solution solution, DocumentId documentId, CodeSmell targetSmell)
    {
        // For switch smells, we target by line number
        var document = solution.GetDocument(documentId);
        if (document == null)
            return solution;

        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
            return solution;

        var targetSwitch = root.DescendantNodes()
            .OfType<SwitchStatementSyntax>()
            .FirstOrDefault(s =>
                s.GetLocation().GetLineSpan().StartLinePosition.Line + 1 >= targetSmell.StartLine &&
                s.GetLocation().GetLineSpan().EndLinePosition.Line + 1 <= targetSmell.EndLine);

        if (targetSwitch != null)
        {
            return await ReplaceWithPolymorphism(solution, document, targetSwitch, model);
        }

        return solution;
    }

    public override async Task<RefactoringEstimate> EstimateImprovementAsync(Document document, CodeSmell? targetSmell = null)
    {
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = "Could not parse document"
            };
        }

        var switches = root.DescendantNodes()
            .OfType<SwitchStatementSyntax>()
            .Where(IsTypeBasedSwitch)
            .ToList();

        if (switches.Count == 0)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = "No type-based switch statements found"
            };
        }

        var totalCases = switches.Sum(s => s.Sections.Count);
        var proposedNames = switches.SelectMany(GetProposedTypeNames).ToList();

        return new RefactoringEstimate
        {
            StrategyType = RefactoringType,
            CanApply = true,
            EstimatedComplexityReduction = totalCases,
            EstimatedCohesionImprovement = switches.Count,
            EstimatedMaintainabilityGain = totalCases * 2,
            EstimatedNewClassCount = totalCases,
            ProposedNames = proposedNames
        };
    }

    public override async Task<RefactoringDetails> GetProposedChangesAsync(Document document, CodeSmell targetSmell)
    {
        var details = new RefactoringDetails();
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return details;

        var switches = root.DescendantNodes()
            .OfType<SwitchStatementSyntax>()
            .Where(IsTypeBasedSwitch)
            .ToList();

        foreach (var sw in switches)
        {
            var proposedNames = GetProposedTypeNames(sw);
            details.ExtractedClasses.AddRange(proposedNames);
            details.ExtractedInterfaces.Add(GetInterfaceName(sw));
        }

        return details;
    }

    #region Switch Analysis

    private bool IsTypeBasedSwitch(SwitchStatementSyntax switchStmt)
    {
        if (switchStmt.Sections.Count < MinSwitchCases)
            return false;

        // Check if switching on a type/enum
        var expression = switchStmt.Expression;

        // Pattern: switch(obj.GetType()) or switch(obj)
        if (expression is InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.Text == "GetType")
            {
                return true;
            }
        }

        // Check if cases are type patterns or constant patterns with similar handling
        var hasTypePatterns = switchStmt.Sections.Any(s =>
            s.Labels.Any(l => l is CasePatternSwitchLabelSyntax));

        var hasConstantPatterns = switchStmt.Sections.Any(s =>
            s.Labels.OfType<CaseSwitchLabelSyntax>()
                .Any(l => l.Value is MemberAccessExpressionSyntax));

        return hasTypePatterns || hasConstantPatterns;
    }

    private List<string> GetProposedTypeNames(SwitchStatementSyntax switchStmt)
    {
        var names = new List<string>();

        foreach (var section in switchStmt.Sections)
        {
            foreach (var label in section.Labels)
            {
                if (label is CasePatternSwitchLabelSyntax patternLabel)
                {
                    if (patternLabel.Pattern is DeclarationPatternSyntax declPattern)
                    {
                        names.Add($"Handler{declPattern.Type}");
                    }
                    else if (patternLabel.Pattern is ConstantPatternSyntax constPattern)
                    {
                        var valueName = GetConstantName(constPattern.Expression);
                        names.Add($"Handler{valueName}");
                    }
                }
                else if (label is CaseSwitchLabelSyntax caseLabel)
                {
                    var valueName = GetConstantName(caseLabel.Value);
                    names.Add($"Handler{valueName}");
                }
            }
        }

        return names.Where(n => !n.EndsWith("Default")).ToList();
    }

    private string GetConstantName(ExpressionSyntax expression)
    {
        return expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            IdentifierNameSyntax i => i.Identifier.Text,
            LiteralExpressionSyntax l => l.Token.ValueText,
            _ => "Unknown"
        };
    }

    private string GetInterfaceName(SwitchStatementSyntax switchStmt)
    {
        // Try to infer interface name from containing method
        var containingMethod = switchStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod != null)
        {
            return $"I{ToPascalCase(containingMethod.Identifier.Text)}Handler";
        }
        return "IHandler";
    }

    #endregion

    #region Replacement Logic

    private async Task<Solution> ReplaceWithPolymorphism(
        Solution solution,
        Document document,
        SwitchStatementSyntax switchStmt,
        SemanticModel model)
    {
        var editor = await DocumentEditor.CreateAsync(document);

        var containingMethod = switchStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var containingClass = switchStmt.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

        if (containingMethod == null || containingClass == null)
            return solution;

        // Create interface
        var interfaceName = GetInterfaceName(switchStmt);
        var methodName = "Handle";
        var returnType = containingMethod.ReturnType;

        var interfaceMethod = SyntaxFactory.MethodDeclaration(returnType, methodName)
            .WithParameterList(SyntaxFactory.ParameterList())
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        var interfaceDecl = SyntaxFactory.InterfaceDeclaration(interfaceName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(interfaceMethod))
            .NormalizeWhitespace();

        // Create implementation classes
        var implementations = new List<ClassDeclarationSyntax>();

        foreach (var section in switchStmt.Sections)
        {
            foreach (var label in section.Labels)
            {
                if (label is DefaultSwitchLabelSyntax)
                    continue;

                var className = GetClassNameFromLabel(label);
                var classBody = CreateImplementationBody(section, returnType);

                var implClass = SyntaxFactory.ClassDeclaration(className)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithBaseList(SyntaxFactory.BaseList(
                        SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                            SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName)))))
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                        SyntaxFactory.MethodDeclaration(returnType, methodName)
                            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                            .WithBody(classBody)))
                    .NormalizeWhitespace();

                implementations.Add(implClass);
            }
        }

        // Create factory or dictionary to map to implementations
        var factoryField = CreateHandlerFactory(switchStmt, interfaceName, implementations);

        // Replace switch with delegation
        var delegationCall = CreateDelegationCall(switchStmt, interfaceName);

        // Add interface and implementations
        editor.InsertBefore(containingClass, interfaceDecl);
        foreach (var impl in implementations)
        {
            editor.InsertBefore(containingClass, impl);
        }

        // Add factory field to class
        editor.InsertMembers(containingClass, 0, new[] { factoryField });

        // Replace switch statement
        editor.ReplaceNode(switchStmt, delegationCall);

        return editor.GetChangedDocument().Project.Solution;
    }

    private string GetClassNameFromLabel(SwitchLabelSyntax label)
    {
        if (label is CasePatternSwitchLabelSyntax patternLabel)
        {
            if (patternLabel.Pattern is DeclarationPatternSyntax declPattern)
            {
                return $"{declPattern.Type}Handler";
            }
            else if (patternLabel.Pattern is ConstantPatternSyntax constPattern)
            {
                return $"{GetConstantName(constPattern.Expression)}Handler";
            }
        }
        else if (label is CaseSwitchLabelSyntax caseLabel)
        {
            return $"{GetConstantName(caseLabel.Value)}Handler";
        }

        return "DefaultHandler";
    }

    private BlockSyntax CreateImplementationBody(SwitchSectionSyntax section, TypeSyntax returnType)
    {
        var statements = new List<StatementSyntax>();

        foreach (var statement in section.Statements)
        {
            if (statement is BreakStatementSyntax)
                continue;

            statements.Add(statement);
        }

        // If no return and method returns something, add default return
        if (!statements.Any(s => s is ReturnStatementSyntax) && returnType.ToString() != "void")
        {
            statements.Add(SyntaxFactory.ReturnStatement(
                SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));
        }

        return SyntaxFactory.Block(statements);
    }

    private FieldDeclarationSyntax CreateHandlerFactory(
        SwitchStatementSyntax switchStmt,
        string interfaceName,
        List<ClassDeclarationSyntax> implementations)
    {
        // Create dictionary initialization
        var initializerExpressions = new List<ExpressionSyntax>();

        var switchExpression = switchStmt.Expression;
        string keyType = "object";

        // Try to infer key type from switch expression
        if (switchStmt.Sections.FirstOrDefault()?.Labels.FirstOrDefault() is CaseSwitchLabelSyntax caseLabel)
        {
            if (caseLabel.Value is MemberAccessExpressionSyntax memberAccess)
            {
                var enumTypeParts = memberAccess.Expression.ToString().Split('.');
                if (enumTypeParts.Length > 0)
                {
                    keyType = memberAccess.Expression.ToString();
                }
            }
        }

        foreach (var impl in implementations)
        {
            var implName = impl.Identifier.Text;
            // Create key-value pair for initialization
            var key = implName.Replace("Handler", "");
            initializerExpressions.Add(
                SyntaxFactory.InitializerExpression(
                    SyntaxKind.ComplexElementInitializerExpression,
                    SyntaxFactory.SeparatedList(new ExpressionSyntax[]
                    {
                        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(key)),
                        SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName(implName))
                            .WithArgumentList(SyntaxFactory.ArgumentList())
                    })));
        }

        var dictionaryType = $"Dictionary<string, {interfaceName}>";

        return SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(dictionaryType))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator("_handlers")
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(dictionaryType))
                                .WithInitializer(SyntaxFactory.InitializerExpression(
                                    SyntaxKind.CollectionInitializerExpression,
                                    SyntaxFactory.SeparatedList(initializerExpressions))))))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));
    }

    private StatementSyntax CreateDelegationCall(SwitchStatementSyntax switchStmt, string interfaceName)
    {
        // Create: _handlers[key].Handle()
        var keyExpression = switchStmt.Expression;

        var indexAccess = SyntaxFactory.ElementAccessExpression(
            SyntaxFactory.IdentifierName("_handlers"),
            SyntaxFactory.BracketedArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                keyExpression,
                                SyntaxFactory.IdentifierName("ToString")))))));

        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                indexAccess,
                SyntaxFactory.IdentifierName("Handle")));

        // Check containing method return type
        var containingMethod = switchStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod?.ReturnType.ToString() == "void")
        {
            return SyntaxFactory.ExpressionStatement(invocation);
        }
        else
        {
            return SyntaxFactory.ReturnStatement(invocation);
        }
    }

    private string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    #endregion
}
