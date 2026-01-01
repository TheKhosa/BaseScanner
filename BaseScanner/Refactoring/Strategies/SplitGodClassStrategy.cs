using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using BaseScanner.Refactoring.Models;
using BaseScanner.Refactoring.Analysis;

namespace BaseScanner.Refactoring.Strategies;

/// <summary>
/// Splits god classes into multiple focused classes based on responsibility boundaries.
/// </summary>
public class SplitGodClassStrategy : RefactoringStrategyBase
{
    private readonly CohesionAnalyzer _cohesionAnalyzer;

    public override string Name => "Split God Class";
    public override string Category => "Refactoring";
    public override string Description => "Splits god classes into multiple focused classes by responsibility";
    public override RefactoringType RefactoringType => RefactoringType.SplitGodClass;

    public override IReadOnlyList<CodeSmellType> AddressesSmells => new[]
    {
        CodeSmellType.GodClass
    };

    private const int MinMethodsForGodClass = 15;
    private const int MinResponsibilities = 3;

    public SplitGodClassStrategy(CohesionAnalyzer? cohesionAnalyzer = null)
    {
        _cohesionAnalyzer = cohesionAnalyzer ?? new CohesionAnalyzer();
    }

    public override async Task<bool> CanApplyAsync(Document document)
    {
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
            return false;

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var methodCount = classDecl.Members.OfType<MethodDeclarationSyntax>().Count();
            if (methodCount >= MinMethodsForGodClass)
            {
                var responsibilities = _cohesionAnalyzer.IdentifyResponsibilities(classDecl, model);
                if (responsibilities.Count >= MinResponsibilities)
                {
                    return true;
                }
            }
        }

        return false;
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

