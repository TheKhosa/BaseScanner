using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using BaseScanner.Refactoring.Models;

namespace BaseScanner.Refactoring.Strategies;

/// <summary>
/// Extracts cohesive code blocks into separate methods.
/// </summary>
public class ExtractMethodStrategy : RefactoringStrategyBase
{
    public override string Name => "Extract Method";
    public override string Category => "Refactoring";
    public override string Description => "Extracts cohesive code blocks into smaller, reusable methods";
    public override RefactoringType RefactoringType => RefactoringType.ExtractMethod;

    public override IReadOnlyList<CodeSmellType> AddressesSmells => new[]
    {
        CodeSmellType.LongMethod,
        CodeSmellType.DeepNesting
    };

    private const int MinBlockSize = 5; // Minimum lines to extract
    private const int MaxMethodLines = 30; // Target max method size

    public override async Task<bool> CanApplyAsync(Document document)
    {
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return false;

        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Body != null && m.Body.GetText().Lines.Count > MaxMethodLines);
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

        var editor = await DocumentEditor.CreateAsync(document);
        var methodsToProcess = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body != null && m.Body.GetText().Lines.Count > MaxMethodLines)
            .ToList();

        foreach (var method in methodsToProcess)
        {
            var extractedMethods = ExtractMethods(method, model);
            if (extractedMethods.Count > 0)
            {
                // Add extracted methods to the class
                var containingClass = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                if (containingClass != null)
                {
                    foreach (var extracted in extractedMethods)
                    {
                        editor.InsertAfter(method, extracted.NewMethod);
                    }

                    // Replace original method with simplified version
                    var simplifiedMethod = ReplaceWithMethodCalls(method, extractedMethods);
                    editor.ReplaceNode(method, simplifiedMethod);
                }
            }
        }

        return editor.GetChangedDocument().Project.Solution;
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

        var targetMethod = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == targetSmell.TargetName ||
                                  m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == targetSmell.StartLine);

        if (targetMethod == null)
            return solution;

        var editor = await DocumentEditor.CreateAsync(document);
        var extractedMethods = ExtractMethods(targetMethod, model);

        if (extractedMethods.Count > 0)
        {
            foreach (var extracted in extractedMethods)
            {
                editor.InsertAfter(targetMethod, extracted.NewMethod);
            }

            var simplifiedMethod = ReplaceWithMethodCalls(targetMethod, extractedMethods);
            editor.ReplaceNode(targetMethod, simplifiedMethod);
        }

        return editor.GetChangedDocument().Project.Solution;
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

        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => targetSmell == null || m.Identifier.Text == targetSmell.TargetName)
            .Where(m => m.Body != null && m.Body.GetText().Lines.Count > MaxMethodLines)
            .ToList();

        if (methods.Count == 0)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = "No long methods found"
            };
        }

        var totalExtractable = methods.Sum(m => CountExtractableBlocks(m));
        var proposedNames = methods.SelectMany(m => GetProposedMethodNames(m, model)).ToList();

        return new RefactoringEstimate
        {
            StrategyType = RefactoringType,
            CanApply = true,
            EstimatedComplexityReduction = totalExtractable * 2,
            EstimatedMaintainabilityGain = totalExtractable * 3,
            EstimatedNewMethodCount = totalExtractable,
            ProposedNames = proposedNames
        };
    }

    public override async Task<RefactoringDetails> GetProposedChangesAsync(Document document, CodeSmell targetSmell)
    {
        var details = new RefactoringDetails();
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
            return details;

        var targetMethod = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == targetSmell.TargetName);

        if (targetMethod != null)
        {
            details.ExtractedMethods.AddRange(GetProposedMethodNames(targetMethod, model));
        }

        return details;
    }

    #region Extraction Logic

    private List<ExtractedMethod> ExtractMethods(MethodDeclarationSyntax method, SemanticModel model)
    {
        var extractions = new List<ExtractedMethod>();

        if (method.Body == null)
            return extractions;

        var blocks = FindExtractableBlocks(method.Body, model);

        foreach (var block in blocks)
        {
            var extraction = CreateExtraction(block, method, model);
            if (extraction != null)
            {
                extractions.Add(extraction);
            }
        }

        return extractions;
    }

    private List<ExtractableBlock> FindExtractableBlocks(BlockSyntax body, SemanticModel model)
    {
        var blocks = new List<ExtractableBlock>();

        // Strategy 1: Find consecutive statements that work with same variables
        var currentBlock = new List<StatementSyntax>();
        var currentVariables = new HashSet<string>();

        foreach (var statement in body.Statements)
        {
            var statementVars = GetReferencedVariables(statement, model);

            // Check if this statement shares variables with current block
            if (currentBlock.Count > 0 && statementVars.Intersect(currentVariables).Any())
            {
                currentBlock.Add(statement);
                currentVariables.UnionWith(statementVars);
            }
            else
            {
                // Save current block if large enough
                if (currentBlock.Count >= MinBlockSize)
                {
                    blocks.Add(new ExtractableBlock
                    {
                        Statements = currentBlock.ToList(),
                        Variables = currentVariables.ToHashSet()
                    });
                }

                // Start new block
                currentBlock = new List<StatementSyntax> { statement };
                currentVariables = statementVars;
            }
        }

        // Don't forget the last block
        if (currentBlock.Count >= MinBlockSize)
        {
            blocks.Add(new ExtractableBlock
            {
                Statements = currentBlock.ToList(),
                Variables = currentVariables.ToHashSet()
            });
        }

        // Strategy 2: Find if-blocks that are self-contained
        foreach (var ifStmt in body.Statements.OfType<IfStatementSyntax>())
        {
            var ifLines = ifStmt.GetText().Lines.Count;
            if (ifLines >= MinBlockSize)
            {
                blocks.Add(new ExtractableBlock
                {
                    Statements = new List<StatementSyntax> { ifStmt },
                    Variables = GetReferencedVariables(ifStmt, model),
                    IsConditionalBlock = true
                });
            }
        }

        // Strategy 3: Find loop bodies that are self-contained
        foreach (var loop in body.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            var loopLines = loop.GetText().Lines.Count;
            if (loopLines >= MinBlockSize && loop.Statement is BlockSyntax loopBody)
            {
                blocks.Add(new ExtractableBlock
                {
                    Statements = loopBody.Statements.ToList(),
                    Variables = GetReferencedVariables(loop, model),
                    IsLoopBody = true,
                    LoopVariable = loop.Identifier.Text
                });
            }
        }

        return blocks;
    }

    private HashSet<string> GetReferencedVariables(SyntaxNode node, SemanticModel model)
    {
        var variables = new HashSet<string>();

        foreach (var identifier in node.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(identifier).Symbol;
            if (symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol)
            {
                variables.Add(identifier.Identifier.Text);
            }
        }

        return variables;
    }

    private ExtractedMethod? CreateExtraction(ExtractableBlock block, MethodDeclarationSyntax originalMethod, SemanticModel model)
    {
        if (block.Statements.Count == 0)
            return null;

        // Analyze data flow
        var (inputParams, outputParams, localDeclarations) = AnalyzeDataFlow(block, originalMethod, model);

        // Generate method name
        var methodName = GenerateMethodName(block, originalMethod);

        // Determine return type
        var (returnType, returnVariable) = DetermineReturnType(outputParams, localDeclarations);

        // Build parameter list
        var parameters = BuildParameterList(inputParams, model);

        // Build method body
        var methodBody = BuildMethodBody(block, returnVariable);

        // Create the new method
        var newMethod = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.ParseTypeName(returnType),
                SyntaxFactory.Identifier(methodName))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithParameterList(parameters)
            .WithBody(methodBody)
            .NormalizeWhitespace();

        // Create the call expression
        var callExpression = BuildMethodCall(methodName, inputParams, returnVariable);

        return new ExtractedMethod
        {
            NewMethod = newMethod,
            CallExpression = callExpression,
            OriginalStatements = block.Statements,
            StartIndex = GetStatementIndex(originalMethod.Body!, block.Statements.First()),
            EndIndex = GetStatementIndex(originalMethod.Body!, block.Statements.Last())
        };
    }

    private (List<string> inputs, List<string> outputs, List<string> locals) AnalyzeDataFlow(
        ExtractableBlock block,
        MethodDeclarationSyntax method,
        SemanticModel model)
    {
        var inputs = new List<string>();
        var outputs = new List<string>();
        var locals = new List<string>();

        var blockVars = block.Variables;

        // Find variables declared before the block that are used in it (inputs)
        // Find variables declared/modified in the block that are used after it (outputs)

        if (method.Body == null)
            return (inputs, outputs, locals);

        var blockStartIndex = GetStatementIndex(method.Body, block.Statements.First());
        var blockEndIndex = GetStatementIndex(method.Body, block.Statements.Last());

        // Check statements before the block
        for (var i = 0; i < blockStartIndex; i++)
        {
            var stmt = method.Body.Statements[i];
            if (stmt is LocalDeclarationStatementSyntax decl)
            {
                foreach (var variable in decl.Declaration.Variables)
                {
                    var name = variable.Identifier.Text;
                    if (blockVars.Contains(name))
                    {
                        inputs.Add(name);
                    }
                }
            }
        }

        // Check for variables declared in the block
        foreach (var stmt in block.Statements)
        {
            if (stmt is LocalDeclarationStatementSyntax decl)
            {
                foreach (var variable in decl.Declaration.Variables)
                {
                    locals.Add(variable.Identifier.Text);
                }
            }
        }

        // Check statements after the block for output usage
        for (var i = blockEndIndex + 1; i < method.Body.Statements.Count; i++)
        {
            var stmt = method.Body.Statements[i];
            var usedVars = GetReferencedVariables(stmt, model);

            foreach (var localVar in locals)
            {
                if (usedVars.Contains(localVar))
                {
                    outputs.Add(localVar);
                }
            }
        }

        // Also add method parameters that are used
        foreach (var param in method.ParameterList.Parameters)
        {
            if (blockVars.Contains(param.Identifier.Text))
            {
                inputs.Add(param.Identifier.Text);
            }
        }

        return (inputs.Distinct().ToList(), outputs.Distinct().ToList(), locals);
    }

    private int GetStatementIndex(BlockSyntax body, StatementSyntax statement)
    {
        for (var i = 0; i < body.Statements.Count; i++)
        {
            if (body.Statements[i] == statement)
                return i;
        }
        return -1;
    }

    private string GenerateMethodName(ExtractableBlock block, MethodDeclarationSyntax originalMethod)
    {
        var baseName = originalMethod.Identifier.Text;

        // Try to infer purpose from content
        var firstStatement = block.Statements.First();

        if (firstStatement is IfStatementSyntax)
        {
            return $"Check{baseName}Condition";
        }
        else if (firstStatement is LocalDeclarationStatementSyntax decl)
        {
            var varName = decl.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
            if (!string.IsNullOrEmpty(varName))
            {
                return $"Calculate{ToPascalCase(varName)}";
            }
        }

        if (block.IsLoopBody)
        {
            return $"Process{ToPascalCase(block.LoopVariable ?? "Item")}";
        }

        return $"{baseName}Part{block.GetHashCode() % 100}";
    }

    private string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // Handle camelCase, snake_case
        var words = System.Text.RegularExpressions.Regex.Split(name, @"(?<!^)(?=[A-Z])|_")
            .Where(w => !string.IsNullOrEmpty(w))
            .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant());

        return string.Join("", words);
    }

    private (string returnType, string? returnVariable) DetermineReturnType(List<string> outputs, List<string> locals)
    {
        if (outputs.Count == 0)
        {
            return ("void", null);
        }
        else if (outputs.Count == 1)
        {
            // For simplicity, use var/object - in real impl, would infer type
            return ("var", outputs[0]);
        }
        else
        {
            // Multiple outputs - would need tuple or out parameters
            return ("void", null);
        }
    }

    private ParameterListSyntax BuildParameterList(List<string> inputs, SemanticModel model)
    {
        var parameters = inputs.Select(name =>
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(name))
                .WithType(SyntaxFactory.ParseTypeName("var")));

        return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters));
    }

    private BlockSyntax BuildMethodBody(ExtractableBlock block, string? returnVariable)
    {
        var statements = new List<StatementSyntax>(block.Statements);

        if (returnVariable != null)
        {
            statements.Add(SyntaxFactory.ReturnStatement(
                SyntaxFactory.IdentifierName(returnVariable)));
        }

        return SyntaxFactory.Block(statements);
    }

    private StatementSyntax BuildMethodCall(string methodName, List<string> arguments, string? returnVariable)
    {
        var args = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(
                arguments.Select(a => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(a)))));

        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(methodName), args);

        if (returnVariable != null)
        {
            return SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(returnVariable)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(invocation)))));
        }
        else
        {
            return SyntaxFactory.ExpressionStatement(invocation);
        }
    }

    private MethodDeclarationSyntax ReplaceWithMethodCalls(MethodDeclarationSyntax method, List<ExtractedMethod> extractions)
    {
        if (method.Body == null)
            return method;

        var newStatements = new List<StatementSyntax>(method.Body.Statements);

        // Process extractions in reverse order to maintain indices
        foreach (var extraction in extractions.OrderByDescending(e => e.StartIndex))
        {
            // Remove original statements
            for (var i = extraction.EndIndex; i >= extraction.StartIndex; i--)
            {
                if (i < newStatements.Count)
                {
                    newStatements.RemoveAt(i);
                }
            }

            // Insert method call
            if (extraction.StartIndex <= newStatements.Count)
            {
                newStatements.Insert(extraction.StartIndex, extraction.CallExpression);
            }
        }

        var newBody = method.Body.WithStatements(SyntaxFactory.List(newStatements));
        return method.WithBody(newBody);
    }

    private int CountExtractableBlocks(MethodDeclarationSyntax method)
    {
        if (method.Body == null)
            return 0;

        var lines = method.Body.GetText().Lines.Count;
        return Math.Max(0, (lines - MaxMethodLines) / MinBlockSize);
    }

    private List<string> GetProposedMethodNames(MethodDeclarationSyntax method, SemanticModel model)
    {
        var names = new List<string>();

        if (method.Body == null)
            return names;

        var blocks = FindExtractableBlocks(method.Body, model);
        foreach (var block in blocks)
        {
            names.Add(GenerateMethodName(block, method));
        }

        return names;
    }

    #endregion

    private class ExtractableBlock
    {
        public List<StatementSyntax> Statements { get; set; } = [];
        public HashSet<string> Variables { get; set; } = [];
        public bool IsConditionalBlock { get; set; }
        public bool IsLoopBody { get; set; }
        public string? LoopVariable { get; set; }
    }

    private class ExtractedMethod
    {
        public required MethodDeclarationSyntax NewMethod { get; set; }
        public required StatementSyntax CallExpression { get; set; }
        public required List<StatementSyntax> OriginalStatements { get; set; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
    }
}
