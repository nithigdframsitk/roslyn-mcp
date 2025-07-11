using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMCP;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            // Configure all logs to go to stderr
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
        //
        // Console.WriteLine(await RoslynTools.FindUsagesInSolution(
        //     "C:\\Users\\NithiDhanasekaran\\source\\repos\\146314\\Framsikt\\Framsikt.Entities\\TenantDBContext.cs",
        //     19, 41,
        //     "C:\\Users\\NithiDhanasekaran\\source\\repos\\146314\\Framsikt\\FramsiktWorkerRole.Core.sln"
        // ));

        // Console.WriteLine(await RoslynTools.BuildDependencyTree(
        //     "C:\\Users\\NithiDhanasekaran\\source\\repos\\146314\\Framsikt\\Framsikt.BL\\Repository\\BudgetProposalProcessRepository.cs",
        //     758, 54,
        //     "C:\\Users\\NithiDhanasekaran\\source\\repos\\146314\\Framsikt\\FramsiktWebRole.Core.sln"
        // ));

        Console.WriteLine(await RoslynTools.FindExposingAPIs(
            "C:\\Users\\NithiDhanasekaran\\source\\repos\\146314\\Framsikt\\Framsikt.Entities\\TenantDBContext.cs",
            185, 37,
            "C:\\Users\\NithiDhanasekaran\\source\\repos\\146314\\Framsikt\\FramsiktWebRole.Core.sln"
        ));

        await builder.Build().RunAsync();
    }

    public static async Task<string> FindContainingProjectAsync(string filePath)
    {
        // Start from the directory containing the file and go up until we find a .csproj file
        DirectoryInfo directory = new FileInfo(filePath).Directory;

        while (directory != null)
        {
            var projectFiles = directory.GetFiles("*.csproj");
            if (projectFiles.Length > 0)
            {
                var workspace = WorkspaceManager.GetWorkspace();

                try
                {
                    // Use the cache-enabled method to load the project
                    var project = await WorkspaceManager.GetOrLoadProjectAsync(projectFiles[0].FullName);

                    var documents = project.Documents
                        .Where(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (documents.Any())
                    {
                        return projectFiles[0].FullName;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error opening project: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                    }
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static async Task ValidateFileInProjectContextAsync(string filePath, string projectPath,
        TextWriter writer = null, bool runAnalyzers = true)
    {
        writer ??= Console.Out;

        try
        {
            // Get project from the workspace manager
            writer.WriteLine($"Loading project from cache or disk: {projectPath}");
            var project = await WorkspaceManager.GetOrLoadProjectAsync(projectPath);
            if (project == null)
            {
                writer.WriteLine($"Error: Could not load project '{projectPath}'.");
                return;
            }

            writer.WriteLine($"Project loaded successfully: {project.Name}");


            // Find the document in the project
            var document = project.Documents
                .FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            if (document == null)
            {
                writer.WriteLine("Error: File not found in the project documents.");
                writer.WriteLine("All project documents:");
                foreach (var doc in project.Documents)
                {
                    writer.WriteLine($"  - {doc.FilePath}");
                }

                return;
            }

            writer.WriteLine($"Document found: {document.Name}");

            // The rest of the method logic remains the same...
            // Parse syntax tree
            var syntaxTree = await document.GetSyntaxTreeAsync();
            var syntaxDiagnostics = syntaxTree.GetDiagnostics();

            if (syntaxDiagnostics.Any())
            {
                writer.WriteLine("Syntax errors found:");
                foreach (var diagnostic in syntaxDiagnostics)
                {
                    var location = diagnostic.Location.GetLineSpan();
                    writer.WriteLine($"Line {location.StartLinePosition.Line + 1}: {diagnostic.GetMessage()}");
                }
            }
            else
            {
                writer.WriteLine("No syntax errors found.");
            }

            // Get the semantic model for deeper analysis
            var semanticModel = await document.GetSemanticModelAsync();
            var semanticDiagnostics = semanticModel.GetDiagnostics();

            if (semanticDiagnostics.Any())
            {
                writer.WriteLine("\nSemantic errors found:");
                foreach (var diagnostic in semanticDiagnostics)
                {
                    var location = diagnostic.Location.GetLineSpan();
                    writer.WriteLine($"Line {location.StartLinePosition.Line + 1}: {diagnostic.GetMessage()}");
                }
            }
            else
            {
                writer.WriteLine("No semantic errors found.");
            }

            // Check compilation for the entire project to validate references
            var compilation = await project.GetCompilationAsync();

            // Get compilation diagnostics for the file
            var compilationDiagnostics = compilation.GetDiagnostics()
                .Where(d => d.Location.SourceTree != null &&
                            string.Equals(d.Location.SourceTree.FilePath, filePath,
                                StringComparison.OrdinalIgnoreCase))
                .ToList();

            // If analyzers are requested, run them and get their diagnostics
            IEnumerable<Diagnostic> analyzerDiagnostics = Array.Empty<Diagnostic>();
            if (runAnalyzers)
            {
                writer.WriteLine("\nRunning code analyzers...");

                try
                {
                    // Get the analyzer assembly paths
                    var analyzerAssemblies = new List<string>();

                    // Try to find the analyzer assemblies in the NuGet packages
                    var nugetPackagesPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                                            ?? Path.Combine(
                                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                                ".nuget", "packages");

                    // Microsoft.CodeAnalysis.NetAnalyzers
                    var netAnalyzersPath = Path.Combine(nugetPackagesPath, "microsoft.codeanalysis.analyzers", "3.11.0",
                        "analyzers", "dotnet", "cs", "Microsoft.CodeAnalysis.Analyzers.dll");
                    if (File.Exists(netAnalyzersPath))
                        analyzerAssemblies.Add(netAnalyzersPath);

                    var csharpAnalyzersPath = Path.Combine(nugetPackagesPath, "microsoft.codeanalysis.analyzers",
                        "3.11.0",
                        "analyzers", "dotnet", "cs", "Microsoft.CodeAnalysis.CSharp.Analyzers.dll");
                    if (File.Exists(csharpAnalyzersPath))
                        analyzerAssemblies.Add(csharpAnalyzersPath);

                    // Load the analyzers
                    var analyzers = new List<DiagnosticAnalyzer>();
                    foreach (var analyzerPath in analyzerAssemblies)
                    {
                        try
                        {
                            var analyzerAssembly = Assembly.LoadFrom(analyzerPath);
                            var analyzerTypes = analyzerAssembly.GetTypes()
                                .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t));

                            foreach (var analyzerType in analyzerTypes)
                            {
                                try
                                {
                                    var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType);
                                    analyzers.Add(analyzer);
                                }
                                catch (Exception ex)
                                {
                                    Console.Error.WriteLine($"Error creating analyzer instance: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Error loading analyzer assembly: {ex.Message}");
                        }
                    }

                    // If no analyzers were found in the NuGet packages, try to use the ones that are already loaded
                    if (!analyzers.Any())
                    {
                        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                        foreach (var assembly in loadedAssemblies)
                        {
                            try
                            {
                                if (assembly.FullName.Contains("Microsoft.CodeAnalysis") &&
                                    assembly.FullName.Contains("Analyzers"))
                                {
                                    var analyzerTypes = assembly.GetTypes()
                                        .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t));

                                    foreach (var analyzerType in analyzerTypes)
                                    {
                                        try
                                        {
                                            var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType);
                                            analyzers.Add(analyzer);
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.Error.WriteLine($"Error creating analyzer instance: {ex.Message}");
                                        }
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                // Ignore errors when trying to get types from dynamic assemblies
                            }
                        }
                    }

                    // Add the analyzers to the compilation
                    if (analyzers.Any())
                    {
                        writer.WriteLine($"Found {analyzers.Count} analyzers");

                        // Create a CompilationWithAnalyzers object
                        var compilationWithAnalyzers = compilation.WithAnalyzers(
                            ImmutableArray.CreateRange(analyzers));

                        // Get the diagnostics from the analyzers
                        var allAnalyzerDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

                        // Filter to only include diagnostics for the current file
                        analyzerDiagnostics = allAnalyzerDiagnostics.Where(d =>
                            d.Location.SourceTree != null &&
                            string.Equals(d.Location.SourceTree.FilePath, filePath,
                                StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        writer.WriteLine("No analyzers found");
                    }
                }
                catch (Exception ex)
                {
                    writer.WriteLine($"Error running analyzers: {ex.Message}");
                }
            }

            // Combine compilation diagnostics and analyzer diagnostics
            var allDiagnostics = compilationDiagnostics.Concat(analyzerDiagnostics);

            if (allDiagnostics.Any())
            {
                writer.WriteLine("\nCompilation and analyzer diagnostics:");
                foreach (var diagnostic in allDiagnostics.OrderBy(d => d.Severity))
                {
                    var location = diagnostic.Location.GetLineSpan();
                    var severity = diagnostic.Severity.ToString();
                    writer.WriteLine(
                        $"[{severity}] Line {location.StartLinePosition.Line + 1}: {diagnostic.Id} - {diagnostic.GetMessage()}");
                }
            }
            else
            {
                writer.WriteLine("File compiles successfully in project context with no analyzer warnings.");
            }
        }
        catch (Exception ex)
        {
            writer.WriteLine($"Error validating file: {ex.Message}");
            if (ex.InnerException != null)
            {
                writer.WriteLine($"Inner exception: {ex.InnerException.Message}");
            }

            writer.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Ensures that C# language services are properly registered with the workspace
    /// </summary>
    public static void EnsureCSharpLanguageServicesRegistered(MSBuildWorkspace workspace)
    {
        try
        {
            // First try to register all language services using reflection
            Type msbuildWorkspaceType = typeof(MSBuildWorkspace);
            var registerMethod = msbuildWorkspaceType.GetMethod("RegisterLanguageServices",
                BindingFlags.NonPublic | BindingFlags.Instance);

            registerMethod?.Invoke(workspace, null);

            // Explicitly register C# language service if available
            // This ensures the C# language is supported
            var languageServicesField = msbuildWorkspaceType.GetField("_languageServices",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (languageServicesField != null)
            {
                var languageServices = languageServicesField.GetValue(workspace);
                var languageServicesType = languageServices.GetType();

                // Try to find the method to register a language service
                var registerLanguageServiceMethod = languageServicesType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Register" && m.GetParameters().Length >= 1);

                if (registerLanguageServiceMethod != null)
                {
                    // Get the CSharpLanguageService type
                    var csharpLanguageServiceType =
                        Type.GetType(
                            "Microsoft.CodeAnalysis.CSharp.CSharpLanguageService, Microsoft.CodeAnalysis.CSharp");
                    if (csharpLanguageServiceType != null)
                    {
                        // Create an instance of the CSharpLanguageService
                        var csharpLanguageService = Activator.CreateInstance(csharpLanguageServiceType);

                        // Register it with the language services
                        registerLanguageServiceMethod.Invoke(languageServices, new[] { csharpLanguageService });
                        Console.WriteLine("Successfully registered C# language service explicitly");
                    }
                }
            }

            // Force load the CSharp assembly to ensure its language services are available
            RuntimeHelpers.RunClassConstructor(typeof(CSharpSyntaxTree).TypeHandle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Error while registering language services: {ex.Message}");
            // Continue execution as the standard registration might still work
        }
    }

    /// <summary>
    /// Adds Microsoft recommended analyzers to the compilation
    /// </summary>
    private static Compilation AddAnalyzersToCompilation(Compilation compilation)
    {
        try
        {
            // Get the analyzer assembly paths
            var analyzerAssemblies = new List<string>();

            // Try to find the analyzer assemblies in the NuGet packages
            var nugetPackagesPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                        ".nuget", "packages");

            // Microsoft.CodeAnalysis.NetAnalyzers
            var netAnalyzersPath = Path.Combine(nugetPackagesPath, "microsoft.codeanalysis.analyzers", "3.11.0",
                "analyzers", "dotnet", "cs", "Microsoft.CodeAnalysis.Analyzers.dll");
            if (File.Exists(netAnalyzersPath))
                analyzerAssemblies.Add(netAnalyzersPath);

            var csharpAnalyzersPath = Path.Combine(nugetPackagesPath, "microsoft.codeanalysis.analyzers", "3.11.0",
                "analyzers", "dotnet", "cs", "Microsoft.CodeAnalysis.CSharp.Analyzers.dll");
            if (File.Exists(csharpAnalyzersPath))
                analyzerAssemblies.Add(csharpAnalyzersPath);

            // Load the analyzers
            var analyzers = new List<DiagnosticAnalyzer>();
            foreach (var analyzerPath in analyzerAssemblies)
            {
                try
                {
                    var analyzerAssembly = Assembly.LoadFrom(analyzerPath);
                    var analyzerTypes = analyzerAssembly.GetTypes()
                        .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t));

                    foreach (var analyzerType in analyzerTypes)
                    {
                        try
                        {
                            var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType);
                            analyzers.Add(analyzer);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Error creating analyzer instance: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error loading analyzer assembly: {ex.Message}");
                }
            }

            // If no analyzers were found in the NuGet packages, try to use the ones that are already loaded
            if (!analyzers.Any())
            {
                var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in loadedAssemblies)
                {
                    try
                    {
                        if (assembly.FullName.Contains("Microsoft.CodeAnalysis") &&
                            assembly.FullName.Contains("Analyzers"))
                        {
                            var analyzerTypes = assembly.GetTypes()
                                .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t));

                            foreach (var analyzerType in analyzerTypes)
                            {
                                try
                                {
                                    var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType);
                                    analyzers.Add(analyzer);
                                }
                                catch (Exception ex)
                                {
                                    Console.Error.WriteLine($"Error creating analyzer instance: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore errors when trying to get types from dynamic assemblies
                    }
                }
            }

            // Add the analyzers to the compilation
            if (analyzers.Any())
            {
                Console.Error.WriteLine($"Added {analyzers.Count} analyzers to compilation");

                // Create a CompilationWithAnalyzers object
                var compilationWithAnalyzers = compilation.WithAnalyzers(
                    ImmutableArray.CreateRange(analyzers));

                // Get the diagnostics from the analyzers
                var analyzerDiagnostics = compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().Result;

                // Add the analyzer diagnostics to the compilation diagnostics
                var allDiagnostics = compilation.GetDiagnostics()
                    .Concat(analyzerDiagnostics)
                    .ToImmutableArray();

                // Return the original compilation (we've already extracted the diagnostics)
                return compilation;
            }

            Console.Error.WriteLine("No analyzers found");
            return compilation;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error adding analyzers: {ex.Message}");
            return compilation;
        }
    }
}

[McpServerToolType]
public static class RoslynTools
{
    [McpServerTool,
     Description(
         "Finds all database tables (Entity Framework DbSet properties) that a specific controller action depends on.")]
    public static async Task<string> FindActionDbDependencies(
        [Description("Path to the file containing the controller action.")]
        string filePath,
        [Description("Line number (1-based) of the controller action method.")]
        int line,
        [Description("Column number (1-based) of the controller action method.")]
        int column,
        [Description("Optional path to the .sln file. If not provided, it will be searched for.")]
        string solutionPath = null)
    {
        try
        {
            // --- Standard setup to find the starting symbol ---
            var (systemPath, error) = NormalizeAndValidateFilePath(filePath);
            if (error != null) return error;

            var projectPath = await Program.FindContainingProjectAsync(systemPath);
            if (string.IsNullOrEmpty(projectPath))
                return "Error: Couldn't find a project containing this file.";

            string resolvedSolutionPath = solutionPath;
            if (string.IsNullOrWhiteSpace(resolvedSolutionPath))
            {
                resolvedSolutionPath = await FindContainingSolutionAsync(projectPath);
            }

            var (solution, document) = await LoadSolutionAndDocument(resolvedSolutionPath, projectPath, systemPath);
            if (document == null) return "Error: File not found in project.";

            var symbol = await GetSymbolAtPosition(document, line, column);
            if (symbol == null) return "No symbol found at specified position.";

            // --- Validate that the starting symbol is a controller action ---
            if (symbol is not IMethodSymbol methodSymbol || !IsApiControllerAction(methodSymbol))
            {
                return
                    "Error: The specified symbol is not a valid API controller action. Please select a public method in a class inheriting from 'ControllerBase' or 'ApiController'.";
            }

            var results = new StringBuilder();
            results.AppendLine($"# Database Dependency Analysis for Action: `{methodSymbol.ToDisplayString()}`");
            results.AppendLine();

            // --- Recursive search for DbSet dependencies ---
            var foundDbSets = new List<IPropertySymbol>();
            var visitedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            await FindDbSetsInCalleesAsync(methodSymbol, solution, foundDbSets, visitedSymbols);

            // --- Build the final report ---
            if (!foundDbSets.Any())
            {
                results.AppendLine("## No database dependencies (DbSets) found.");
                results.AppendLine(
                    "This API action does not appear to reference any EF Core DbSet properties within the search depth.");
            }
            else
            {
                results.AppendLine("## Found Database Table Dependencies (DbSets) 💾");
                results.AppendLine("This API action depends on the following database tables:");
                results.AppendLine();

                foreach (var dbSet in foundDbSets.Distinct(SymbolEqualityComparer.Default).OfType<IPropertySymbol>())
                {
                    var entityType = (dbSet.Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault()?.Name ??
                                     "Unknown";

                    results.AppendLine($"### Table: `{entityType}`");
                    results.AppendLine($"- **DbSet Property:** `{dbSet.Name}`");
                    results.AppendLine($"- **DbContext:** `{dbSet.ContainingType.Name}`");
                    results.AppendLine($"- **Full Declaration:** `{dbSet.ToDisplayString()}`");
                    results.AppendLine("---");
                }
            }

            return results.ToString();
        }
        catch (Exception ex)
        {
            return $"An error occurred while finding database dependencies: {ex.Message}\n{ex.StackTrace}";
        }
    }

    /// <summary>
    /// Recursively traverses the call tree downwards (callees) from a starting symbol to find DbSet properties.
    /// </summary>
    private static async Task FindDbSetsInCalleesAsync(ISymbol symbol, Solution solution,
        List<IPropertySymbol> foundDbSets, ISet<ISymbol> visitedSymbols, int maxDepth = 15)
    {
        // Base case: check depth and prevent cycles/redundant work
        if (visitedSymbols.Count >= maxDepth || !visitedSymbols.Add(symbol))
        {
            return;
        }

        // Check if the current symbol is a DbSet property
        if (symbol is IPropertySymbol propertySymbol && IsDbSetProperty(propertySymbol))
        {
            foundDbSets.Add(propertySymbol);
            return; // Found a database dependency, no need to go further down this branch
        }

        // Find all symbols that this symbol calls
        var callees = await FindCalleesAsync(symbol, solution);
        foreach (var callee in callees)
        {
            // Recurse down the call tree
            await FindDbSetsInCalleesAsync(callee, solution, foundDbSets, visitedSymbols, maxDepth);
        }
    }

    /// <summary>
    /// Determines if a property is an EF Core DbSet<T>.
    /// </summary>
    private static bool IsDbSetProperty(IPropertySymbol propertySymbol)
    {
        if (propertySymbol?.Type is not INamedTypeSymbol typeSymbol)
        {
            return false;
        }

        return typeSymbol.IsGenericType && typeSymbol.Name == "DbSet";
    }

    [McpServerTool,
     Description("Searches for a symbol (class, method, property, etc.) by its name across the entire solution.")]
    public static async Task<string> SearchSymbolInSolution(
        [Description("The name of the symbol to search for.")]
        string symbolName,
        [Description("The file path to the .sln solution file.")]
        string solutionPath,
        [Description("Optional: The kind of symbol to search for (e.g., 'Class', 'Method', 'Property', 'Interface').")]
        string symbolKind = null)
    {
        try
        {
            // --- Validate inputs ---
            if (string.IsNullOrWhiteSpace(symbolName))
                return "Error: Symbol name cannot be empty.";
            if (!File.Exists(solutionPath))
                return $"Error: Solution file not found at '{solutionPath}'.";

            var results = new StringBuilder();
            results.AppendLine($"# Symbol Search for \"{symbolName}\"");
            if (!string.IsNullOrWhiteSpace(symbolKind))
                results.AppendLine($"*Filtering by kind: **{symbolKind}***");
            results.AppendLine();

            // --- Load solution and perform search ---
            var solution = await WorkspaceManager.GetOrLoadSolutionAsync(solutionPath);
            var foundSymbols = new List<ISymbol>();

            await Console.Error.WriteLineAsync(
                $"Searching for '{symbolName}' in solution '{Path.GetFileName(solutionPath)}'...");

            foreach (var project in solution.Projects)
            {
                var symbolsInProject = await SymbolFinder.FindDeclarationsAsync(project, symbolName, ignoreCase: true);

                // Further filter by kind if the user specified one
                if (!string.IsNullOrWhiteSpace(symbolKind))
                {
                    symbolsInProject = symbolsInProject.Where(s =>
                        s.Kind.ToString().Equals(symbolKind, StringComparison.OrdinalIgnoreCase));
                }

                foundSymbols.AddRange(symbolsInProject);
            }

            await Console.Error.WriteLineAsync($"Search complete. Found {foundSymbols.Count} matching symbol(s).");

            // --- Build the report ---
            if (!foundSymbols.Any())
            {
                results.AppendLine("## No matching symbols found in the solution.");
            }
            else
            {
                results.AppendLine($"## Found {foundSymbols.Count} declaration(s):");
                int count = 1;
                foreach (var symbol in foundSymbols)
                {
                    var location = symbol.Locations.FirstOrDefault();
                    if (location == null || location.SourceTree == null) continue;

                    var doc = solution.GetDocument(location.SourceTree);
                    var lineSpan = location.GetLineSpan();
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var col = lineSpan.StartLinePosition.Character + 1;

                    results.AppendLine($"### {count++}. `{symbol.ToDisplayString()}`");
                    results.AppendLine($"- **Kind:** {symbol.Kind}");
                    results.AppendLine($"- **File:** {location.SourceTree.FilePath}");
                    results.AppendLine($"- **Position:** Line {line}, Column {col}");

                    if (doc != null)
                    {
                        AppendCodeContext(results, doc, line);
                    }

                    results.AppendLine("\n---");
                }
            }

            return results.ToString();
        }
        catch (Exception ex)
        {
            return $"An error occurred during the symbol search: {ex.Message}\n{ex.StackTrace}";
        }
    }

    [McpServerTool,
     Description(
         "Traces a symbol's call tree to find the publicly exposed API controller methods (endpoints) that use it.")]
    public static async Task<string> FindExposingAPIs(
        [Description("Path to the source file where the code exists")]
        string filePath,
        [Description("Line number (1-based) of the symbol to analyze")]
        int line,
        [Description("Column number (1-based) of the symbol to analyze")]
        int column,
        [Description("Optional path to the .sln file. If not provided, it will be searched for.")]
        string solutionPath = null)
    {
        try
        {
            // --- Standard setup to find the symbol ---
            var (systemPath, error) = NormalizeAndValidateFilePath(filePath);
            if (error != null) return error;

            var projectPath = await Program.FindContainingProjectAsync(systemPath);
            if (string.IsNullOrEmpty(projectPath))
                return "Error: Couldn't find a project containing this file.";

            string resolvedSolutionPath = solutionPath;
            if (string.IsNullOrWhiteSpace(resolvedSolutionPath))
            {
                resolvedSolutionPath = await FindContainingSolutionAsync(projectPath);
            }

            var (solution, document) = await LoadSolutionAndDocument(resolvedSolutionPath, projectPath, systemPath);
            if (document == null) return "Error: File not found in project.";

            var symbol = await GetSymbolAtPosition(document, line, column);
            if (symbol == null) return "No symbol found at specified position.";

            var results = new StringBuilder();
            results.AppendLine($"# API Exposure Analysis for: `{symbol.ToDisplayString()}`");
            results.AppendLine();

            // --- Recursive search for exposing APIs ---
            var foundApis = new List<IMethodSymbol>();
            var visitedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            await FindApiControllerActionsInCallersAsync(symbol, solution, foundApis, visitedSymbols);

            // --- Build the final report ---
            if (!foundApis.Any())
            {
                results.AppendLine("## No exposing API endpoints found.");
                results.AppendLine(
                    "This code does not appear to be called by any public API controller actions within the search depth.");
            }
            else
            {
                results.AppendLine("## Found Exposing API Endpoints 🚀");
                results.AppendLine("The following API endpoints are impacted by changes to the selected code:");
                results.AppendLine();

                foreach (var apiMethod in foundApis.Distinct(SymbolEqualityComparer.Default).OfType<IMethodSymbol>())
                {
                    var httpMethod = GetHttpMethod(apiMethod);
                    var route = GetRouteTemplate(apiMethod);
                    var location = apiMethod.Locations.FirstOrDefault()?.GetLineSpan();
                    var fileName = location != null ? Path.GetFileName(location.Value.Path) : "N/A";
                    var lineNumber = location != null ? location.Value.StartLinePosition.Line + 1 : 0;


                    results.AppendLine($"### `{httpMethod} {route}`");
                    results.AppendLine($"- **Action Method:** `{apiMethod.ToDisplayString()}`");
                    results.AppendLine($"- **Controller:** `{apiMethod.ContainingType.Name}`");
                    results.AppendLine($"- **Location:** {fileName}:{lineNumber}");
                    results.AppendLine("---");
                }
            }

            return results.ToString();
        }
        catch (Exception ex)
        {
            return $"An error occurred while finding exposing APIs: {ex.Message}\n{ex.StackTrace}";
        }
    }


    /// <summary>
    /// Recursively traverses the call tree upwards from a starting symbol to find methods that are API controller actions.
    /// </summary>
    private static async Task FindApiControllerActionsInCallersAsync(ISymbol symbol, Solution solution,
        List<IMethodSymbol> foundApis, ISet<ISymbol> visitedSymbols, int maxDepth = 100000)
    {
        // Base case: check depth and prevent cycles
        if (visitedSymbols.Count >= maxDepth)
        {
            return;
        }

        if (!visitedSymbols.Add(symbol))
        {
            return;
        }

        // Check if the current symbol is itself an API action
        if (symbol is IMethodSymbol methodSymbol && IsApiControllerAction(methodSymbol))
        {
            foundApis.Add(methodSymbol);
            return; // Found an endpoint, no need to go further up this branch
        }

        // Find all symbols that call the current symbol
        var callers = await SymbolFinder.FindCallersAsync(symbol, solution);
        foreach (var caller in callers)
        {
            // Recurse up the call tree
            await FindApiControllerActionsInCallersAsync(caller.CallingSymbol, solution, foundApis, visitedSymbols,
                maxDepth);
        }
    }

    /// <summary>
    /// Determines if a method is a public action method within a class that inherits from a known controller base class.
    /// </summary>
    private static bool IsApiControllerAction(IMethodSymbol methodSymbol)
    {
        if (methodSymbol == null || methodSymbol.DeclaredAccessibility != Accessibility.Public)
        {
            return false;
        }

        var containingType = methodSymbol.ContainingType;
        if (containingType == null)
        {
            return false;
        }

        // Check if the containing class is a controller
        bool isController = InheritsFrom(containingType, "ControllerBase") ||
                            InheritsFrom(containingType, "ApiController");
        if (!isController)
        {
            return false;
        }

        // Check if the method has any HTTP method attributes (e.g., [HttpGet], [HttpPost])
        return methodSymbol.GetAttributes().Any(attr =>
            attr.AttributeClass.Name.StartsWith("Http") && attr.AttributeClass.Name.EndsWith("Attribute"));
    }

    /// <summary>
    /// Checks if a type inherits from a base type with the given name.
    /// </summary>
    private static bool InheritsFrom(INamedTypeSymbol type, string baseTypeName)
    {
        var current = type;
        while (current != null)
        {
            if (current.Name == baseTypeName)
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Extracts the HTTP method (e.g., "GET", "POST") from a method's attributes.
    /// </summary>
    private static string GetHttpMethod(IMethodSymbol method)
    {
        var httpAttribute = method.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name.StartsWith("Http"));
        if (httpAttribute != null)
        {
            return httpAttribute.AttributeClass.Name.Replace("Http", "").Replace("Attribute", "").ToUpper();
        }

        return "UNKNOWN";
    }

    /// <summary>
    /// Constructs the full route template for a controller action.
    /// </summary>
    private static string GetRouteTemplate(IMethodSymbol method)
    {
        string controllerRoute = method.ContainingType.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass.Name == "RouteAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "";

        string actionRoute = method.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass.Name.StartsWith("Http"))
            ?.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "";

        // Clean up controller route template, removing placeholders like "[controller]"
        controllerRoute = controllerRoute.Replace("[controller]", method.ContainingType.Name.Replace("Controller", ""));

        // Combine routes
        var fullRoute = Path.Combine("/" + controllerRoute, actionRoute).Replace("\\", "/");
        return fullRoute;
    }


    // --- All other existing tools and helpers from the previous version remain below ---

    [McpServerTool,
     Description(
         "Validates a C# file using Roslyn and runs code analyzers. Accepts either a relative or absolute file path.")]
    public static async Task<string> ValidateFile(
        [Description("The path to the C# file to validate")]
        string filePath,
        [Description("Run analyzers (default: true)")]
        bool runAnalyzers = true)
    {
        try
        {
            LogValidationDetails(filePath, runAnalyzers);

            var (systemPath, error) = NormalizeAndValidateFilePath(filePath);
            if (error != null) return error;

            var projectPath = await Program.FindContainingProjectAsync(systemPath);
            if (string.IsNullOrEmpty(projectPath))
            {
                await Console.Error.WriteLineAsync("Could not find a project containing this file");
                return "Error: Couldn't find a project containing this file.";
            }

            await Console.Error.WriteLineAsync($"Found containing project: '{projectPath}'");
            await Console.Error.WriteLineAsync("Validating file in project context...");

            var outputWriter = new StringWriter();
            await Program.ValidateFileInProjectContextAsync(systemPath, projectPath, outputWriter, runAnalyzers);
            var result = outputWriter.ToString();

            await Console.Error.WriteLineAsync("Validation complete");
            return result;
        }
        catch (Exception ex)
        {
            LogException(ex, nameof(ValidateFile));
            return $"Error processing file: {ex.Message}";
        }
    }

    [McpServerTool,
     Description("Find all references to a symbol at the specified position across the entire solution.")]
    public static async Task<string> FindUsagesInSolution(
        [Description("Path to the source file")]
        string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based)")]
        int column,
        [Description("Optional path to the .sln file. If not provided, it will be searched for.")]
        string solutionPath = null)
    {
        try
        {
            var (systemPath, error) = NormalizeAndValidateFilePath(filePath);
            if (error != null) return error;

            var projectPath = await Program.FindContainingProjectAsync(systemPath);
            if (string.IsNullOrEmpty(projectPath))
                return "Error: Couldn't find a project containing this file.";

            // --- Solution Path Resolution ---
            string resolvedSolutionPath = solutionPath;
            if (string.IsNullOrWhiteSpace(resolvedSolutionPath))
            {
                await Console.Error.WriteLineAsync("Solution path not provided, searching for it...");
                resolvedSolutionPath = await FindContainingSolutionAsync(projectPath);
                if (string.IsNullOrWhiteSpace(resolvedSolutionPath))
                {
                    await Console.Error.WriteLineAsync(
                        "Could not find a containing solution file. Proceeding in project-only mode.");
                }
                else
                {
                    await Console.Error.WriteLineAsync($"Found containing solution: '{resolvedSolutionPath}'");
                }
            }
            else
            {
                // Validate the provided solution path
                if (!File.Exists(resolvedSolutionPath))
                {
                    return $"Error: Provided solution file does not exist at '{resolvedSolutionPath}'.";
                }

                await Console.Error.WriteLineAsync($"Using provided solution path: '{resolvedSolutionPath}'");
            }

            var (solution, document) = await LoadSolutionAndDocument(resolvedSolutionPath, projectPath, systemPath);
            if (document == null) return "Error: File not found in project.";

            var symbol = await GetSymbolAtPosition(document, line, column);
            if (symbol == null) return "No symbol found at specified position.";

            var references = await SymbolFinder.FindReferencesAsync(symbol, solution);

            return BuildUsageAnalysisReport(symbol, references, systemPath, line, column, projectPath,
                resolvedSolutionPath,
                solution);
        }
        catch (Exception ex)
        {
            return $"Error finding usages: {ex.Message}\n{ex.StackTrace}";
        }
    }

    [McpServerTool, Description("Find all references to a symbol at the specified position.")]
    public static async Task<string> FindUsages(
        [Description("Path to the file")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based)")]
        int column)
    {
        try
        {
            var (systemPath, error) = NormalizeAndValidateFilePath(filePath);
            if (error != null) return error;

            var projectPath = await Program.FindContainingProjectAsync(systemPath);
            if (string.IsNullOrEmpty(projectPath))
                return "Error: Couldn't find a project containing this file.";

            var project = await WorkspaceManager.GetOrLoadProjectAsync(projectPath);
            var document = project.Documents.FirstOrDefault(d =>
                string.Equals(d.FilePath, systemPath, StringComparison.OrdinalIgnoreCase));

            if (document == null) return "Error: File not found in project.";

            var symbol = await GetSymbolAtPosition(document, line, column);
            if (symbol == null) return "No symbol found at specified position.";

            var references = await SymbolFinder.FindReferencesAsync(symbol, project.Solution);

            return BuildUsageAnalysisReport(symbol, references, systemPath, line, column, projectPath, null,
                project.Solution);
        }
        catch (Exception ex)
        {
            return $"Error finding usages: {ex.Message}\n{ex.StackTrace}";
        }
    }


    [McpServerTool,
     Description(
         "Builds a dependency tree (callers and callees) for a symbol at a specific position. Usefull for impact analysis.")]
    public static async Task<string> BuildDependencyTree(
        [Description("Path to the source file")]
        string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based)")]
        int column,
        [Description("Optional path to the .sln file. If not provided, it will be searched for.")]
        string solutionPath = null)
    {
        try
        {
            // --- (Standard setup code remains the same) ---
            var (systemPath, error) = NormalizeAndValidateFilePath(filePath);
            if (error != null) return error;

            var projectPath = await Program.FindContainingProjectAsync(systemPath);
            if (string.IsNullOrEmpty(projectPath))
                return "Error: Couldn't find a project containing this file.";

            string resolvedSolutionPath = solutionPath;
            if (string.IsNullOrWhiteSpace(resolvedSolutionPath))
            {
                resolvedSolutionPath = await FindContainingSolutionAsync(projectPath);
            }

            var (solution, document) = await LoadSolutionAndDocument(resolvedSolutionPath, projectPath, systemPath);
            if (document == null) return "Error: File not found in project.";

            var symbol = await GetSymbolAtPosition(document, line, column);
            if (symbol == null) return "No symbol found at specified position.";

            if (symbol.Kind != SymbolKind.Method && symbol.Kind != SymbolKind.Property &&
                symbol.Kind != SymbolKind.Event && symbol.Kind != SymbolKind.Field)
            {
                return
                    $"Dependency tree can only be built for methods, properties, events, or fields. Symbol is a '{symbol.Kind}'.";
            }

            var results = new StringBuilder();
            results.AppendLine($"# Dependency Tree for: {symbol.ToDisplayString()}");
            results.AppendLine($"* **Kind:** {symbol.Kind}");
            results.AppendLine($"* **File:** {Path.GetFileName(systemPath)}");
            results.AppendLine(
                $"* **Solution Context:** {(string.IsNullOrEmpty(resolvedSolutionPath) ? "Project Only" : Path.GetFileName(resolvedSolutionPath))}");
            results.AppendLine();

            // --- Section 1: Callers (Incoming Call Hierarchy) ---
            results.AppendLine("## Callers (Incoming Call Hierarchy)");
            var directCallers = await SymbolFinder.FindCallersAsync(symbol, solution);

            if (!directCallers.Any())
            {
                results.AppendLine("No callers found in the solution.");
            }
            else
            {
                // Process each unique direct caller as the root of a call tree branch
                foreach (var directCallerGroup in directCallers.GroupBy(c => c.CallingSymbol,
                             SymbolEqualityComparer.Default))
                {
                    var directCallerSymbol = directCallerGroup.Key;

                    // Get the location of the first call to display
                    var firstLocation = directCallerGroup.SelectMany(c => c.Locations).FirstOrDefault();
                    string locationDisplay = "";
                    if (firstLocation != null)
                    {
                        var pos = firstLocation.GetLineSpan();
                        locationDisplay =
                            $" at {Path.GetFileName(pos.Path)}:{pos.StartLinePosition.Line + 1}:{pos.StartLinePosition.Character + 1}";
                    }

                    results.AppendLine(
                        $"- **{directCallerSymbol.Kind}:** `{directCallerSymbol.ToDisplayString()}`{locationDisplay}");

                    // Start the recursive search for this branch
                    var visitedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { symbol };
                    await BuildCallerTreeRecursiveAsync(directCallerSymbol, solution, results, 1, visitedSymbols);
                }
            }

            results.AppendLine();


            // --- Section 2: Callees (Outgoing Calls) ---
            results.AppendLine("## Callees (What does this symbol call?)");
            var callees = await FindCalleesAsync(symbol, solution);
            if (!callees.Any())
            {
                results.AppendLine("No outgoing calls or dependencies found from this symbol.");
            }
            else
            {
                // Group distinct callees to provide a clean list
                foreach (var callee in callees.Distinct(SymbolEqualityComparer.Default).OrderBy(s => s.Name))
                {
                    results.AppendLine($"- **{callee.Kind}:** `{callee.ToDisplayString()}`");
                }
            }

            return results.ToString();
        }
        catch (Exception ex)
        {
            return $"Error building dependency tree: {ex.Message}\n{ex.StackTrace}";
        }
    }

    /// <summary>
    /// Recursively finds and builds the caller tree for a given symbol.
    /// </summary>
    private static async Task BuildCallerTreeRecursiveAsync(ISymbol symbol, Solution solution, StringBuilder results,
        int depth, ISet<ISymbol> visitedSymbols, int maxDepth = 5)
    {
        // --- Base cases for recursion ---
        if (depth >= maxDepth)
        {
            results.AppendLine($"{new string(' ', depth * 2)}  └─ ... (depth limit reached)");
            return;
        }

        if (!visitedSymbols.Add(symbol))
        {
            results.AppendLine($"{new string(' ', depth * 2)}  └─ ... (recursive call to `{symbol.Name}` detected)");
            return;
        }

        var callers = await SymbolFinder.FindCallersAsync(symbol, solution);
        if (!callers.Any())
        {
            return; // No more callers up the chain.
        }

        var indent = new string(' ', depth * 2);
        foreach (var callerInfo in callers.GroupBy(c => c.CallingSymbol, SymbolEqualityComparer.Default))
        {
            var callingSymbol = callerInfo.Key;

            // Get the location of the first call to display
            var firstLocation = callerInfo.SelectMany(c => c.Locations).FirstOrDefault();
            string locationDisplay = "";
            if (firstLocation != null)
            {
                var pos = firstLocation.GetLineSpan();
                locationDisplay =
                    $" at {Path.GetFileName(pos.Path)}:{pos.StartLinePosition.Line + 1}:{pos.StartLinePosition.Character + 1}";
            }

            results.AppendLine(
                $"{indent}  └─ calls **{callingSymbol.Kind}:** `{callingSymbol.ToDisplayString()}`{locationDisplay}");

            // Recurse to find the callers of this caller
            await BuildCallerTreeRecursiveAsync(callingSymbol, solution, results, depth + 1, visitedSymbols, maxDepth);
        }
    }


    // Helper Methods

    /// <summary>
    /// Finds all symbols that the given symbol calls or references.
    /// </summary>
    private static async Task<IEnumerable<ISymbol>> FindCalleesAsync(ISymbol symbol, Solution solution)
    {
        var callees = new List<ISymbol>();
        // A symbol can be defined in multiple locations (e.g., partial classes). We check all of them.
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var syntaxNode = await syntaxReference.GetSyntaxAsync();
            var document = solution.GetDocument(syntaxReference.SyntaxTree);
            if (document == null) continue;

            var semanticModel = await document.GetSemanticModelAsync();
            if (semanticModel == null) continue;

            // Use a custom syntax walker to traverse the code block of the symbol
            var walker = new CalleeWalker(semanticModel, callees);
            walker.Visit(syntaxNode);
        }

        return callees;
    }

    /// <summary>
    /// A syntax walker that identifies method calls, object creations, and member accesses
    /// within a given syntax tree, effectively finding all outgoing "callees".
    /// </summary>
    private class CalleeWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _semanticModel;
        private readonly List<ISymbol> _callees;

        public CalleeWalker(SemanticModel semanticModel, List<ISymbol> callees)
        {
            _semanticModel = semanticModel;
            _callees = callees;
        }

        // Called when visiting a method call, e.g., `MyMethod()`
        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol != null)
            {
                _callees.Add(symbolInfo.Symbol);
            }

            base.VisitInvocationExpression(node);
        }

        // Called when visiting an object creation, e.g., `new MyClass()`
        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol != null)
            {
                _callees.Add(symbolInfo.Symbol);
            }

            base.VisitObjectCreationExpression(node);
        }

        // Called when visiting a member access, e.g. `myObject.MyProperty`
        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(node);
            // We only add properties and fields here. Methods are handled by VisitInvocationExpression.
            if (symbolInfo.Symbol is IPropertySymbol || symbolInfo.Symbol is IFieldSymbol)
            {
                _callees.Add(symbolInfo.Symbol);
            }

            base.VisitMemberAccessExpression(node);
        }
    }

    private static (string systemPath, string error) NormalizeAndValidateFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            Console.Error.WriteLine("File path is null or empty");
            return (null, "Error: File path cannot be empty.");
        }

        var normalizedPath = filePath.Replace("\\", "/");
        var systemPath = Path.IsPathRooted(normalizedPath)
            ? Path.GetFullPath(normalizedPath)
            : Path.GetFullPath(normalizedPath);

        if (!File.Exists(systemPath))
        {
            Console.Error.WriteLine($"File does not exist: '{systemPath}'");
            return (null, $"Error: File {systemPath} does not exist.");
        }

        return (systemPath, null);
    }

    private static void LogValidationDetails(string filePath, bool runAnalyzers)
    {
        Console.Error.WriteLine($"ValidateFile called with path: '{filePath}'");
        Console.Error.WriteLine($"Path type: {filePath?.GetType().FullName ?? "null"}");
        Console.Error.WriteLine($"Run analyzers: {runAnalyzers}");
    }

    private static void LogException(Exception ex, string methodName)
    {
        Console.Error.WriteLine($"ERROR in {methodName}: {ex.Message}");
        Console.Error.WriteLine($"Exception type: {ex.GetType().FullName}");
        Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");

        if (ex.InnerException != null)
        {
            Console.Error.WriteLine($"Inner exception: {ex.InnerException.Message}");
            Console.Error.WriteLine($"Inner exception type: {ex.InnerException.GetType().FullName}");
            Console.Error.WriteLine($"Inner stack trace: {ex.InnerException.StackTrace}");
        }
    }

    private static async Task<(Solution solution, Document document)> LoadSolutionAndDocument(
        string solutionPath, string projectPath, string systemPath)
    {
        Solution solution;
        Document document = null;

        if (!string.IsNullOrEmpty(solutionPath))
        {
            solution = await WorkspaceManager.GetOrLoadSolutionAsync(solutionPath);
            var targetProject = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));

            if (targetProject != null)
            {
                document = targetProject.Documents.FirstOrDefault(d =>
                    string.Equals(d.FilePath, systemPath, StringComparison.OrdinalIgnoreCase));
            }
        }
        else
        {
            var targetProject = await WorkspaceManager.GetOrLoadProjectAsync(projectPath);
            solution = targetProject?.Solution;
            if (targetProject != null)
            {
                document = targetProject.Documents.FirstOrDefault(d =>
                    string.Equals(d.FilePath, systemPath, StringComparison.OrdinalIgnoreCase));
            }
        }

        return (solution, document);
    }


    private static async Task<ISymbol> GetSymbolAtPosition(Document document, int line, int column)
    {
        var sourceText = await document.GetTextAsync();
        var position = sourceText.Lines[line - 1].Start + (column - 1);
        var semanticModel = await document.GetSemanticModelAsync();
        // Use the workspace from the document
        return await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position,
            document.Project.Solution.Workspace);
    }


    private static string BuildUsageAnalysisReport(ISymbol symbol, IEnumerable<ReferencedSymbol> references,
        string systemPath, int line, int column, string projectPath, string solutionPath, Solution solution)
    {
        var results = new StringBuilder();
        var isSolutionWide = !string.IsNullOrEmpty(solutionPath);

        BuildReportHeader(results, systemPath, line, column, projectPath, solutionPath, solution, isSolutionWide);
        BuildSymbolDetails(results, symbol);
        BuildReferencesSection(results, references, isSolutionWide);
        BuildReportSummary(results, symbol, references, isSolutionWide);

        return results.ToString();
    }

    private static void BuildReportHeader(StringBuilder results, string systemPath, int line, int column,
        string projectPath, string solutionPath, Solution solution, bool isSolutionWide)
    {
        results.AppendLine($"# Symbol Usage Analysis{(isSolutionWide ? " (Solution-wide)" : "")}");
        results.AppendLine();
        results.AppendLine("## Search Information");
        results.AppendLine($"- **File**: {systemPath}");
        results.AppendLine($"- **Position**: Line {line}, Column {column}");
        results.AppendLine($"- **Project**: {Path.GetFileName(projectPath)}");

        if (isSolutionWide)
        {
            results.AppendLine($"- **Solution**: {Path.GetFileName(solutionPath)}");
            results.AppendLine($"- **Projects in Solution**: {solution.Projects.Count()}");
        }
        else
        {
            results.AppendLine($"- **Solution**: Single project mode (no .sln file found)");
        }

        results.AppendLine();
    }

    private static void BuildSymbolDetails(StringBuilder results, ISymbol symbol)
    {
        results.AppendLine("## Symbol Details");
        results.AppendLine($"- **Name**: {symbol.Name}");
        results.AppendLine($"- **Kind**: {symbol.Kind}");
        results.AppendLine($"- **Full Name**: {symbol.ToDisplayString()}");

        if (symbol.ContainingType != null)
            results.AppendLine($"- **Containing Type**: {symbol.ContainingType.ToDisplayString()}");

        if (symbol.ContainingNamespace != null && !symbol.ContainingNamespace.IsGlobalNamespace)
            results.AppendLine($"- **Namespace**: {symbol.ContainingNamespace.ToDisplayString()}");

        results.AppendLine($"- **Accessibility**: {symbol.DeclaredAccessibility}");

        AppendSymbolSpecificDetails(results, symbol);
        results.AppendLine();
    }

    private static void AppendSymbolSpecificDetails(StringBuilder results, ISymbol symbol)
    {
        switch (symbol.Kind)
        {
            case SymbolKind.Method:
                var methodSymbol = (IMethodSymbol)symbol;
                results.AppendLine($"- **Return Type**: {methodSymbol.ReturnType.ToDisplayString()}");
                results.AppendLine($"- **Is Extension Method**: {methodSymbol.IsExtensionMethod}");
                results.AppendLine($"- **Parameter Count**: {methodSymbol.Parameters.Length}");
                break;

            case SymbolKind.Property:
                var propertySymbol = (IPropertySymbol)symbol;
                results.AppendLine($"- **Property Type**: {propertySymbol.Type.ToDisplayString()}");
                results.AppendLine($"- **Has Getter**: {propertySymbol.GetMethod != null}");
                results.AppendLine($"- **Has Setter**: {propertySymbol.SetMethod != null}");
                break;

            case SymbolKind.Field:
                var fieldSymbol = (IFieldSymbol)symbol;
                results.AppendLine($"- **Field Type**: {fieldSymbol.Type.ToDisplayString()}");
                results.AppendLine($"- **Is Const**: {fieldSymbol.IsConst}");
                results.AppendLine($"- **Is Static**: {fieldSymbol.IsStatic}");
                break;

            case SymbolKind.Event:
                var eventSymbol = (IEventSymbol)symbol;
                results.AppendLine($"- **Event Type**: {eventSymbol.Type.ToDisplayString()}");
                break;

            case SymbolKind.Parameter:
                var parameterSymbol = (IParameterSymbol)symbol;
                results.AppendLine($"- **Parameter Type**: {parameterSymbol.Type.ToDisplayString()}");
                results.AppendLine($"- **Is Optional**: {parameterSymbol.IsOptional}");
                break;

            case SymbolKind.Local:
                var localSymbol = (ILocalSymbol)symbol;
                results.AppendLine($"- **Local Type**: {localSymbol.Type.ToDisplayString()}");
                results.AppendLine($"- **Is Const**: {localSymbol.IsConst}");
                break;
        }
    }

    private static void BuildReferencesSection(StringBuilder results, IEnumerable<ReferencedSymbol> references,
        bool isSolutionWide)
    {
        var referencesByProject = references
            .SelectMany(r => r.Locations.Select(loc => new { Reference = r, Location = loc }))
            .GroupBy(x => x.Location.Document.Project.Name)
            .OrderBy(g => g.Key);

        results.AppendLine("## References");
        results.AppendLine(
            $"Found {references.Sum(r => r.Locations.Count())} references across {referencesByProject.Count()} projects.");
        results.AppendLine();

        if (isSolutionWide)
        {
            BuildSolutionWideReferences(results, referencesByProject);
        }
        else
        {
            BuildProjectReferences(results, references);
        }
    }

    private static void BuildSolutionWideReferences(StringBuilder results,
        IEnumerable<IGrouping<string, dynamic>> referencesByProject)
    {
        int referenceCount = 1;
        foreach (var projectGroup in referencesByProject)
        {
            results.AppendLine($"### Project: {projectGroup.Key}");
            results.AppendLine($"**References in this project**: {projectGroup.Count()}");
            results.AppendLine();

            foreach (var refItem in projectGroup.OrderBy(x => x.Location.Document.FilePath)
                         .ThenBy(x => x.Location.Location.GetLineSpan().StartLinePosition.Line))
            {
                ProcessReferenceLocation(results, refItem.Location, referenceCount++);
            }
        }
    }

    private static void BuildProjectReferences(StringBuilder results, IEnumerable<ReferencedSymbol> references)
    {
        int referenceCount = 1;
        foreach (var reference in references)
        {
            results.AppendLine($"### Reference Definition: {reference.Definition.ToDisplayString()}");

            foreach (var location in reference.Locations)
            {
                ProcessReferenceLocation(results, location, referenceCount++);
            }
        }
    }

    private static void ProcessReferenceLocation(StringBuilder results, ReferenceLocation location, int referenceCount)
    {
        var linePosition = location.Location.GetLineSpan();
        var refLine = linePosition.StartLinePosition.Line + 1;
        var refColumn = linePosition.StartLinePosition.Character + 1;

        results.AppendLine(
            $"#### Reference {referenceCount}: {Path.GetFileName(location.Document.FilePath)}:{refLine}:{refColumn}");
        results.AppendLine($"- **File**: {location.Document.FilePath}");

        if (location.Document.Project != null)
            results.AppendLine($"- **Project**: {location.Document.Project.Name}");

        results.AppendLine($"- **Line**: {refLine}");
        results.AppendLine($"- **Column**: {refColumn}");

        AppendCodeContext(results, location.Document, refLine);
        results.AppendLine();
    }

    private static async void AppendCodeContext(StringBuilder results, Document document, int refLine)
    {
        try
        {
            var refSourceText = await document.GetTextAsync();
            var startLine = Math.Max(0, refLine - 3);
            var endLine = Math.Min(refSourceText.Lines.Count, refLine + 2);

            results.AppendLine("- **Code Context**:");
            results.AppendLine("```csharp");

            for (int i = startLine - 1; i < endLine; i++)
            {
                if (i < 0 || i >= refSourceText.Lines.Count) continue;
                var codeLine = refSourceText.Lines[i];
                var lineText = codeLine.ToString();
                var lineNumber = i + 1;

                results.AppendLine(lineNumber == refLine
                    ? $"{lineNumber,3}: > {lineText}"
                    : $"{lineNumber,3}:   {lineText}");
            }

            results.AppendLine("```");
        }
        catch (Exception ex)
        {
            results.AppendLine($"- **Code Context**: Unable to retrieve code context. Error: {ex.Message}");
        }
    }

    private static void BuildReportSummary(StringBuilder results, ISymbol symbol,
        IEnumerable<ReferencedSymbol> references, bool isSolutionWide)
    {
        var totalReferences = references.Sum(r => r.Locations.Count());
        var projectCount = references.SelectMany(r => r.Locations).Select(l => l.Document.Project.Name).Distinct()
            .Count();

        results.AppendLine("## Summary");
        results.AppendLine(
            $"Symbol `{symbol.Name}` of type `{symbol.Kind}` has {totalReferences} references across {projectCount} projects{(isSolutionWide ? " in the solution" : "")}.");

        if (isSolutionWide)
        {
            results.AppendLine();
            results.AppendLine("### References by Project:");
            var referencesByProject = references
                .SelectMany(r => r.Locations.Select(loc => new { Reference = r, Location = loc }))
                .GroupBy(x => x.Location.Document.Project.Name)
                .OrderBy(g => g.Key);

            foreach (var projectGroup in referencesByProject)
            {
                results.AppendLine($"- **{projectGroup.Key}**: {projectGroup.Count()} references");
            }
        }
    }

    /// <summary>
    /// Finds the solution file (.sln) that contains the given project. It searches up the directory tree.
    /// If multiple solutions in a directory contain the project, it returns the first one found.
    /// </summary>
    private static async Task<string> FindContainingSolutionAsync(string projectPath)
    {
        try
        {
            var projectFileName = Path.GetFileName(projectPath);
            var directory = new FileInfo(projectPath).Directory;

            while (directory != null)
            {
                var solutionFiles = directory.GetFiles("*.sln");
                // Check all solution files in the current directory before going up
                foreach (var slnFile in solutionFiles)
                {
                    var solutionContent = await File.ReadAllTextAsync(slnFile.FullName);
                    // Check if the solution file's content mentions the project file name.
                    // This is a robust way to check for project inclusion.
                    if (solutionContent.Contains($"\"{projectFileName}\""))
                    {
                        await Console.Error.WriteLineAsync(
                            $"Found project '{projectFileName}' in solution '{slnFile.FullName}'.");
                        return slnFile.FullName;
                    }
                }

                directory = directory.Parent;
            }

            await Console.Error.WriteLineAsync(
                $"Could not find any solution file containing the project '{projectFileName}'.");
            return null;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Error finding containing solution: {ex.Message}");
            return null;
        }
    }
}

[McpServerToolType]
public static class EchoTool
{
    [McpServerTool, Description("Echoes the message back to the client.")]
    public static string Echo(string message) => $"hello {message}";
}