using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.Transformers.Core;

/// <summary>
/// Validates semantic equivalence of code transformations.
/// </summary>
public class SemanticValidator
{
    /// <summary>
    /// Validate that a transformation preserves semantic correctness.
    /// </summary>
    public async Task<SemanticValidation> ValidateTransformationAsync(
        Document originalDocument,
        Document transformedDocument)
    {
        var validation = new SemanticValidation
        {
            IsEquivalent = true,
            CompilesSuccessfully = true
        };

        var breakingChanges = new List<string>();
        var typeChanges = new List<string>();
        var signatureChanges = new List<string>();
        var compilationErrors = new List<string>();

        // Step 1: Check if transformed code compiles
        var transformedCompilation = await transformedDocument.Project.GetCompilationAsync();
        if (transformedCompilation == null)
        {
            return validation with
            {
                IsEquivalent = false,
                CompilesSuccessfully = false,
                CompilationErrors = ["Failed to get compilation"]
            };
        }

        var errors = transformedCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Any())
        {
            compilationErrors.AddRange(errors.Take(10).Select(e => e.GetMessage()));
            return validation with
            {
                IsEquivalent = false,
                CompilesSuccessfully = false,
                CompilationErrors = compilationErrors
            };
        }

        // Step 2: Compare public API surface
        var originalModel = await originalDocument.GetSemanticModelAsync();
        var transformedModel = await transformedDocument.GetSemanticModelAsync();

        if (originalModel == null || transformedModel == null)
        {
            return validation with
            {
                IsEquivalent = false,
                BreakingChanges = ["Could not get semantic models"]
            };
        }

        var originalRoot = await originalDocument.GetSyntaxRootAsync();
        var transformedRoot = await transformedDocument.GetSyntaxRootAsync();

        if (originalRoot == null || transformedRoot == null)
        {
            return validation with
            {
                IsEquivalent = false,
                BreakingChanges = ["Could not get syntax roots"]
            };
        }

        // Compare public members
        var publicMemberChanges = ComparePublicMembers(
            originalRoot, transformedRoot, originalModel, transformedModel);

        signatureChanges.AddRange(publicMemberChanges.SignatureChanges);
        typeChanges.AddRange(publicMemberChanges.TypeChanges);
        breakingChanges.AddRange(publicMemberChanges.BreakingChanges);

        return new SemanticValidation
        {
            IsEquivalent = !breakingChanges.Any() && !signatureChanges.Any(),
            CompilesSuccessfully = true,
            BreakingChanges = breakingChanges,
            TypeChanges = typeChanges,
            SignatureChanges = signatureChanges,
            CompilationErrors = compilationErrors
        };
    }

    /// <summary>
    /// Quick validation that just checks if code compiles.
    /// </summary>
    public async Task<bool> ValidatesCompilationAsync(string code, Compilation referenceCompilation)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code);
        var compilation = referenceCompilation.AddSyntaxTrees(syntaxTree);

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        return !errors.Any();
    }

    private MemberComparisonResult ComparePublicMembers(
        SyntaxNode originalRoot,
        SyntaxNode transformedRoot,
        SemanticModel originalModel,
        SemanticModel transformedModel)
    {
        var result = new MemberComparisonResult();

        // Get all public type declarations
        var originalTypes = originalRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Modifiers.Any(SyntaxKind.PublicKeyword))
            .ToList();

        var transformedTypes = transformedRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Modifiers.Any(SyntaxKind.PublicKeyword))
            .ToDictionary(t => t.Identifier.Text);

        foreach (var originalType in originalTypes)
        {
            var typeName = originalType.Identifier.Text;

            if (!transformedTypes.TryGetValue(typeName, out var transformedType))
            {
                result.BreakingChanges.Add($"Public type '{typeName}' was removed");
                continue;
            }

            // Compare public methods
            CompareTypeMethods(originalType, transformedType, originalModel, transformedModel, result);

            // Compare public properties
            CompareTypeProperties(originalType, transformedType, originalModel, transformedModel, result);
        }

        return result;
    }

    private void CompareTypeMethods(
        TypeDeclarationSyntax originalType,
        TypeDeclarationSyntax transformedType,
        SemanticModel originalModel,
        SemanticModel transformedModel,
        MemberComparisonResult result)
    {
        var originalMethods = originalType.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword))
            .ToList();

        var transformedMethods = transformedType.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword))
            .ToDictionary(m => GetMethodSignature(m));

        foreach (var original in originalMethods)
        {
            var signature = GetMethodSignature(original);

            if (!transformedMethods.TryGetValue(signature, out var transformed))
            {
                result.BreakingChanges.Add($"Public method '{signature}' was removed or signature changed");
                continue;
            }

            // Check return type
            var originalReturn = originalModel.GetDeclaredSymbol(original)?.ReturnType.ToDisplayString();
            var transformedReturn = transformedModel.GetDeclaredSymbol(transformed)?.ReturnType.ToDisplayString();

            if (originalReturn != transformedReturn)
            {
                result.TypeChanges.Add($"Method '{signature}' return type changed from '{originalReturn}' to '{transformedReturn}'");
            }
        }
    }

    private void CompareTypeProperties(
        TypeDeclarationSyntax originalType,
        TypeDeclarationSyntax transformedType,
        SemanticModel originalModel,
        SemanticModel transformedModel,
        MemberComparisonResult result)
    {
        var originalProps = originalType.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(SyntaxKind.PublicKeyword))
            .ToList();

        var transformedProps = transformedType.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(SyntaxKind.PublicKeyword))
            .ToDictionary(p => p.Identifier.Text);

        foreach (var original in originalProps)
        {
            var name = original.Identifier.Text;

            if (!transformedProps.TryGetValue(name, out var transformed))
            {
                result.BreakingChanges.Add($"Public property '{name}' was removed");
                continue;
            }

            // Check type
            var originalType2 = originalModel.GetDeclaredSymbol(original)?.Type.ToDisplayString();
            var transformedType2 = transformedModel.GetDeclaredSymbol(transformed)?.Type.ToDisplayString();

            if (originalType2 != transformedType2)
            {
                result.TypeChanges.Add($"Property '{name}' type changed from '{originalType2}' to '{transformedType2}'");
            }

            // Check accessors
            var originalHasGetter = original.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false;
            var transformedHasGetter = transformed.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false;

            if (originalHasGetter && !transformedHasGetter)
            {
                result.BreakingChanges.Add($"Property '{name}' getter was removed");
            }

            var originalHasSetter = original.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false;
            var transformedHasSetter = transformed.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false;

            if (originalHasSetter && !transformedHasSetter)
            {
                result.BreakingChanges.Add($"Property '{name}' setter was removed");
            }
        }
    }

    private string GetMethodSignature(MethodDeclarationSyntax method)
    {
        var parameters = string.Join(", ", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?"));
        return $"{method.Identifier.Text}({parameters})";
    }

    private class MemberComparisonResult
    {
        public List<string> BreakingChanges { get; } = [];
        public List<string> TypeChanges { get; } = [];
        public List<string> SignatureChanges { get; } = [];
    }
}
