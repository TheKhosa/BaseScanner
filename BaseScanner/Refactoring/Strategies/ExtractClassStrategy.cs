using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using BaseScanner.Refactoring.Models;
using BaseScanner.Refactoring.Analysis;

namespace BaseScanner.Refactoring.Strategies;

/// <summary>
/// Extracts cohesive groups of methods and fields into separate classes.
/// </summary>
public class ExtractClassStrategy : RefactoringStrategyBase
{
    private readonly CohesionAnalyzer _cohesionAnalyzer;

    public override string Name => "Extract Class";
    public override string Category => "Refactoring";
    public override string Description => "Extracts cohesive method clusters into separate classes";
    public override RefactoringType RefactoringType => RefactoringType.ExtractClass;

    public override IReadOnlyList<CodeSmellType> AddressesSmells => new[]
    {
        CodeSmellType.GodClass,
        CodeSmellType.LargeClass
    };

    public ExtractClassStrategy(CohesionAnalyzer? cohesionAnalyzer = null)
    {
        _cohesionAnalyzer = cohesionAnalyzer ?? new CohesionAnalyzer();
    }

    public override async Task<bool> CanApplyAsync(Document document)
    {
        var clusters = await _cohesionAnalyzer.FindCohesiveClustersAsync(document);
        return clusters.Count > 0;
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

        var mainClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (mainClass == null)
            return solution;

        var clusters = _cohesionAnalyzer.FindCohesiveClusters(mainClass, model);
        if (clusters.Count == 0)
            return solution;

        // Extract the highest cohesion cluster
        var bestCluster = clusters.OrderByDescending(c => c.CohesionScore).First();
        return await ExtractClusterToClass(solution, document, mainClass, bestCluster, model);
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

        var clusters = _cohesionAnalyzer.FindCohesiveClusters(targetClass, model);
        if (clusters.Count == 0)
            return solution;

        var bestCluster = clusters.OrderByDescending(c => c.CohesionScore).First();
        return await ExtractClusterToClass(solution, document, targetClass, bestCluster, model);
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
            : root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

        if (targetClass == null)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = "No class found"
            };
        }

        var clusters = _cohesionAnalyzer.FindCohesiveClusters(targetClass, model);
        if (clusters.Count == 0)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = "No cohesive clusters found for extraction"
            };
        }

        var lcom4Before = _cohesionAnalyzer.CalculateLCOM4(targetClass, model);
        var estimatedLcom4After = Math.Max(1, lcom4Before - clusters.Count);

        return new RefactoringEstimate
        {
            StrategyType = RefactoringType,
            CanApply = true,
            EstimatedComplexityReduction = clusters.Sum(c => c.TotalComplexity) / 2,
            EstimatedCohesionImprovement = lcom4Before - estimatedLcom4After,
            EstimatedMaintainabilityGain = clusters.Count * 5,
            EstimatedNewClassCount = 1,
            ProposedNames = clusters.Select(c => c.SuggestedClassName).ToList()
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

        var clusters = _cohesionAnalyzer.FindCohesiveClusters(targetClass, model);

        foreach (var cluster in clusters)
        {
            details.ExtractedClasses.Add(cluster.SuggestedClassName);
            details.MovedMembers.AddRange(cluster.MethodNames.Select(m => $"{m} -> {cluster.SuggestedClassName}"));
        }

        return details;
    }

    #region Extraction Logic

    private async Task<Solution> ExtractClusterToClass(
        Solution solution,
        Document document,
        ClassDeclarationSyntax originalClass,
        CohesiveCluster cluster,
        SemanticModel model)
    {
        var editor = await DocumentEditor.CreateAsync(document);

        // Create the new class
        var newClass = CreateExtractedClass(originalClass, cluster, model);

        // Create a field to hold the extracted class instance
        var fieldName = "_" + char.ToLowerInvariant(cluster.SuggestedClassName[0]) + cluster.SuggestedClassName.Substring(1);
        var field = CreateDelegateField(cluster.SuggestedClassName, fieldName);

        // Create delegation methods in original class
        var delegationMethods = CreateDelegationMethods(cluster.MethodNames, fieldName, originalClass, model);

        // Modify the original class
        var modifiedClass = originalClass;

        // Remove the methods being extracted
        var methodsToRemove = originalClass.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => cluster.MethodNames.Contains(m.Identifier.Text))
            .ToList();

        foreach (var method in methodsToRemove)
        {
            modifiedClass = modifiedClass.RemoveNode(method, SyntaxRemoveOptions.KeepNoTrivia)!;
        }

        // Remove the fields being extracted (but keep in original for delegation)
        // Actually, keep fields in original and pass via constructor

        // Add the delegate field
        modifiedClass = modifiedClass.AddMembers(field);

        // Add delegation methods
        modifiedClass = modifiedClass.AddMembers(delegationMethods.ToArray());

        // Update constructor to initialize the delegate
        modifiedClass = UpdateConstructor(modifiedClass, cluster.SuggestedClassName, fieldName, cluster.SharedFields);

        // Replace original class and add new class
        editor.ReplaceNode(originalClass, modifiedClass);
        editor.InsertBefore(originalClass, newClass);

        return editor.GetChangedDocument().Project.Solution;
    }

    private ClassDeclarationSyntax CreateExtractedClass(
        ClassDeclarationSyntax originalClass,
        CohesiveCluster cluster,
        SemanticModel model)
    {
        var members = new List<MemberDeclarationSyntax>();

        // Add fields
        foreach (var fieldName in cluster.SharedFields)
        {
            var originalField = originalClass.Members
                .OfType<FieldDeclarationSyntax>()
                .SelectMany(f => f.Declaration.Variables)
                .FirstOrDefault(v => v.Identifier.Text == fieldName);

            if (originalField != null)
            {
                var fieldDecl = originalField.Ancestors().OfType<FieldDeclarationSyntax>().First();
                members.Add(fieldDecl
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword))));
            }
        }

        // Add methods
        foreach (var methodName in cluster.MethodNames)
        {
            var originalMethod = originalClass.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName);

            if (originalMethod != null)
            {
                // Make the method public if it was public, otherwise internal
                var modifiers = originalMethod.Modifiers.Any(SyntaxKind.PublicKeyword)
                    ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                    : SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword));

                members.Add(originalMethod.WithModifiers(modifiers));
            }
        }

        // Create constructor with field initialization
        var constructor = CreateConstructor(cluster);
        members.Insert(0, constructor);

        return SyntaxFactory.ClassDeclaration(cluster.SuggestedClassName)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.List(members))
            .NormalizeWhitespace();
    }

    private ConstructorDeclarationSyntax CreateConstructor(CohesiveCluster cluster)
    {
        // Create constructor with parameters for dependencies
        var parameters = SyntaxFactory.ParameterList();
        var statements = new List<StatementSyntax>();

        // For now, create empty constructor
        // In a real implementation, would analyze dependencies

        return SyntaxFactory.ConstructorDeclaration(cluster.SuggestedClassName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(parameters)
            .WithBody(SyntaxFactory.Block(statements));
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

    private List<MethodDeclarationSyntax> CreateDelegationMethods(
        List<string> methodNames,
        string fieldName,
        ClassDeclarationSyntax originalClass,
        SemanticModel model)
    {
        var delegations = new List<MethodDeclarationSyntax>();

        foreach (var methodName in methodNames)
        {
            var originalMethod = originalClass.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName);

            if (originalMethod == null)
                continue;

            // Only create delegation for public methods
            if (!originalMethod.Modifiers.Any(SyntaxKind.PublicKeyword))
                continue;

            // Create delegation call
            var arguments = SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(
                    originalMethod.ParameterList.Parameters.Select(p =>
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier)))));

            var call = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(fieldName),
                    SyntaxFactory.IdentifierName(methodName)),
                arguments);

            StatementSyntax body;
            if (originalMethod.ReturnType.ToString() == "void")
            {
                body = SyntaxFactory.ExpressionStatement(call);
            }
            else
            {
                body = SyntaxFactory.ReturnStatement(call);
            }

            var delegation = SyntaxFactory.MethodDeclaration(
                    originalMethod.ReturnType,
                    originalMethod.Identifier)
                .WithModifiers(originalMethod.Modifiers)
                .WithParameterList(originalMethod.ParameterList)
                .WithBody(SyntaxFactory.Block(body));

            delegations.Add(delegation);
        }

        return delegations;
    }

    private ClassDeclarationSyntax UpdateConstructor(
        ClassDeclarationSyntax classDecl,
        string extractedClassName,
        string fieldName,
        List<string> sharedFields)
    {
        var existingConstructor = classDecl.Members
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault();

        // Create initialization statement
        var initialization = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(fieldName),
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName(extractedClassName))
                    .WithArgumentList(SyntaxFactory.ArgumentList())));

        if (existingConstructor != null)
        {
            // Add initialization to existing constructor
            var newBody = existingConstructor.Body!.AddStatements(initialization);
            var newConstructor = existingConstructor.WithBody(newBody);
            return classDecl.ReplaceNode(existingConstructor, newConstructor);
        }
        else
        {
            // Create new constructor
            var constructor = SyntaxFactory.ConstructorDeclaration(classDecl.Identifier)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithBody(SyntaxFactory.Block(initialization));

            return classDecl.AddMembers(constructor);
        }
    }

    #endregion
}
