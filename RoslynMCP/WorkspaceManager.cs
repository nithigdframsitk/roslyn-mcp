using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMCP;

/// <summary>
/// Manages a single instance of MSBuildWorkspace and caches loaded solutions and projects.
/// This prevents redundant work when analyzing multiple files in the same solution.
/// </summary>
public static class WorkspaceManager
{
    private static readonly MSBuildWorkspace _workspace;
    private static readonly ConcurrentDictionary<string, Solution> _solutionCache = new();
    private static readonly ConcurrentDictionary<string, Project> _projectCache = new();

    static WorkspaceManager()
    {
        var properties = new Dictionary<string, string>
        {
            { "AlwaysUseNETSdkDefaults", "true" },
            { "DesignTimeBuild", "true" }
        };

        _workspace = MSBuildWorkspace.Create(properties);
        Program.EnsureCSharpLanguageServicesRegistered(_workspace);

        _workspace.WorkspaceFailed += (sender, args) =>
        {
            Console.Error.WriteLine($"[WorkspaceManager] Workspace warning: {args.Diagnostic.Message}");
        };
    }

    public static MSBuildWorkspace GetWorkspace() => _workspace;

    /// <summary>
    /// Gets a loaded Solution from the cache or loads it into the cache.
    /// </summary>
    public static async Task<Solution> GetOrLoadSolutionAsync(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
            return null;

        // Normalize the path to use as a consistent cache key
        var normalizedPath = Path.GetFullPath(solutionPath);

        if (_solutionCache.TryGetValue(normalizedPath, out var cachedSolution))
        {
            return cachedSolution;
        }

        await Console.Error.WriteLineAsync($"[WorkspaceManager] Caching solution: {normalizedPath}");
        var solution = await _workspace.OpenSolutionAsync(normalizedPath);
        _solutionCache[normalizedPath] = solution;
        return solution;
    }

    /// <summary>
    /// Gets a loaded Project from the cache or loads it into the cache.
    /// </summary>
    public static async Task<Project> GetOrLoadProjectAsync(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return null;

        // Normalize the path to use as a consistent cache key
        var normalizedPath = Path.GetFullPath(projectPath);

        if (_projectCache.TryGetValue(normalizedPath, out var cachedProject))
        {
            return cachedProject;
        }

        await Console.Error.WriteLineAsync($"[WorkspaceManager] Caching project: {normalizedPath}");
        var project = await _workspace.OpenProjectAsync(normalizedPath);
        _projectCache[normalizedPath] = project;
        return project;
    }
}