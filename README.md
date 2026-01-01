# BaseScanner

A powerful C# code analysis tool that provides deep insights into code quality, performance issues, and refactoring opportunities. Works as both a CLI tool and an MCP (Model Context Protocol) server for Claude Code integration.

## Features

### Analysis Modes

| Flag | Description |
|------|-------------|
| `--deep` | Usage counting, deprecated code detection, dead code analysis |
| `--sentiment` | Code quality scoring, complexity metrics, duplicate detection |
| `--perf` | Async issues, performance anti-patterns, blocking calls |
| `--exceptions` | Exception handling issues, empty catches, swallowed exceptions |
| `--resources` | Resource leaks, IDisposable issues, event handler leaks |
| `--deps` | Circular dependencies, coupling metrics (Ce, Ca, Instability) |
| `--magic` | Magic numbers and strings detection |
| `--git` | Git churn analysis, hotspot detection |
| `--refactor` | Long methods, god classes, feature envy, parameter smells |
| `--arch` | Architecture analysis, API surface, call graph, inheritance |
| `--safety` | Null safety, immutability opportunities, logging gaps |
| `--optimize` | Optimization opportunities with code suggestions |
| `--all` | Run all analyses |

### Framework-Aware Analysis

BaseScanner intelligently detects your project's target framework and C# language version:

- **C# 6+**: Null-conditional operator (`?.`)
- **C# 7+**: Pattern matching (`is T variable`)
- **C# 8+**: Switch expressions, null-coalescing assignment (`??=`)
- **C# 9+**: Target-typed `new()`
- **.NET 6+**: `MinBy()`/`MaxBy()` LINQ methods

Suggestions are filtered to only show what's available for your project.

## Installation

### Prerequisites

- .NET 9.0 SDK
- Visual Studio 2022 (for MSBuild)

### Build

```bash
dotnet build
```

## Usage

### CLI Mode

```bash
# Analyze a project with all checks
dotnet run -- "path/to/project.csproj" --all

# Quick scan
dotnet run -- "path/to/project" --deep --perf

# Optimization suggestions only
dotnet run -- "path/to/project" --optimize
```

### MCP Server Mode (Claude Code Integration)

```bash
# Add to Claude Code
claude mcp add --transport stdio basescanner -- dotnet run --project "path/to/BaseScanner" -- --mcp
```

Available MCP tools:
- `QuickProjectScan` - Fast health check with top issues
- `AnalyzeCsharpProject` - Full analysis with configurable options
- `ListAnalysisTypes` - Show available analysis types

## Analysis Details

### Dead Code Detection
Finds unused classes, methods, and fields by analyzing symbol references across the entire project.

### Code Sentiment Analysis
Scores code quality based on:
- Cyclomatic complexity
- Nesting depth
- Method length
- Parameter count
- Duplicate detection (exact and structural)

### Performance Analysis
Detects:
- `async void` methods (exception handling issues)
- `.GetAwaiter().GetResult()` (deadlock risk)
- String concatenation in loops
- LINQ in loops
- Missing `ConfigureAwait(false)`

### Refactoring Opportunities
Identifies:
- **God Classes**: High LCOM (Lack of Cohesion), many methods/fields
- **Long Methods**: With extract method suggestions
- **Feature Envy**: Methods that use other classes more than their own
- **Parameter Smells**: Long parameter lists, primitive obsession

### Architecture Analysis
- Public API surface analysis
- Entry points and dead ends in call graph
- Deep inheritance hierarchies
- Composition over inheritance candidates

## Output Example

```
=== OPTIMIZATION ANALYSIS ===

  PERFORMANCE OPTIMIZATIONS (28):
    AsyncVoid (5):
      Open.cs:1156 [High] async void method - use async Task instead
        - async void SetLatestConversionRate
        + async Task SetLatestConversionRate

  MODERNIZATION OPTIMIZATIONS (2):
    NullConditionalOperator (2):
      Open.cs:358 [High] Null check can use null-conditional operator
        - if (x != null) { x.Method(); }
        + x?.Method();

  === OPTIMIZATION SUMMARY ===
    Total opportunities:  30
    High confidence:      10
    Medium confidence:    20
```

## Project Structure

```
BaseScanner/
├── Analyzers/
│   ├── ArchitectureAnalyzer.cs
│   ├── OptimizationAnalyzer.cs
│   └── Optimizations/
│       ├── AsyncPatternDetector.cs
│       ├── CollectionOptimizationDetector.cs
│       ├── LinqOptimizationDetector.cs
│       └── ModernCSharpDetector.cs
├── Context/
│   ├── CodeContext.cs
│   └── ContextCache.cs
├── Services/
│   ├── AnalysisService.cs
│   └── AnalysisResult.cs
├── Tools/
│   └── AnalyzerTools.cs
└── Program.cs
```

## License

MIT

## Contributing

Contributions welcome! Please open an issue or PR.