        // Find the first god class
        var godClass = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => IsGodClass(c, model));

        if (godClass == null)
            return solution;

        return await SplitClass(solution, document, godClass, model);
    }

    public override async Task<Solution> ApplyAsync(Solution solution, DocumentId documentId, CodeSmell targetSmell)
    {
        var document = solution.GetDocument(documentId);
        if (document == null)
            return solution;

        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
            return solution;

        var targetClass = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == targetSmell.TargetName);

        if (targetClass == null)
            return solution;

        return await SplitClass(solution, document, targetClass, model);
    }

    public override async Task<RefactoringEstimate> EstimateImprovementAsync(Document document, CodeSmell? targetSmell = null)
    {
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = "Could not parse document"
            };
        }

        var targetClass = targetSmell != null
            ? root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                  .FirstOrDefault(c => c.Identifier.Text == targetSmell.TargetName)
            : root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                  .FirstOrDefault(c => IsGodClass(c, model));

        if (targetClass == null)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = "No god class found"
            };
        }

        var responsibilities = _cohesionAnalyzer.IdentifyResponsibilities(targetClass, model);
        if (responsibilities.Count < MinResponsibilities)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = $"Only {responsibilities.Count} responsibilities found (need {MinResponsibilities})"
            };
        }

        var lcom4Before = _cohesionAnalyzer.CalculateLCOM4(targetClass, model);

        return new RefactoringEstimate
        {
            StrategyType = RefactoringType,
            CanApply = true,
            EstimatedComplexityReduction = responsibilities.Sum(r => r.Methods.Count) / 2,
            EstimatedCohesionImprovement = lcom4Before - 1, // Each new class should have LCOM4 ~1
            EstimatedMaintainabilityGain = responsibilities.Count * 10,
            EstimatedNewClassCount = responsibilities.Count,
            ProposedNames = responsibilities.Select(r => $"{targetClass.Identifier.Text}{r.ResponsibilityName}").ToList()
        };
    }

    public override async Task<RefactoringDetails> GetProposedChangesAsync(Document document, CodeSmell targetSmell)
    {
        var details = new RefactoringDetails();
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
            return details;

        var targetClass = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == targetSmell.TargetName);

        if (targetClass == null)
            return details;

        var responsibilities = _cohesionAnalyzer.IdentifyResponsibilities(targetClass, model);

        foreach (var resp in responsibilities)
        {
            var newClassName = $"{targetClass.Identifier.Text}{resp.ResponsibilityName}";
            details.ExtractedClasses.Add(newClassName);

            foreach (var method in resp.Methods)
            {
                details.MovedMembers.Add($"{method} -> {newClassName}");
            }
        }

        return details;
    }

    #region Split Logic

    private bool IsGodClass(ClassDeclarationSyntax classDecl, SemanticModel model)
    {
        var methodCount = classDecl.Members.OfType<MethodDeclarationSyntax>().Count();
        if (methodCount < MinMethodsForGodClass)
            return false;

        var responsibilities = _cohesionAnalyzer.IdentifyResponsibilities(classDecl, model);
        return responsibilities.Count >= MinResponsibilities;
    }

    private async Task<Solution> SplitClass(
        Solution solution,
        Document document,
        ClassDeclarationSyntax godClass,
        SemanticModel model)
    {
        var responsibilities = _cohesionAnalyzer.IdentifyResponsibilities(godClass, model);
        if (responsibilities.Count < MinResponsibilities)
            return solution;

        var editor = await DocumentEditor.CreateAsync(document);
        var originalClassName = godClass.Identifier.Text;

        // Create new classes for each responsibility
        var newClasses = new List<ClassDeclarationSyntax>();
        var delegateFields = new List<FieldDeclarationSyntax>();
        var delegationMethods = new List<MethodDeclarationSyntax>();

        foreach (var responsibility in responsibilities)
        {
            var newClassName = $"{originalClassName}{responsibility.ResponsibilityName.Replace(" ", "")}";
            var newClass = CreateResponsibilityClass(godClass, responsibility, newClassName, model);
            newClasses.Add(newClass);

            // Create delegate field and methods
            var fieldName = "_" + char.ToLowerInvariant(newClassName[0]) + newClassName.Substring(1);
            delegateFields.Add(CreateDelegateField(newClassName, fieldName));

            foreach (var methodName in responsibility.Methods)
            {
                var originalMethod = godClass.Members
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == methodName);

                if (originalMethod != null && originalMethod.Modifiers.Any(SyntaxKind.PublicKeyword))
                {
                    delegationMethods.Add(CreateDelegationMethod(originalMethod, fieldName));
                }
            }
        }

        // Create facade class with delegation
        var facadeClass = CreateFacadeClass(godClass, responsibilities, delegateFields, delegationMethods);

        // Replace the original class with facade
        editor.ReplaceNode(godClass, facadeClass);

        // Insert new responsibility classes before the facade
        foreach (var newClass in newClasses)
        {
            editor.InsertBefore(godClass, newClass);
        }

        return editor.GetChangedDocument().Project.Solution;
    }

    private ClassDeclarationSyntax CreateResponsibilityClass(
        ClassDeclarationSyntax originalClass,
        ResponsibilityBoundary responsibility,
        string className,
        SemanticModel model)
    {
        var members = new List<MemberDeclarationSyntax>();

        // Add fields
        foreach (var fieldName in responsibility.Fields)
        {
            var originalField = originalClass.Members
                .OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));

            if (originalField != null)
            {
                members.Add(originalField
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword))));
            }
        }

        // Add properties
        foreach (var propName in responsibility.Properties)
        {
            var originalProp = originalClass.Members
                .OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault(p => p.Identifier.Text == propName);

            if (originalProp != null)
            {
                members.Add(originalProp);
            }
        }

        // Add methods
        foreach (var methodName in responsibility.Methods)
        {
            var originalMethod = originalClass.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName);

            if (originalMethod != null)
            {
                var modifiers = originalMethod.Modifiers.Any(SyntaxKind.PublicKeyword)
                    ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                    : SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword));

                members.Add(originalMethod.WithModifiers(modifiers));
            }
        }

        // Add constructor
        var constructor = SyntaxFactory.ConstructorDeclaration(className)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithBody(SyntaxFactory.Block());

        members.Insert(0, constructor);

        return SyntaxFactory.ClassDeclaration(className)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(SyntaxFactory.Comment($"/// <summary>\n/// {responsibility.ResponsibilityName} functionality.\n/// </summary>\n"))
            .NormalizeWhitespace();
    }

    private ClassDeclarationSyntax CreateFacadeClass(
        ClassDeclarationSyntax originalClass,
        List<ResponsibilityBoundary> responsibilities,
        List<FieldDeclarationSyntax> delegateFields,
        List<MethodDeclarationSyntax> delegationMethods)
    {
        var members = new List<MemberDeclarationSyntax>();

        // Add delegate fields
        members.AddRange(delegateFields);

        // Add constructor that initializes delegates
        var initStatements = new List<StatementSyntax>();
        foreach (var field in delegateFields)
        {
            var fieldName = field.Declaration.Variables.First().Identifier.Text;
            var typeName = field.Declaration.Type.ToString();

            initStatements.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(fieldName),
                    SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName(typeName))
                        .WithArgumentList(SyntaxFactory.ArgumentList()))));
        }

        var constructor = SyntaxFactory.ConstructorDeclaration(originalClass.Identifier)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithBody(SyntaxFactory.Block(initStatements));

        members.Add(constructor);

        // Add delegation methods
        members.AddRange(delegationMethods);

        // Keep members that weren't moved to responsibility classes
        var movedMethods = responsibilities.SelectMany(r => r.Methods).ToHashSet();
        var movedFields = responsibilities.SelectMany(r => r.Fields).ToHashSet();
        var movedProperties = responsibilities.SelectMany(r => r.Properties).ToHashSet();

        foreach (var member in originalClass.Members)
        {
            var shouldKeep = member switch
            {
                MethodDeclarationSyntax m => !movedMethods.Contains(m.Identifier.Text),
                FieldDeclarationSyntax f => !f.Declaration.Variables.Any(v => movedFields.Contains(v.Identifier.Text)),
                PropertyDeclarationSyntax p => !movedProperties.Contains(p.Identifier.Text),
                ConstructorDeclarationSyntax => false, // We create our own
                _ => true
            };

            if (shouldKeep)
            {
                members.Add(member);
            }
        }

        return originalClass
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(SyntaxFactory.Comment($"/// <summary>\n/// Facade for {originalClass.Identifier.Text} functionality.\n/// </summary>\n"))
            .NormalizeWhitespace();
    }

    private FieldDeclarationSyntax CreateDelegateField(string typeName, string fieldName)
    {
        return SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(typeName))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(fieldName))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));
    }

    private MethodDeclarationSyntax CreateDelegationMethod(MethodDeclarationSyntax original, string fieldName)
    {
        var arguments = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(
                original.ParameterList.Parameters.Select(p =>
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier)))));

        var call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(fieldName),
                SyntaxFactory.IdentifierName(original.Identifier)),
            arguments);

        StatementSyntax body = original.ReturnType.ToString() == "void"
            ? SyntaxFactory.ExpressionStatement(call)
            : SyntaxFactory.ReturnStatement(call);

        return SyntaxFactory.MethodDeclaration(original.ReturnType, original.Identifier)
            .WithModifiers(original.Modifiers)
            .WithParameterList(original.ParameterList)
            .WithBody(SyntaxFactory.Block(body));
    }

    #endregion
}
