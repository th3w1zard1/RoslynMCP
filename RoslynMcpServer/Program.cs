using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Microsoft.Build.Locator;
using RoslynMcpServer.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoslynMcpServer
{
    class Program
    {
        static void RegisterMSBuild(ILogger logger)
        {
            if (MSBuildLocator.IsRegistered)
            {
                logger.LogInformation("MSBuild already registered");
                return;
            }

            try
            {
                // Try RegisterDefaults first (fastest path)
                MSBuildLocator.RegisterDefaults();
                logger.LogInformation("MSBuild registered using RegisterDefaults");
                return;
            }
            catch (InvalidOperationException)
            {
                // RegisterDefaults failed, try manual detection
                logger.LogWarning("RegisterDefaults failed, attempting manual MSBuild detection");
            }

            // Query all available Visual Studio instances
            var instances = MSBuildLocator.QueryVisualStudioInstances()
                .OrderByDescending(i => i.Version)
                .ToList();

            if (instances.Count > 0)
            {
                var instance = instances[0];
                MSBuildLocator.RegisterInstance(instance);
                logger.LogInformation("MSBuild registered from Visual Studio instance: {Path} (Version: {Version})",
                    instance.MSBuildPath, instance.Version);
                return;
            }

            // Try to find MSBuild in .NET SDK
            var dotnetPath = FindDotNetPath();
            if (!string.IsNullOrEmpty(dotnetPath))
            {
                var sdkPath = FindLatestSdkPath(dotnetPath);
                if (!string.IsNullOrEmpty(sdkPath))
                {
                    var msbuildPath = Path.Combine(sdkPath, "MSBuild.dll");
                    if (File.Exists(msbuildPath))
                    {
                        MSBuildLocator.RegisterMSBuildPath(Path.GetDirectoryName(msbuildPath));
                        logger.LogInformation("MSBuild registered from .NET SDK: {Path}", msbuildPath);
                        return;
                    }
                }
            }

            // Try common Visual Studio Build Tools paths
            var buildToolsPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "2022", "BuildTools", "MSBuild", "Current", "Bin", "MSBuild.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "2022", "Community", "MSBuild", "Current", "Bin", "MSBuild.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "2022", "Professional", "MSBuild", "Current", "Bin", "MSBuild.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "2022", "Enterprise", "MSBuild", "Current", "Bin", "MSBuild.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "BuildTools", "MSBuild", "Current", "Bin", "MSBuild.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "Community", "MSBuild", "Current", "Bin", "MSBuild.dll"),
            };

            foreach (var msbuildPath in buildToolsPaths)
            {
                if (File.Exists(msbuildPath))
                {
                    var msbuildDir = Path.GetDirectoryName(msbuildPath);
                    MSBuildLocator.RegisterMSBuildPath(msbuildDir);
                    logger.LogInformation("MSBuild registered from Visual Studio Build Tools: {Path}", msbuildPath);
                    return;
                }
            }

            // Last resort: try to use dotnet msbuild command
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--info",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    // Parse SDK path from output
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Contains("Base Path:"))
                        {
                            var basePath = line.Split(':')[1].Trim();
                            var msbuildPath = Path.Combine(basePath, "MSBuild.dll");
                            if (File.Exists(msbuildPath))
                            {
                                MSBuildLocator.RegisterMSBuildPath(Path.GetDirectoryName(msbuildPath));
                                logger.LogInformation("MSBuild registered from .NET base path: {Path}", msbuildPath);
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to detect MSBuild from dotnet --info");
            }

            throw new InvalidOperationException(
                "Could not locate MSBuild. Please ensure one of the following is installed:\n" +
                "- Visual Studio 2022 (any edition)\n" +
                "- Visual Studio Build Tools 2022\n" +
                "- .NET SDK with MSBuild");
        }

        static string FindDotNetPath()
        {
            var dotnetExe = "dotnet";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                dotnetExe = "dotnet.exe";
            }

            // Check PATH
            var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
            foreach (var dir in pathDirs)
            {
                var fullPath = Path.Combine(dir, dotnetExe);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            // Check common installation paths
            var commonPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", dotnetExe),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", dotnetExe),
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        static string FindLatestSdkPath(string dotnetPath)
        {
            var dotnetDir = Path.GetDirectoryName(dotnetPath);
            var sdkDir = Path.Combine(dotnetDir, "sdk");

            if (!Directory.Exists(sdkDir))
            {
                return null;
            }

            var sdkVersions = Directory.GetDirectories(sdkDir)
                .Select(d => new { Path = d, Version = Path.GetFileName(d) })
                .Where(x => Version.TryParse(x.Version, out _))
                .OrderByDescending(x => Version.Parse(x.Version))
                .ToList();

            return sdkVersions.FirstOrDefault()?.Path;
        }

        static async Task Main(string[] args)
        {
            // Create a temporary logger for early initialization
            using var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Information));
            var tempLogger = loggerFactory.CreateLogger<Program>();

            // Register MSBuild before any workspace operations
            // This is required for Roslyn to find MSBuild
            try
            {
                RegisterMSBuild(tempLogger);
            }
            catch (Exception ex)
            {
                tempLogger.LogError(ex, "Failed to register MSBuild: {Message}", ex.Message);
                Environment.Exit(1);
            }

            var builder = Host.CreateApplicationBuilder(args);

            // Configure logging for MCP integration - ensure all logs go to stderr
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace; // All logs to stderr
            });

            // Register services
            builder.Services.AddSingleton<CodeAnalysisService>();
            builder.Services.AddSingleton<SymbolSearchService>();
            builder.Services.AddSingleton<SecurityValidator>();
            builder.Services.AddSingleton<DiagnosticLogger>();
            builder.Services.AddSingleton<IncrementalAnalyzer>();
            builder.Services.AddSingleton<IPersistentCache, FilePersistentCache>();
            builder.Services.AddSingleton<MultiLevelCacheManager>();
            builder.Services.AddMemoryCache();

            // Configure MCP server
            try
            {
                builder.Services
                    .AddMcpServer()
                    .WithStdioServerTransport()
                    .WithToolsFromAssembly();

                var host = builder.Build();

                var logger = host.Services.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("Starting Roslyn MCP Server...");

                await host.RunAsync();
            }
            catch (Exception ex)
            {
                tempLogger.LogError(ex, "Failed to start MCP server: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }
    }
}
