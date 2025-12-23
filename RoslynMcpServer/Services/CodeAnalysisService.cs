using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RoslynMcpServer.Services
{
    public class CodeAnalysisService
    {
        private readonly ILogger<CodeAnalysisService> _logger;
        private readonly ConcurrentDictionary<string, Solution> _solutionCache;
        private MSBuildWorkspace? _workspace;
        private readonly object _workspaceLock = new object();

        public CodeAnalysisService(ILogger<CodeAnalysisService> logger)
        {
            _logger = logger;
            _solutionCache = new ConcurrentDictionary<string, Solution>();
            // Lazy initialization of workspace to avoid assembly loading issues
        }

        private MSBuildWorkspace GetOrCreateWorkspace()
        {
            if (_workspace != null)
                return _workspace;

            lock (_workspaceLock)
            {
                if (_workspace != null)
                    return _workspace;

                try
                {
                    // Create workspace with properties that help with .NET 9.0 compatibility
                    _workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
                    {
                        { "Configuration", "Debug" },
                        { "Platform", "AnyCPU" }
                    });
                    _logger.LogInformation("MSBuildWorkspace created successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create MSBuildWorkspace with properties, retrying with default settings");
                    try
                    {
                        _workspace = MSBuildWorkspace.Create();
                        _logger.LogInformation("MSBuildWorkspace created with default settings");
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "Failed to create MSBuildWorkspace even with default settings");
                        throw new InvalidOperationException("Unable to create MSBuildWorkspace. Ensure MSBuild is properly registered.", ex2);
                    }
                }

                return _workspace;
            }
        }

        public async Task<Solution> GetSolutionAsync(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath))
                throw new ArgumentException("Solution path cannot be null or empty", nameof(solutionPath));

            var normalizedPath = Path.GetFullPath(solutionPath);

            if (_solutionCache.TryGetValue(normalizedPath, out var cachedSolution))
            {
                // Check if solution file was modified
                var fileInfo = new FileInfo(normalizedPath);
                if (fileInfo.Exists && fileInfo.LastWriteTime <= DateTime.UtcNow.AddMinutes(-5))
                {
                    return cachedSolution;
                }
            }

            try
            {
                _logger.LogInformation("Loading solution: {SolutionPath}", normalizedPath);
                var workspace = GetOrCreateWorkspace();
                var solution = await workspace.OpenSolutionAsync(normalizedPath);
                _solutionCache[normalizedPath] = solution;
                _logger.LogInformation("Solution loaded successfully: {ProjectCount} projects", solution.Projects.Count());
                return solution;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load solution: {SolutionPath}", normalizedPath);
                throw;
            }
        }

        public async Task<DependencyAnalysis> AnalyzeDependenciesAsync(string solutionPath, int maxDepth = 3)
        {
            var solution = await GetSolutionAsync(solutionPath);
            var analysis = new DependencyAnalysis
            {
                ProjectName = Path.GetFileNameWithoutExtension(solutionPath),
                Dependencies = new List<ProjectDependency>(),
                NamespaceUsages = new List<NamespaceUsage>(),
                TotalSymbols = 0,
                PublicSymbols = 0,
                InternalSymbols = 0
            };

            var namespaceUsageDict = new Dictionary<string, NamespaceUsage>();
            var dependencyDict = new Dictionary<string, ProjectDependency>();

            foreach (var project in solution.Projects.Where(p => p.SupportsCompilation))
            {
                try
                {
                    var compilation = await project.GetCompilationAsync();
                    if (compilation == null) continue;

                    // Count symbols
                    var allSymbols = GetAllSymbols(compilation.GlobalNamespace).ToList();
                    analysis.TotalSymbols += allSymbols.Count;
                    analysis.PublicSymbols += allSymbols.Count(s => s.DeclaredAccessibility == Accessibility.Public);
                    analysis.InternalSymbols += allSymbols.Count(s => s.DeclaredAccessibility == Accessibility.Internal);

                    // Analyze project references
                    foreach (var reference in project.ProjectReferences)
                    {
                        var referencedProject = solution.GetProject(reference.ProjectId);
                        if (referencedProject != null)
                        {
                            var key = referencedProject.Name;
                            if (!dependencyDict.ContainsKey(key))
                            {
                                dependencyDict[key] = new ProjectDependency
                                {
                                    Name = referencedProject.Name,
                                    Type = "ProjectReference",
                                    Version = "",
                                    UsageCount = 0
                                };
                            }
                            dependencyDict[key].UsageCount++;
                        }
                    }

                    // Analyze metadata references (NuGet packages, etc.)
                    foreach (var metadataRef in project.MetadataReferences)
                    {
                        if (metadataRef is PortableExecutableReference portableRef)
                        {
                            var assemblyName = Path.GetFileNameWithoutExtension(portableRef.FilePath ?? "");
                            if (!string.IsNullOrEmpty(assemblyName) && !dependencyDict.ContainsKey(assemblyName))
                            {
                                dependencyDict[assemblyName] = new ProjectDependency
                                {
                                    Name = assemblyName,
                                    Type = "AssemblyReference",
                                    Version = "",
                                    UsageCount = 1
                                };
                            }
                        }
                    }

                    // Analyze namespace usage
                    foreach (var symbol in allSymbols)
                    {
                        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
                        if (!string.IsNullOrEmpty(ns) && !ns.StartsWith("System.") && !ns.StartsWith("Microsoft."))
                        {
                            if (!namespaceUsageDict.ContainsKey(ns))
                            {
                                namespaceUsageDict[ns] = new NamespaceUsage
                                {
                                    Namespace = ns,
                                    UsageCount = 0,
                                    UsedTypes = new List<string>()
                                };
                            }
                            namespaceUsageDict[ns].UsageCount++;
                            var typeName = symbol.ToDisplayString();
                            if (!namespaceUsageDict[ns].UsedTypes.Contains(typeName))
                            {
                                namespaceUsageDict[ns].UsedTypes.Add(typeName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error analyzing project: {ProjectName}", project.Name);
                }
            }

            analysis.Dependencies = dependencyDict.Values.ToList();
            analysis.NamespaceUsages = namespaceUsageDict.Values.ToList();

            return analysis;
        }

        private IEnumerable<ISymbol> GetAllSymbols(INamespaceSymbol namespaceSymbol)
        {
            foreach (var member in namespaceSymbol.GetMembers())
            {
                yield return member;

                if (member is INamespaceSymbol nestedNamespace)
                {
                    foreach (var nested in GetAllSymbols(nestedNamespace))
                        yield return nested;
                }
                else if (member is INamedTypeSymbol namedType)
                {
                    foreach (var typeMember in namedType.GetMembers())
                        yield return typeMember;
                }
            }
        }
    }
}

