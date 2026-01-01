using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using BaseScanner.Refactoring.Models;

namespace BaseScanner.Refactoring.Strategies;

/// <summary>
/// Extracts interfaces from classes to improve testability and abstraction.
/// </summary>
public class ExtractInterfaceStrategy : RefactoringStrategyBase
{
    public override string Name => "Extract Interface";
    public override string Category => "Refactoring";
    public override string Description => "Extracts interface from public members to improve testability";
    public override RefactoringType RefactoringType => RefactoringType.ExtractInterface;

    public override IReadOnlyList<CodeSmellType> AddressesSmells => new[]
    {
        CodeSmellType.GodClass,
        CodeSmellType.LargeClass
    };

    private const int MinPublicMembers = 3;

    public override async Task<bool> CanApplyAsync(Document document)
    {
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return false;

        return root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Any(c => GetExtractableMembers(c).Count >= MinPublicMembers &&
                      !ImplementsInterface(c));
    }

    public override async Task<Solution> ApplyAsync(Solution solution, DocumentId documentId)
    {
        var document = solution.GetDocument(documentId);
        if (document == null)
            return solution;

        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return solution;

        var targetClass = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => GetExtractableMembers(c).Count >= MinPublicMembers &&
                                  !ImplementsInterface(c));

        if (targetClass == null)
            return solution;

        return await ExtractInterface(solution, document, targetClass);
    }

    public override async Task<Solution> ApplyAsync(Solution solution, DocumentId documentId, CodeSmell targetSmell)
    {
        var document = solution.GetDocument(documentId);
        if (document == null)
            return solution;

        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return solution;

        var targetClass = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == targetSmell.TargetName);

        if (targetClass == null)
            return solution;

        return await ExtractInterface(solution, document, targetClass);
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

        var targetClass = targetSmell != null
            ? root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                  .FirstOrDefault(c => c.Identifier.Text == targetSmell.TargetName)
            : root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                  .FirstOrDefault(c => GetExtractableMembers(c).Count >= MinPublicMembers);

        if (targetClass == null)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = "No suitable class found"
            };
        }

        var extractableMembers = GetExtractableMembers(targetClass);
        if (extractableMembers.Count < MinPublicMembers)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = $"Only {extractableMembers.Count} extractable members (need {MinPublicMembers})"
            };
        }

        var interfaceName = $"I{targetClass.Identifier.Text}";

        return new RefactoringEstimate
        {
            StrategyType = RefactoringType,
            CanApply = true,
            EstimatedComplexityReduction = 0,
            EstimatedCohesionImprovement = 0,
            EstimatedMaintainabilityGain = extractableMembers.Count * 2,
            EstimatedNewClassCount = 0,
            ProposedNames = new List<string> { interfaceName }
        };
    }

    public override async Task<RefactoringDetails> GetProposedChangesAsync(Document document, CodeSmell targetSmell)
    {
        var details = new RefactoringDetails();
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return details;

        var targetClass = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == targetSmell.TargetName);

        if (targetClass == null)
            return details;

        var interfaceName = $"I{targetClass.Identifier.Text}";
        details.ExtractedInterfaces.Add(interfaceName);

        foreach (var member in GetExtractableMembers(targetClass))
        {
            var memberName = member switch
            {
                MethodDeclarationSyntax m => m.Identifier.Text,
                PropertyDeclarationSyntax p => p.Identifier.Text,
                _ => "Unknown"
            };
            details.MovedMembers.Add($"{memberName} -> {interfaceName}");
        }

        return details;
    }

    #region Interface Extraction

    private List<MemberDeclarationSyntax> GetExtractableMembers(ClassDeclarationSyntax classDecl)
    {
        var members = new List<MemberDeclarationSyntax>();

        // Get public methods (excluding special methods)
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            if (method.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                !method.Modifiers.Any(SyntaxKind.StaticKeyword) &&
                !method.Modifiers.Any(SyntaxKind.OverrideKeyword) &&
                !IsSpecialMethod(method))
            {
                members.Add(method);
            }
        }

        // Get public properties
        foreach (var property in classDecl.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (property.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                !property.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                members.Add(property);
            }
        }

        return members;
    }

    private bool IsSpecialMethod(MethodDeclarationSyntax method)
    {
        var name = method.Identifier.Text;
        return name == "ToString" ||
               name == "Equals" ||
               name == "GetHashCode" ||
               name == "Dispose" ||
               name.StartsWith("get_") ||
               name.StartsWith("set_");
    }

    private bool ImplementsInterface(ClassDeclarationSyntax classDecl)
    {
        if (classDecl.BaseList == null)
            return false;

        return classDecl.BaseList.Types.Any(t =>
            t.Type is IdentifierNameSyntax id && id.Identifier.Text.StartsWith("I"));
    }

    private async Task<Solution> ExtractInterface(
        Solution solution,
        Document document,
        ClassDeclarationSyntax targetClass)
    {
        var editor = await DocumentEditor.CreateAsync(document);

        var interfaceName = $"I{targetClass.Identifier.Text}";
        var extractableMembers = GetExtractableMembers(targetClass);

        // Create interface
        var interfaceMembers = new List<MemberDeclarationSyntax>();

        foreach (var member in extractableMembers)
        {
            if (member is MethodDeclarationSyntax method)
            {
                // Create interface method signature
                var interfaceMethod = SyntaxFactory.MethodDeclaration(
                        method.ReturnType,
                        method.Identifier)
                    .WithParameterList(method.ParameterList)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                // Add XML doc if the original has it
                var trivia = method.GetLeadingTrivia()
                    .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                                t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

                if (trivia.Any())
                {
                    interfaceMethod = interfaceMethod.WithLeadingTrivia(trivia);
                }

                interfaceMembers.Add(interfaceMethod);
            }
            else if (member is PropertyDeclarationSyntax property)
            {
                // Create interface property
                var accessors = new List<AccessorDeclarationSyntax>();

                if (property.AccessorList != null)
                {
                    foreach (var accessor in property.AccessorList.Accessors)
                    {
                        if (accessor.Modifiers.Count == 0) // Only include public accessors
                        {
                            accessors.Add(SyntaxFactory.AccessorDeclaration(accessor.Kind())
                                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                        }
                    }
                }

                var interfaceProperty = SyntaxFactory.PropertyDeclaration(
                        property.Type,
                        property.Identifier)
                    .WithAccessorList(SyntaxFactory.AccessorList(
                        SyntaxFactory.List(accessors)));

                interfaceMembers.Add(interfaceProperty);
            }
        }

        var interfaceDecl = SyntaxFactory.InterfaceDeclaration(interfaceName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.List(interfaceMembers))
            .WithLeadingTrivia(SyntaxFactory.Comment($"/// <summary>\n/// Interface for {targetClass.Identifier.Text}.\n/// </summary>\n"))
            .NormalizeWhitespace();

        // Update class to implement interface
        var baseList = targetClass.BaseList ?? SyntaxFactory.BaseList();
        var newBaseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName));

        BaseListSyntax newBaseList;
        if (baseList.Types.Count == 0)
        {
            newBaseList = SyntaxFactory.BaseList(
                SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(newBaseType));
        }
        else
        {
            newBaseList = baseList.AddTypes(newBaseType);
        }

        var updatedClass = targetClass.WithBaseList(newBaseList);

        // Insert interface before class
        editor.InsertBefore(targetClass, interfaceDecl);
        editor.ReplaceNode(targetClass, updatedClass);

        return editor.GetChangedDocument().Project.Solution;
    }

    #endregion
}
