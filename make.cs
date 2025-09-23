#:property Version 0.0.1
#:package NuGet.Packaging 6.14.0
#:package System.CommandLine 2.0.0-rc.1.25451.107

using NuGet.Packaging;
using System.Collections.Immutable;
using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

// ===== Configuration =====
const string DefaultConfigFileName = "make.json",  DefaultProjectPath = "./src",             
             DefaultCacheFileName  = "cache.json", DefaultNugetSource = "https://api.nuget.org/v3/index.json";

// ===== Globally shared HttpClient =====
using var http = new HttpClient();

// ===== Shared CLI options and arguments =====
var projectOption   = new Option<FileSystemInfo?>("--project")       { Description = "Path to a .csproj file or a directory containing one. If a directory is given, the first .csproj inside it will be used.", Arity = ArgumentArity.ExactlyOne };
var configOption    = new Option<string?>        ("--configuration") { Description = "Build configuration to use (e.g. Debug or Release).",                                                                      Arity = ArgumentArity.ExactlyOne };
var defineOption    = new Option<string[]?>      ("--define")        { Description = "One or more preprocessor symbols to define (semicolon or comma separated).",                                               Arity = ArgumentArity.ZeroOrMore };
var noRestoreOption = new Option<bool?>          ("--no-restore")    { Description = "Skip the restore phase when building or packing." };
var propertyOption  = new Option<string[]?>      ("--property")      { Description = "Additional MSBuild properties in the form name=value.",                                                                               Arity = ArgumentArity.ZeroOrMore };
var verboseOption   = new Option<bool?>          ("--verbose")       { Description = "Enable verbose logging with detailed output." };
var noLogoOption    = new Option<bool?>          ("--no-logo")       { Description = "Suppress the startup logo." };

var configPathArgument = new Argument<FileSystemInfo?>("CONFIG_PATH")
{
    HelpName = "CONFIG_PATH",
    Description = $"Optional path to a configuration file or a directory containing one. If omitted, the tool looks for '{DefaultConfigFileName}' in the current directory.",
    Arity = ArgumentArity.ZeroOrOne
};

// Root command
var rootCommand = new RootCommand($"Build and package tool for managed projects + native runtimes");

// ===== build command =====
var buildCommand = new Command("build", "Build the managed project")
{
    projectOption,  configOption,
    defineOption,   noRestoreOption,
    propertyOption, verboseOption,
    noLogoOption,
    configPathArgument
};
buildCommand.SetAction(GlobalSetupAsync(HandleBuildAsync));
rootCommand.Add(buildCommand);

// ===== clean command =====
var outputDirOption = new Option<string?>("--output-dir") { Description = "Directory where build or pack outputs will be placed.",         Arity = ArgumentArity.ExactlyOne };
var cacheDirOption  = new Option<string?>("--cache-dir")  { Description = "Directory to store cached downloads (e.g. runtimes archives).", Arity = ArgumentArity.ExactlyOne }; 
var tempDirOption   = new Option<string?>("--temp-dir")   { Description = "Temporary working directory used during packing.",              Arity = ArgumentArity.ExactlyOne };

var cleanCommand = new Command("clean", "Clean temp, cache, and output directories")
{
    projectOption,   configOption,
    noRestoreOption, propertyOption,
    outputDirOption, cacheDirOption,
    tempDirOption,   verboseOption,
    noLogoOption,
    configPathArgument
};
cleanCommand.SetAction(GlobalSetupAsync(HandleCleanAsync));
rootCommand.Add(cleanCommand);

// ===== pack command =====
var runtimesVersionOption            = new Option<string?>  ("--runtimes-version")               { Description = "Version of the runtimes package to download and include in RID-specific packages.",                                                          Arity = ArgumentArity.ExactlyOne };
var runtimesUrlOption                = new Option<string?>  ("--runtimes-url")                   { Description = "URL or format string for the runtimes archive. Use '{0}' as a placeholder for the version.",                                                 Arity = ArgumentArity.ExactlyOne };
var runtimesLicenseSpdxOption        = new Option<string?>  ("--runtimes-license-spdx")          { Description = "SPDX license expression to apply to RID packages (e.g. MIT, Apache-2.0).",                                                                   Arity = ArgumentArity.ExactlyOne };
var runtimesLicenseFileUrlOption     = new Option<string?>  ("--runtimes-license-file-url")      { Description = "URL or format string to a license file to include in RID packages. If no SPDX is set, also used as PackageLicenseFile.",                     Arity = ArgumentArity.ExactlyOne };
var runtimesLicenseSpdxFileUrlOption = new Option<string?>  ("--runtimes-license-spdx-file-url") { Description = "URL or format string to a text file containing an SPDX identifier. Used as PackageLicenseExpression if --runtimes-license-spdx is not set.", Arity = ArgumentArity.ExactlyOne };
var forceRuntimesDownloadOption      = new Option<bool?>    ("--force-runtimes-download")        { Description = "Force re-download of runtimes archive even if a cached version exists." };
var targetsOption                    = new Option<string[]?>("--targets")                        { Description = "List of targets to pack. Use 'all' for all, 'core' for the main package, 'meta' for the meta package, or specify RIDs.",                     Arity = ArgumentArity.ZeroOrMore };
var strictOption                     = new Option<bool?>    ("--strict")                         { Description = "Fail if a requested RID has no native binary instead of warning." };
var noSymbolsOption                  = new Option<bool?>    ("--no-symbols")                     { Description = "Do not create a symbols package for the core project." };

var packCommand = new Command("pack", "Package NuGet artifacts")
{
    runtimesVersionOption,        runtimesUrlOption,
    forceRuntimesDownloadOption,  runtimesLicenseSpdxOption,
    runtimesLicenseFileUrlOption, runtimesLicenseSpdxFileUrlOption,
    targetsOption,                strictOption,
    projectOption,                configOption,
    defineOption,                 noSymbolsOption,
    noRestoreOption,              propertyOption,
    outputDirOption,              cacheDirOption,
    tempDirOption,                verboseOption,
    noLogoOption,
    configPathArgument
};
packCommand.SetAction(GlobalSetupAsync(HandlePackAsync));
rootCommand.Add(packCommand);

// ===== push command =====
var nugetSourceOption = new Option<string?>("--nuget-source") { Description = $"NuGet feed URL. Defaults to '{DefaultNugetSource}'.",  Arity = ArgumentArity.ExactlyOne };
var apiKeyOption      = new Option<string> ("--api-key")      { Description = "API key for the NuGet feed.",                           Arity = ArgumentArity.ExactlyOne,   Required = true };
var noPackOption      = new Option<bool?>  ("--no-pack")      { Description = "Do not 'pack' even if cache is stale." };
var failStaleOption   = new Option<bool?>  ("--fail-stale")   { Description = "Exit with error if cache is stale instead of packing." };

var pushCommand = new Command("push", "Push NuGet packages to a feed")
{
    nugetSourceOption,            apiKeyOption,
    noPackOption,                 failStaleOption,
    runtimesVersionOption,        runtimesUrlOption,
    forceRuntimesDownloadOption,  runtimesLicenseSpdxOption,
    runtimesLicenseFileUrlOption, runtimesLicenseSpdxFileUrlOption,
    targetsOption,                strictOption,
    projectOption,                configOption,
    defineOption,                 noSymbolsOption,
    noRestoreOption,              propertyOption,
    outputDirOption,              cacheDirOption,
    tempDirOption,                verboseOption,
    noLogoOption,
    configPathArgument
};
pushCommand.SetAction(GlobalSetupAsync(HandlePushAsync));
rootCommand.Add(pushCommand);

// ===== Invoke =====
return await rootCommand.Parse(args).InvokeAsync();

// ===== Global setup =====
Func<ParseResult, CancellationToken, Task<int>> GlobalSetupAsync(HandlerAsync continuationAsync) => async (parserResult, cancellationToken) =>
{
    const string projectPropertyName = "project", noLogoPropertyName  = "noLogo";

    var logger = new Logger(parserResult.InvocationConfiguration.Output, parserResult.InvocationConfiguration.Error, IsVerbose: parserResult.GetValue(verboseOption) ?? false);

    FileInfo? configFile;
    switch (parserResult.GetValue(configPathArgument))
    {
        case FileInfo { Exists: true } fileInfo: { configFile = fileInfo; break; }
        case DirectoryInfo { Exists: true, FullName: var fullName }:
        {
            if (Path.Combine(fullName, DefaultConfigFileName) is var candidate && File.Exists(candidate)) { configFile = new(candidate); break; }
            return await logger.FailAsync($"No configuration file named '{DefaultConfigFileName}' found in directory '{fullName}'.", cancellationToken);
        }
        case { Exists: false, FullName: var fullName }: { return await logger.FailAsync($"The specified configuration path '{fullName}' does not exist.", cancellationToken); }
        default:
        { 
            if (Path.Combine(Environment.CurrentDirectory, DefaultConfigFileName) is var candidate && File.Exists(candidate)) { configFile = new(candidate); break; }
            configFile = null; break;
        }
    }

    JsonDocument? jsonDocument = null;
    if (configFile is not null)
    {
        try
        {
            await using var stream = configFile.OpenRead();
            jsonDocument = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException) { return await logger.FailAsync($"The configuration file '{configFile.FullName}' contains invalid JSON.", cancellationToken); }
        catch (IOException e) { return await logger.FailAsync($"Failed to read configuration file '{configFile.FullName}': [{e.GetType().Name}]: {e.Message}", cancellationToken); }
    }

    try
    {
        var options = new Options(parserResult, jsonDocument);

        FileInfo projectFile;
        switch (options.GetFileSystemInfo(projectOption, projectPropertyName))
        {
            case FileInfo { Exists: true } fileInfo: { projectFile = fileInfo; break; }
            case DirectoryInfo { Exists: true, FullName: var fullName } dirInfo:
            {
                if (dirInfo.EnumerateFiles("*.csproj").FirstOrDefault() is { } fileInfo) { projectFile = fileInfo; break; }
                return await logger.FailAsync($"No project file (*.csproj) found in directory '{fullName}'.", cancellationToken);
            }
            case { Exists: false, FullName: var fullName }: { return await logger.FailAsync($"The specified project path '{fullName}' does not exist.", cancellationToken); }
            default:
            {
                if (Path.Combine(Environment.CurrentDirectory, DefaultProjectPath) is var x && Directory.Exists(x) && new DirectoryInfo(x).EnumerateFiles("*.csproj").FirstOrDefault() is { } fileInfo) { projectFile = fileInfo; break; }
                return await logger.FailAsync($"No project file could be resolved. Provide {projectOption.Name}, set '{projectPropertyName}' in the config, or place a .csproj in '{DefaultProjectPath}'.", cancellationToken);
            }
        }

        var noLogo = options.GetBoolean(noLogoOption, noLogoPropertyName, false);
        if (!noLogo) { await PrintLogoAsync(logger.Out, cancellationToken); }

        return await continuationAsync(logger, options, projectFile, noLogo, cancellationToken);
    }
    finally
    {
        jsonDocument?.Dispose();
    }
};

// ===== Handlers =====
async Task<int> HandleBuildAsync(Logger logger, Options options, FileInfo projectFile, bool noLogo, CancellationToken cancellationToken)
{
    var config     = options.ParseResult.GetValue(configOption)    ?? "Debug";
    var defines    = options.ParseResult.GetValue(defineOption)    ?? [];
    var noRestore  = options.ParseResult.GetValue(noRestoreOption) ?? false;
    var properties = options.ParseResult.GetValue(propertyOption)  ?? [];

    await logger.OutputVerboseAsync(() => $"Configuration: {config}, NoRestore: {noRestore}", cancellationToken);
    await logger.OutputAsync($"Building project ({config})...", cancellationToken);
    await logger.OutputVerboseAsync(() => $"Project file: '{projectFile.FullName}'", cancellationToken);

    // Run dotnet build
    await logger.OutputDotnetCliAsync("build", [
        projectFile.FullName,
        defines.Length is > 0 ? $"/p:DefineConstants={string.Join(";", defines)}" : null
    ], config, noRestore, noLogo, properties, cancellationToken);

    var exit = await RunDotnetAsync("build", [
        projectFile.FullName,
        defines.Length is > 0 ? $"/p:DefineConstants={string.Join(";", defines)}" : null
    ], config, noRestore, noLogo, properties, logger.Out, logger.Error, cancellationToken);

    await logger.OutputDotnetFinishedAsync("build", exit, cancellationToken);

    return exit;
}

string GetOutputDir(Options options) => options.GetString(outputDirOption, "outputDir", "./output");
string GetCacheDir (Options options) => options.GetString(cacheDirOption,  "cacheDir",  "./cache");
string GetTempDir  (Options options) => options.GetString(tempDirOption,   "tempDir",   "./temp");

async Task<int> HandleCleanAsync(Logger logger, Options options, FileInfo projectFile, bool noLogo, CancellationToken cancellationToken)
{
    var config     = options.ParseResult.GetValue(configOption);
    var noRestore  = options.ParseResult.GetValue(noRestoreOption) ?? false;
    var properties = options.ParseResult.GetValue(propertyOption)  ?? [];
    var outputDir  = GetOutputDir(options);
    var cacheDir   = GetCacheDir(options);
    var tempDir    = GetTempDir(options);
    
    await logger.OutputAsync($"Cleaning...", cancellationToken);
    await logger.OutputVerboseAsync(() => $"Project file: '{projectFile.FullName}'", cancellationToken);

    // Run dotnet clean
    await logger.OutputDotnetCliAsync("clean", [
        projectFile.FullName
    ], config, noRestore, noLogo, properties, cancellationToken);

    var exit = await RunDotnetAsync("clean", [
        projectFile.FullName
    ], config, noRestore, noLogo, properties, logger.Out, logger.Error, cancellationToken);

    await logger.OutputDotnetFinishedAsync("clean", exit, cancellationToken);

    // Remove our own output/cache/temp dirs
    exit = await deleteDir(outputDir, exit);
    exit = await deleteDir(cacheDir, exit);
    exit = await deleteDir(tempDir, exit);

    return exit;

    async Task<int> deleteDir(string dir, int exit)
    {
        if (Directory.Exists(dir))
        {
            try
            {
                await logger.OutputVerboseAsync(() => $" Attempting to delete directory: '{dir}'", cancellationToken);
                Directory.Delete(dir, true);
                await logger.OutputAsync($"Successfully deleted '{dir}'", cancellationToken);
            }
            catch (Exception e) { exit = await logger.FailAsync($"Failed to delete '{dir}': [{e.GetType().Name}]: {e.Message}", cancellationToken); }
        }
        return exit;
    }
}

#pragma warning disable CS0162 // Why is this still a thing for local constants which are part of a top-level statement program? They're most certainly not 'unreachable' and even more certainly not 'code'
const string RuntimesVersionPropertyName = "runtimesVersion",
             RuntimesUrlPropertyName     = "runtimesUrl";
#pragma warning restore CS0162 

string?  GetRuntimesVersion(Options options) => options.GetString(runtimesVersionOption, RuntimesVersionPropertyName);
string?  GetRuntimesUrl    (Options options) => options.GetString(runtimesUrlOption,     RuntimesUrlPropertyName);

async Task<int> HandlePackAsync(Logger logger, Options options, FileInfo projectFile, bool noLogo, CancellationToken cancellationToken)
{
    var runtimesVersion            = GetRuntimesVersion(options);
    var runtimesUrl                = GetRuntimesUrl(options);
    var forceRuntimesDownload      = options.ParseResult.GetValue(forceRuntimesDownloadOption) ?? false;
    var runtimesLicenseSpdx        = options.GetString(runtimesLicenseSpdxOption,        "runtimesLicenseSpdx");
    var runtimesLicenseFileUrl     = options.GetString(runtimesLicenseFileUrlOption,     "runtimesLicenseFileUrl");
    var runtimesLicenseSpdxFileUrl = options.GetString(runtimesLicenseSpdxFileUrlOption, "runtimesLicenseSpdxFileUrl");
    var targets                    = options.ParseResult.GetValue(targetsOption)               ?? [];
    var strict                     = options.ParseResult.GetValue(strictOption)                ?? false;
    var config                     = options.ParseResult.GetValue(configOption)                ?? "Release";
    var defines                    = options.ParseResult.GetValue(defineOption)                ?? [];
    var noSymbols                  = options.ParseResult.GetValue(noSymbolsOption)             ?? false;
    var noRestore                  = options.ParseResult.GetValue(noRestoreOption)             ?? false;
    var properties                 = options.ParseResult.GetValue(propertyOption)              ?? [];
    var outputDir                  = GetOutputDir(options);
    var cacheDir                   = GetCacheDir(options);
    var tempDir                    = GetTempDir(options);

    Directory.CreateDirectory(outputDir);
    Directory.CreateDirectory(cacheDir);

    if (Directory.Exists(tempDir)) { try { Directory.Delete(tempDir, true); } catch { } }
    Directory.CreateDirectory(tempDir);

    try
    {
        var cacheFile = new FileInfo(Path.Combine(cacheDir, DefaultCacheFileName));
        var outputDirDir = new DirectoryInfo(outputDir);

        if (cacheFile.Exists)
        {
            try { cacheFile.Delete(); }
            catch (Exception e) { return await logger.FailAsync($"Failed to delete '{cacheFile.FullName}': [{e.GetType().Name}]: {e.Message}", cancellationToken); }
        }

        foreach (var nupkg in outputDirDir.EnumerateFiles("*.nupkg"))
        {
            await logger.OutputVerboseAsync(() => $"Deleting '{nupkg.FullName}'.", cancellationToken);
            try { nupkg.Delete(); }
            catch (Exception e) { return await logger.FailAsync($"Failed to delete '{nupkg.FullName}': [{e.GetType().Name}]: {e.Message}", cancellationToken); }
        }

        foreach (ref var target in targets.AsSpan()) { target = target.NormalizeLower(); }

        var packAll = targets.Length is 0 || targets.Contains("all");

        await logger.OutputVerboseAsync(() => $"Targets: {(packAll ? "All" : $"{string.Join(", ", targets)} ({targets.Length})")}", cancellationToken);
        await logger.OutputVerboseAsync(() =>  $"Configuration: {config}, NoSymbols: {noSymbols}, NoRestore: {noRestore}, Strict: {strict}", cancellationToken);

        List<(string Flavor, FileInfo File, string Id, string Version)> packed = [];

        var packCore = packAll || targets.Contains("core");
        if (packCore)
        {
            await logger.OutputAsync($"Packing Core package...", cancellationToken);
            await logger.OutputVerboseAsync(() => $"Project path: '{projectFile.FullName}'", cancellationToken);

            // Run dotnet pack
            await logger.OutputDotnetCliAsync("pack", [
                projectFile.FullName,
                "-o", outputDir,
                defines.Length is > 0 ? $"/p:DefineConstants={string.Join(";", defines)}" : null,
                !noSymbols ? "-p:IncludeSymbols=true" : null, !noSymbols ? "-p:SymbolPackageFormat=snupkg" : null,
            ], config, noRestore, noLogo, properties, cancellationToken);

            var exit = await RunDotnetAsync("pack", [
                projectFile.FullName,
                "-o", outputDir,
                defines.Length is > 0 ? $"/p:DefineConstants={string.Join(";", defines)}" : null,
                !noSymbols ? "-p:IncludeSymbols=true" : null, !noSymbols ? "-p:SymbolPackageFormat=snupkg" : null,
            ], config, noRestore, noLogo, properties, logger.Out, logger.Error, cancellationToken);

            await logger.OutputDotnetFinishedAsync("pack", exit, cancellationToken);

            if (exit is not 0) { return exit; }

            if (outputDirDir.EnumerateFiles("*.nupkg").Except(packed.Select(static p => p.File)).FirstOrDefault() is var file && file is null) { return await logger.FailAsync("Failed to find newly created nupkg file.", cancellationToken); }
            if (await file.GetNuGetPackageIdentityAsync(cancellationToken) is not var (id, version)) { return await logger.FailAsync($"Failed to get the identity of the newly created nupkg file '{file.FullName}'.", cancellationToken); }
            packed.Add(("core", file, id, version));

            await logger.OutputVerboseAsync(() => $"Core package successfully packed as '{file.FullName}'.", cancellationToken);
        }


        var packAnyRid = packAll || targets.Any(static t => t is not ("core" or "meta"));
        if (packAnyRid)
        {
            if (string.IsNullOrWhiteSpace(runtimesVersion)) { return await logger.FailAsync($"No runtimes version specified. Provide {runtimesVersionOption.Name} or set '{RuntimesVersionPropertyName}' in the config.", cancellationToken); }

            await logger.OutputAsync($"Using runtimes version: {runtimesVersion}", cancellationToken);

            var runtimesArchiveName = $"runtimes.{runtimesVersion}";
            var runtimesArchivePath = Path.Combine(cacheDir, runtimesArchiveName);
            string? runtimesLicenseFile = null;

            if (forceRuntimesDownload || !File.Exists(runtimesArchivePath))
            {
                if (!forceRuntimesDownload) { await logger.OutputVerboseAsync(() => $"No cached runtimes found under '{runtimesArchivePath}'.", cancellationToken); }
                
                if (string.IsNullOrWhiteSpace(runtimesUrl)) { return await logger.FailAsync($"No runtimes URL specified. Provide {runtimesUrlOption.Name} or set '{RuntimesUrlPropertyName}' in the config. It may be a format string containing '{{0}}' for the version.", cancellationToken); }
                if (!(string.Format(runtimesUrl, runtimesVersion) is var runtimesUriString
                    && Uri.TryCreate(runtimesUriString, UriKind.Absolute, out var runtimesUri))) { return await logger.FailAsync($"\"{runtimesUriString}\" is not a valid absolute URL.", cancellationToken); }
                 
                await logger.OutputVerboseAsync(() => $"Using runtimes URL: {runtimesUriString}", cancellationToken);
                await logger.OutputAsync("Downloading runtimes archive...", cancellationToken);     

                await using (var httpStream = await http.GetStreamAsync(runtimesUri, cancellationToken))
                await using (var fileStream = File.Create(runtimesArchivePath))
                { await httpStream.CopyToAsync(fileStream, cancellationToken); }

                await logger.OutputAsync("Download complete.", cancellationToken);
                await logger.OutputVerboseAsync(() => $"Saved runtimes archive to '{runtimesArchivePath}' ({new FileInfo(runtimesArchivePath).Length} bytes).", cancellationToken);

                if (runtimesLicenseFileUrl is not null)
                {
                    if (!(string.Format(runtimesLicenseFileUrl, runtimesVersion) is var licenseUriString
                        && Uri.TryCreate(licenseUriString, UriKind.Absolute, out var licenseUri))) { return await logger.FailAsync($"\"{licenseUriString}\" is not a valid absolute URL.", cancellationToken); }

                    runtimesLicenseFile = Path.Combine(cacheDir, Path.GetFileName(licenseUri.LocalPath) switch { var filename when !string.IsNullOrWhiteSpace(filename) => filename, _ => "LICENSE" });

                    await logger.OutputVerboseAsync(() => $"Using license file URL: {licenseUriString}", cancellationToken);
                    await logger.OutputAsync("Downloading license file...", cancellationToken);

                    await using (var httpStream = await http.GetStreamAsync(licenseUri, cancellationToken))
                    await using (var fileStream = File.Create(runtimesLicenseFile))
                    { await httpStream.CopyToAsync(fileStream, cancellationToken); }

                    await logger.OutputAsync("Download complete.", cancellationToken);
                    await logger.OutputVerboseAsync(() => $"Saved license file to '{runtimesLicenseFile}' ({new FileInfo(runtimesLicenseFile).Length} bytes).", cancellationToken);
                }

                if (runtimesLicenseSpdx is null && runtimesLicenseSpdxFileUrl is not null)
                {
                    if (!(string.Format(runtimesLicenseSpdxFileUrl, runtimesVersion) is var licenseSpdxUriString
                        && Uri.TryCreate(licenseSpdxUriString, UriKind.Absolute, out var licenseSpdxUri))) { return await logger.FailAsync($"\"{licenseSpdxUriString}\" is not a valid absolute URL.", cancellationToken); }

                    await logger.OutputVerboseAsync(() => $"Using license SPDX file URL: {licenseSpdxUriString}", cancellationToken);                    
                    await logger.OutputAsync("Downloading license SPDX file...", cancellationToken);

                    await using (var httpStream = await http.GetStreamAsync(licenseSpdxUri, cancellationToken))
                    using (var reader = new StreamReader(httpStream))
                    { runtimesLicenseSpdx = (await reader.ReadToEndAsync(cancellationToken)).Trim(); }

                    await logger.OutputAsync("Download complete.", cancellationToken);
                    await logger.OutputVerboseAsync(() => $"Read license SPDX identifier: {runtimesLicenseSpdx}.", cancellationToken);
                }
            }
            else { await logger.OutputVerboseAsync(() => $"Cached runtimes found under '{runtimesArchivePath}'.", cancellationToken); }

            if (runtimesLicenseFile is not null && runtimesLicenseSpdx is not null)
            { await logger.OutputVerboseAsync(() => "Warning: Runtimes license SPDX identifier set and runtimes license file set. The SPDX identifier will be used for the RID packages license. The license file will still be included in the RID packages.", cancellationToken); }

            var runtimesExtractPath = Path.Combine(tempDir, runtimesArchiveName);

            try { ZipFile.ExtractToDirectory(runtimesArchivePath, runtimesExtractPath, overwriteFiles: true); }
            catch (Exception e) { return await logger.FailAsync($"Failed to extract runtimes archive '{runtimesArchivePath}' to '{runtimesExtractPath}': [{e.GetType().Name}]: {e.Message}", cancellationToken); }

            await logger.OutputVerboseAsync(() => $"Extracted runtimes to '{runtimesExtractPath}'.", cancellationToken);

            var runtimesPath = Path.Combine(runtimesExtractPath, "runtimes");

            string[] availableRids = Directory.Exists(runtimesPath)
                ? [..Directory.GetDirectories(runtimesPath)
                    .Select(static p => Path.GetFileName(p).NormalizeLower())
                    .Where(static r => !string.IsNullOrEmpty(r))]
                : [];

            await logger.OutputVerboseAsync(() => $"Available RIDs: {(availableRids.Length is not > 0 ? "None" : $"{string.Join(", ", availableRids)} ({availableRids.Length})")}", cancellationToken);

            var ridsToPack = packAll
                ? availableRids
                : [.. availableRids.Where(r => targets.Contains(r))];

            foreach (var target in targets.Where(static t => t is not ("core" or "meta")))
            {
                if (!availableRids.Contains(target))
                {
                    var msg = $"Requested RID {target} not found in native runtime binaries.";
                    if (strict) { return await logger.FailAsync(msg, cancellationToken); }
                    else { await logger.ErrorAsync(msg, cancellationToken); }
                }
            }

            foreach (var rid in ridsToPack)
            {
                var nativePath = Path.Combine(runtimesPath, rid, "native");
                var nativeFile = Directory.Exists(nativePath)
                    ? Directory.GetFiles(nativePath).FirstOrDefault()
                    : null;

                if (nativeFile is null)
                {
                    var msg = $"Missing native binary for {rid}";
                    if (strict) { return await logger.FailAsync(msg, cancellationToken); }
                    else { await logger.ErrorAsync(msg, cancellationToken); }
                    await logger.ErrorVerboseAsync(() => $"Native binary path checked: '{nativePath}'", cancellationToken);

                    continue;
                }

                var ridProjPath = Path.Combine(tempDir, $"{rid}.csproj");
                await File.WriteAllTextAsync(ridProjPath,
                    $"""
                    <!-- Auto-generated by build.cs. Do not edit manually. -->
                    <Project Sdk="Microsoft.NET.Sdk">
                        <PropertyGroup>
                            <IsPackable>true</IsPackable>
                            <IncludeBuildOutput>false</IncludeBuildOutput>
                            <NoBuild>true</NoBuild>
                            <!-- Make flavor: rid -->
                            <!-- use Condition="'$(MakeFlavor)' == 'rid'" in your "Directory.Build.targets" -->
                            <MakeFlavor>rid</MakeFlavor>
                            <!-- Make flavor rid: {rid} -->
                            <!-- use Condition="'$(MakeFlavorRid)' == '{rid}'" in your "Directory.Build.targets" -->
                            <MakeFlavorRid>{rid}</MakeFlavorRid>
                            {(runtimesLicenseSpdx is not null ? $"<PackageLicenseExpression>{runtimesLicenseSpdx}</PackageLicenseExpression>" : string.Empty)}
                            {(runtimesLicenseSpdx is null && runtimesLicenseFile is not null ? $"<PackageLicenseFile>{Path.GetFileName(runtimesLicenseFile)}</PackageLicenseFile>" : string.Empty)}
                        </PropertyGroup>
                        <ItemGroup>
                            {(packCore ? $"<PackageReference Include=\"{packed[0].Id}\" Version=\"{packed[0].Version}\" PrivateAssets=\"all\" />" : string.Empty)}
                            <None Include="{Path.GetFullPath(nativeFile).Replace("\\", "/")}" Pack="true" PackagePath="runtimes/{rid}/native" />
                            {(runtimesLicenseFile is not null ? $"<None Include=\"{Path.GetFullPath(runtimesLicenseFile).Replace("\\", "/")}\" Pack=\"true\" PackagePath=\"{Path.GetFileName(runtimesLicenseFile)}\"/>" : string.Empty)}
                        </ItemGroup>
                    </Project>
                    """,
                    cancellationToken
                );

                await logger.OutputAsync($"Packing {rid} package...", cancellationToken);

                // Run dotnet pack
                await logger.OutputDotnetCliAsync("pack", [
                    ridProjPath,
                    "-o", outputDir,
                    "--no-build",
                ], config, noRestore, noLogo, properties, cancellationToken);

                var exit = await RunDotnetAsync("pack", [
                    ridProjPath,
                    "-o", outputDir,
                    "--no-build",
                ], config, noRestore, noLogo, properties, logger.Out, logger.Error, cancellationToken);

                await logger.OutputDotnetFinishedAsync("pack", exit, cancellationToken);

                if (exit is not 0) { return exit; }

                if (outputDirDir.EnumerateFiles("*.nupkg").Except(packed.Select(static p => p.File)).FirstOrDefault() is var file && file is null) { return await logger.FailAsync("Failed to find newly created nupkg file.", cancellationToken); }
                if (await file.GetNuGetPackageIdentityAsync(cancellationToken) is not var (id, version)) { return await logger.FailAsync($"Failed to get the identity of the newly created nupkg file '{file.FullName}'.", cancellationToken); }
                packed.Add((rid, file, id, version));

                await logger.OutputVerboseAsync(() => $"RID package {rid} successfully packed as '{file.FullName}'.", cancellationToken);
            }
        }

        var packMeta = packAll || targets.Contains("meta");
        if (packMeta)
        {
            await logger.OutputAsync("Packing Meta package...", cancellationToken);

            string[] deps;
            switch (packed.Count)
            {
                case <= 0: deps = []; await logger.ErrorAsync("Warning: Meta package will have no dependencies.", cancellationToken); break;
                case 1 when packCore: deps = [ $"<PackageReference Include=\"{packed[0].Id}\" Version=\"{packed[0].Version}\" PrivateAssets=\"all\" />" ]; break;
                default: deps = [ ..packed.Skip(packCore ? 1 : 0).Select(static p => $"<PackageReference Include=\"{p.Id}\" Version=\"{p.Version}\" PrivateAssets=\"all\" />") ]; break;
            }

            var metaProjPath = Path.Combine(tempDir, "meta.csproj");
            await File.WriteAllTextAsync(metaProjPath,
                $"""
                <!-- Auto-generated by build.cs. Do not edit manually. -->
                <Project Sdk="Microsoft.NET.Sdk">
                    <PropertyGroup>
                        <IsPackable>true</IsPackable>
                        <IncludeBuildOutput>false</IncludeBuildOutput>
                        <NoBuild>true</NoBuild>
                        <!-- Make flavor: meta -->
                        <!-- use Condition="'$(MakeFlavor)' == 'meta'" in your "Directory.Build.targets" -->
                        <MakeFlavor>meta</MakeFlavor>
                    </PropertyGroup>
                    <ItemGroup>
                        {string.Join("\n        ", deps)}
                    </ItemGroup>
                </Project>
                """,
                cancellationToken
            );

            // Run dotnet pack
            await logger.OutputDotnetCliAsync("pack", [
                metaProjPath,
                    "-o", outputDir,
                    "--no-build"
            ], config, noRestore, noLogo, properties, cancellationToken);

            var exit = await RunDotnetAsync("pack", [
                metaProjPath,
                    "-o", outputDir,
                    "--no-build"
            ], config, noRestore, noLogo, properties, logger.Out, logger.Error, cancellationToken);

            await logger.OutputDotnetFinishedAsync("pack", exit, cancellationToken);

            if (exit is not 0) return exit;

            if (outputDirDir.EnumerateFiles("*.nupkg").Except(packed.Select(static p => p.File)).FirstOrDefault() is var file && file is null) { return await logger.FailAsync("Failed to find newly created nupkg file.", cancellationToken); }
            if (await file.GetNuGetPackageIdentityAsync(cancellationToken) is not var (id, version)) { return await logger.FailAsync($"Failed to get the identity of the newly created nupkg file '{file.FullName}'.", cancellationToken); }
            packed.Add(("meta", file, id, version));

            await logger.OutputVerboseAsync(() => $"Meta package successfully packed as '{file.FullName}'.", cancellationToken);
        }

        var cache = new CacheData(
            Version: Assembly.GetAssembly(typeof(Program))!.GetName().Version!,
            Inputs: new(
                RuntimesVersion: runtimesVersion ?? string.Empty,
                RuntimesUrl:     runtimesUrl ?? string.Empty,
                Config:          config,
                NoSymbols:       noSymbols,
                Defines:         [..defines],
                Properties:      [..properties]
            ),
            Targets: packed.ToImmutableSortedDictionary(keySelector: static p => p.Flavor, elementSelector: static p => p.File.FullName)
        );

        try
        {
            await using var cacheFileStream = cacheFile.OpenWrite();            
            await JsonSerializer.SerializeAsync(cacheFileStream, cache, CacheDataSerializerContext.Default.CacheData, cancellationToken);
        }
        catch (Exception e) { return await logger.FailAsync($"Failed to serialize cache file to '{cacheFile.FullName}': [{e.GetType().Name}]: {e.Message}", cancellationToken); }

        await logger.OutputAsync("Packaging complete.", cancellationToken);
        await logger.OutputVerboseAsync(() => $"Packed: {string.Join(", ", packed)} ({packed.Count}), Output location: {Path.GetFullPath(outputDir)}", cancellationToken);

        return 0;
    }
    finally
    {
        if (Directory.Exists(tempDir)) { try { Directory.Delete(tempDir, true); } catch { } }
    }
}

async Task<int> HandlePushAsync(Logger logger, Options options, FileInfo projectFile, bool noLogo, CancellationToken cancellationToken)
{
    const int maxRetriesWithPack = 1;

    var nugetSource     = options.GetString(nugetSourceOption, "nugetSource", DefaultNugetSource);
    var apiKey          = options.ParseResult.GetRequiredValue(apiKeyOption);
    var noPack          = options.ParseResult.GetValue(noPackOption)          ?? false;
    var failStale       = options.ParseResult.GetValue(failStaleOption)       ?? false;
    var runtimesVersion = GetRuntimesVersion(options);
    var runtimesUrl     = GetRuntimesUrl(options);
    var targets         = options.ParseResult.GetValue(targetsOption)         ?? [];
    var config          = options.ParseResult.GetValue(configOption);
    var defines         = options.ParseResult.GetValue(defineOption);
    var noSymbols       = options.ParseResult.GetValue(noSymbolsOption);
    var properties      = options.ParseResult.GetValue(propertyOption);
    var outputDir       = GetOutputDir(options);
    var cacheDir        = GetCacheDir(options);

    await logger.OutputVerboseAsync(() => $"NuGet source: {nugetSource}", cancellationToken);
    await logger.OutputVerboseAsync(() => $"Targets: {(targets.Length is 0 || targets.Contains("all") ? "All" : $"{string.Join(", ", targets)} ({targets.Length})")}", cancellationToken);
    await logger.OutputVerboseAsync(() => $"NoPack: {noPack}, NoSymbols: {noSymbols switch { bool value => $"{value}", _ => "depends on cache" }}, FailStale: {failStale}", cancellationToken);    


    if (noPack && failStale) { return await logger.FailAsync($"Options '{noPackOption.Name}' and '{failStaleOption.Name}' cannot be used together.", cancellationToken); }       

    foreach(ref var target in targets.AsSpan()) { target = target.NormalizeLower(); }

    int exit;
    ImmutableSortedDictionary<string, string>? localTargets;
    bool?                                      localNoSymbols;

    var retry = 0;
    CheckCache:
    {
        localTargets = null;
        localNoSymbols = noSymbols;

        // check the cached inputs-targets-file exist
        var cacheFilePath = Path.Combine(cacheDir, DefaultCacheFileName);
        if (!File.Exists(cacheFilePath))
        {            
            await logger.ErrorAsync("Cache file not found — treating cache as stale.", cancellationToken);
            goto CacheStale;
        }

        CacheData cache;
        try
        {
            await using var cacheFileStream = File.OpenRead(cacheFilePath);
            cache = await JsonSerializer.DeserializeAsync(cacheFileStream, CacheDataSerializerContext.Default.CacheData, cancellationToken);
        }
        catch
        {
            await logger.ErrorAsync("Cache file invalid or corrupt — treating cache as stale.", cancellationToken);
            goto CacheStale;
        }

        var localCache = new CacheData(
            Version: Assembly.GetAssembly(typeof(Program))!.GetName().Version!,
            Inputs:  new(
                RuntimesVersion: runtimesVersion ?? cache.Inputs.RuntimesVersion,
                RuntimesUrl:     runtimesUrl     ?? cache.Inputs.RuntimesUrl,
                Config:          config          ?? cache.Inputs.Config,
                NoSymbols:       localNoSymbols ??= cache.Inputs.NoSymbols,
                Defines:         [..defines    ?? [..cache.Inputs.Defines]],
                Properties:      [..properties ?? [..cache.Inputs.Properties]]
            ),
            Targets: localTargets = (targets is [] || targets.Contains("all") ? targets.Where(target => target is not "all").Concat(cache.Targets.Keys).Distinct() : targets)
                .ToImmutableSortedDictionary(
                    keySelector: static rid => rid,
                    elementSelector: rid => cache.Targets.TryGetValue(rid, out var nupkg) ? Path.GetFullPath(nupkg) : string.Empty
                )
        );

        // check if the cache is up-to-date
        // the way it's done here is highly ineffecient, but it helps with two things:
        // - we don't have to deal with implementing a custom equality comparison for 'CacheData' and 'CacheData.InputsData' just because they happen to have collection reference types as their members
        // - even though we're using a 'Version' member to differentiate them, using the same JsonSerializerContext for both avoids having versioning issues
        var cacheJson      = JsonSerializer.Serialize(cache,      CacheDataSerializerContext.Default.CacheData);
        var localCacheJson = JsonSerializer.Serialize(localCache, CacheDataSerializerContext.Default.CacheData);
        if (!string.Equals(cacheJson, localCacheJson, StringComparison.Ordinal))
        {
            await logger.OutputVerboseAsync(() => "Current inputs differ from cached inputs — marking cache as stale.", cancellationToken);
            goto CacheStale;
        }

        // check expected packages
        string? coreNupkg = null;
        foreach (var (rid, nupkg) in localTargets)
        {
            await logger.OutputVerboseAsync(() => $"Checking package file: {nupkg}", cancellationToken);
            if (string.IsNullOrWhiteSpace(nupkg) || !File.Exists(nupkg))
            {
                await logger.ErrorAsync($"Expected package '{nupkg}' not found.", cancellationToken);
                goto CacheStale;
            }
            if (rid is "core") { coreNupkg = nupkg; }
        }

        // check symbols package
        if (!string.IsNullOrWhiteSpace(coreNupkg) && !localCache.Inputs.NoSymbols)
        {
            var snupkg = Path.ChangeExtension(coreNupkg, ".snupkg");
            await logger.OutputVerboseAsync(() => $"Checking symbols package file: {snupkg}", cancellationToken);
            if (!File.Exists(snupkg))
            {
                await logger.ErrorAsync($"Expected symbols package '{snupkg}' not found.", cancellationToken);
                goto CacheStale;
            }
        }

        // get most recent source file time using 'dotnet watch --list'
        var newestWatchedFilesWriteTime = DateTime.MinValue;
        exit = await RunProcessAsync("dotnet", [ "watch", "--list", "--project", projectFile.FullName ],
            async (procOut, cancellationToken) =>
            {
                var line = await procOut.ReadLineAsync(cancellationToken);
                if (line is null) { await Task.Yield(); return false; }
                if (!string.IsNullOrWhiteSpace(line) && File.Exists(line)) { newestWatchedFilesWriteTime = newestWatchedFilesWriteTime.Max(File.GetLastWriteTimeUtc(Path.GetFullPath(line))); }
                return true;
            },
            errorReaderAsync: null,
            cancellationToken
        );
        if (exit is not 0) { return await logger.FailAsync($"'dotnet watch --list' failed with exit code {exit}.", errorCode: exit, cancellationToken); }

        // get oldest package file time
        var oldestPackagesWriteTime = DateTime.MaxValue;
        foreach (var (_, nupkg) in localCache.Targets) { oldestPackagesWriteTime = oldestPackagesWriteTime.Min(File.GetLastWriteTimeUtc(nupkg)); }
        if (!string.IsNullOrWhiteSpace(coreNupkg) && !localCache.Inputs.NoSymbols) { oldestPackagesWriteTime = oldestPackagesWriteTime.Min(File.GetLastWriteTimeUtc(Path.ChangeExtension(coreNupkg, ".snupkg"))); }

        // check and compare file times
        if (newestWatchedFilesWriteTime > oldestPackagesWriteTime)
        {
            await logger.OutputVerboseAsync(() => $"Newest input ({newestWatchedFilesWriteTime:o}) > oldest package ({oldestPackagesWriteTime:o}) — marking cache as stale.", cancellationToken);
            goto CacheStale;
        }

        // we did what we could to ensure that the cache is up-to-date and we can skip retrying with a 'pack'
        goto CacheOkay;
    }

    CacheStale:
    {
        if (failStale) { return await logger.FailAsync($"Cache is stale and '{failStaleOption.Name}' was specified.", cancellationToken); }

        if (noPack)
        {
            await logger.OutputVerboseAsync(() => $"Cache is stale; proceeding without repacking due to '{noPackOption.Name}'.", cancellationToken);
            goto CacheOkay;
        }               

        if (retry++ >= maxRetriesWithPack) { return await logger.FailAsync("Cache is stale and repack attempt limit reached.", cancellationToken); }

        await logger.OutputVerboseAsync(() => $"Cache is stale — running '{packCommand.Name}' before push.", cancellationToken);
        exit = await HandlePackAsync(logger, options, projectFile, noLogo, cancellationToken);
        if (exit is not 0) { return exit; }

        goto CheckCache;
    }

    CacheOkay:
    {
        var pushSymbols = !(localNoSymbols ?? false);

        // Build the list of .nupkg files to push, with a deterministic order:
        //   core (if present) -> RID packages (alphabetical by RID) -> meta (if present)
        List<string> packagesToPush;

        if (localTargets is { Count: > 0 })
        {
            if (localTargets.FirstOrDefault(static p => string.IsNullOrWhiteSpace(p.Value)) is { Key: { } rid, Value: not null } /* <- essentially a "not-default" KVP */)
            { return await logger.FailAsync($"Invalid cache mapping: target '{rid}' has an empty package path.", cancellationToken); }

            packagesToPush = [];

            if (localTargets.TryGetValue("core", out var nupkg)) { packagesToPush.Add(nupkg); }
            packagesToPush.AddRange(localTargets
                .Where(static p => p.Key is not ("core" or "meta"))
                .OrderBy(static p => p.Key, StringComparer.Ordinal) // stable, deterministic
                .Select(static p => p.Value)
            );
            if (localTargets.TryGetValue("meta", out nupkg)) { packagesToPush.Add(nupkg); }
        }
        else
        {
            // No authoritative mapping (likely due to --no-pack). Fallback:
            // - If user targets are null/empty/contain "all": push every *.nupkg in outputDir.
            // - If user specified particular targets (e.g., "win-x64"), we cannot reliably map RIDs
            //   to filenames without a cache. Fail with a clear message.
            var pushAll = targets.Length is not > 0 || targets.Contains("all");
            if (!pushAll) {  return await logger.FailAsync($"Cannot map the requested targets to package files without a cache. Run '{packCommand.Name}' first or omit '{noPackOption.Name}'.", cancellationToken); }

            if (!Directory.Exists(outputDir)) { return await logger.FailAsync($"Output directory '{outputDir}' does not exist.", cancellationToken); }

            packagesToPush = [..Directory.EnumerateFiles(outputDir, "*.nupkg", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)];

            if (packagesToPush.Count is not > 0) { return await logger.FailAsync($"No packages found in '{outputDir}'. Consider running '{packCommand.Name}' first.", cancellationToken); }
        }

        await logger.OutputVerboseAsync(() => $"Packages to push: {string.Join(", ", packagesToPush.Select(static p => $"'{p}'"))} ({packagesToPush.Count})", cancellationToken);

        var apiKeyMasked = new string('*', apiKey.Length);
        var pushed = 0;
        foreach (var nupkg in packagesToPush)
        {
            await logger.OutputAsync($"Pushing package '{Path.GetFileName(nupkg)}' to '{nugetSource}'...", cancellationToken);

            // Run dotnet nuget push
            await logger.OutputDotnetCliAsync("nuget", [
                "push", nupkg,
                "--api-key", apiKeyMasked,
                "--source", nugetSource,                
                "--skip-duplicate"
            ], config: null, noRestore: false, noLogo: false, properties: [], cancellationToken);

            exit = await RunDotnetAsync("nuget", [
                "push", nupkg,
                "--api-key", apiKey,
                "--source", nugetSource,                
                "--skip-duplicate"
            ], config: null, noRestore: false, noLogo: false /* "dotnet nuget" doesn't actually accept a "--nologo" option */, properties: [], logger.Out, logger.Error, cancellationToken);

            await logger.OutputDotnetFinishedAsync("nuget push", exit, cancellationToken);

            if (exit is not 0) { return exit; }

            pushed++;
            await logger.OutputVerboseAsync(() => $"Package '{Path.GetFileName(nupkg)}' pushed successfully.", cancellationToken);

            // Optional symbols push (same API key/source for nuget.org). Only for existing .snupkg.
            if (pushSymbols && Path.ChangeExtension(nupkg, ".snupkg") is var snupkg && File.Exists(snupkg))
            {
                await logger.OutputAsync($"Pushing symbols package '{Path.GetFileName(snupkg)}' to '{nugetSource}'...", cancellationToken);

                // Run dotnet nuget push
                await logger.OutputDotnetCliAsync("nuget", [
                    "push", snupkg,
                    "--api-key", apiKeyMasked,
                    "--source", nugetSource,                
                    "--skip-duplicate"
                ], config: null, noRestore: false, noLogo: false, properties: [], cancellationToken);

                exit = await RunDotnetAsync("nuget", [
                    "push", snupkg,
                    "--api-key", apiKey,
                    "--source", nugetSource,                
                    "--skip-duplicate"
                ], config: null, noRestore: false, noLogo: false /* "dotnet nuget" doesn't actually accept a "--nologo" option */, properties: [], logger.Out, logger.Error, cancellationToken);

                await logger.OutputDotnetFinishedAsync("nuget push", exit, cancellationToken);               

                if (exit is not 0) { return exit; }

                pushed++;
                await logger.OutputVerboseAsync(() => $"Symbols package '{Path.GetFileName(snupkg)}' pushed successfully.", cancellationToken);
            }
        }        

        await logger.OutputAsync($"Push complete. Pushed {pushed} package(s).", cancellationToken);

        return 0;
    }
}

// ===== Logo Printing =====
static async Task PrintLogoAsync(TextWriter @out, CancellationToken cancellationToken)
{
    // ===== ASCII Art =====
    const string LogoArt = """

    """; // Your ASCII art goes here

    using var reader = new StringReader(LogoArt);
    while (await reader.ReadLineAsync(cancellationToken) is var line && line is not null)
    {
        int width;
        try { width = Console.WindowWidth; }
        catch { width = -1; }

        await @out.WriteLineAsync(line.TruncateToMaxLength(width).AsMemory(), cancellationToken);
    }
}

// ===== Process runners =====
static async Task<int> RunProcessAsync(string file, IEnumerable<string?> args, Func<TextReader, CancellationToken, Task<bool>>? outReaderAsync, Func<TextReader, CancellationToken, Task<bool>>? errorReaderAsync, CancellationToken cancellationToken)
{
    using var proc = new Process
    {
        StartInfo =
        {
            FileName = file,
            RedirectStandardOutput = outReaderAsync is not null,
            RedirectStandardError = errorReaderAsync is not null,
            UseShellExecute = false
        }
    };
    proc.StartInfo.ArgumentList.AddRange(args.OfType<string>());
    
    proc.Start();
    
    var stdOutTask = outReaderAsync is not null
        ? Task.Run(async () => { while (await outReaderAsync(proc.StandardOutput, cancellationToken) || !proc.HasExited); }, cancellationToken)
        : Task.CompletedTask;
    var stdErrTask = errorReaderAsync is not null
        ? Task.Run(async () => { while (await errorReaderAsync(proc.StandardError, cancellationToken) || !proc.HasExited); }, cancellationToken)
        : Task.CompletedTask;

    await Task.WhenAll(proc.WaitForExitAsync(cancellationToken), stdOutTask, stdErrTask);

    return proc.ExitCode;
}

static Task<int> RunDotnetAsync(string? command, IEnumerable<string?> args, string? config, bool noRestore, bool noLogo, IEnumerable<string> properties, TextWriter? @out, TextWriter? error, CancellationToken cancellationToken)
{
    const int defaultOutputBufferSize = 1024;

    return RunProcessAsync("dotnet", [ command,
            ..args,
            config is not null ? "-c" : null, config,
            noRestore ? "--no-restore" : null,
            noLogo ? "--nologo" : null,
            ..properties.Select(static prop => $"/p:{prop}")
        ],
        @out is not null
            ? GC.AllocateUninitializedArray<char>(defaultOutputBufferSize) switch { var buffer => async (procOut, cancellationToken) =>
            { 
                if (await @out.CopyAvailableTextFromAsync(procOut, buffer.AsMemory(), cancellationToken) is not > 0) { await Task.Yield(); return false; }
                return true;
            } }
            : null,
        error is not null
            ? GC.AllocateUninitializedArray<char>(defaultOutputBufferSize) switch { var buffer => async (procError, cancellationToken) =>
            { 
                if (await error.CopyAvailableTextFromAsync(procError, buffer.AsMemory(), cancellationToken) is not > 0) { await Task.Yield(); return false; }
                return true;
            } }
            : null,
        cancellationToken
    );
}

// ===== Handler prototype =====
file delegate Task<int> HandlerAsync(Logger logger, Options options, FileInfo projectFile, bool noLogo, CancellationToken cancellationToken);

// ===== Logging =====
file readonly record struct Logger(TextWriter Out, TextWriter Error, bool IsVerbose)
{
    private static Task LogAsync(TextWriter writer, string message, CancellationToken cancellationToken)
        => writer.WriteLineAsync(message.AsMemory(), cancellationToken);

    public readonly Task OutputAsync(string message, CancellationToken cancellationToken = default)
        => LogAsync(Out, message, cancellationToken);

    public readonly Task OutputConditionalAsync(bool condition, Func<string> messageFactory, CancellationToken cancellationToken = default)
        => condition ? OutputAsync(messageFactory(), cancellationToken) : Task.CompletedTask;

    public readonly async Task OutputConditionalAsync(bool condition, Func<CancellationToken, Task<string>> messageFactoryAsync, CancellationToken cancellationToken = default)
    {
        if (condition) { await OutputAsync(await messageFactoryAsync(cancellationToken), cancellationToken); }
    }

    public readonly Task OutputVerboseAsync(Func<string> messageFactory, CancellationToken cancellationToken = default)
        => OutputConditionalAsync(IsVerbose, messageFactory, cancellationToken);

    public readonly Task OutputVerboseAsync(Func<CancellationToken, Task<string>> messageFactoryAsync, CancellationToken cancellationToken = default)
        => OutputConditionalAsync(IsVerbose, messageFactoryAsync, cancellationToken);

    public readonly Task ErrorAsync(string message, CancellationToken cancellationToken = default)
        => LogAsync(Error, message, cancellationToken);

    public readonly Task ErrorConditionalAsync(bool condition, Func<string> messageFactory, CancellationToken cancellationToken = default)
        => condition ? ErrorAsync(messageFactory(), cancellationToken) : Task.CompletedTask;

    public readonly async Task ErrorConditionalAsync(bool condition, Func<CancellationToken, Task<string>> messageFactoryAsync, CancellationToken cancellationToken = default)
    {
        if (condition) { await ErrorAsync(await messageFactoryAsync(cancellationToken), cancellationToken); }
    }

    public readonly Task ErrorVerboseAsync(Func<string> messageFactory, CancellationToken cancellationToken = default)
        => ErrorConditionalAsync(IsVerbose, messageFactory, cancellationToken);

    public readonly Task ErrorVerboseAsync(Func<CancellationToken, Task<string>> messageFactoryAsync, CancellationToken cancellationToken = default)
        => ErrorConditionalAsync(IsVerbose, messageFactoryAsync, cancellationToken);

    public readonly async Task<int> FailAsync(string message, int errorCode, CancellationToken cancellationToken = default)
    {
        await ErrorAsync(message, cancellationToken);
        return errorCode;
    }

    public readonly Task<int> FailAsync(string message, CancellationToken cancellationToken = default)
        => FailAsync(message, errorCode: 1, cancellationToken);
}

// ===== Logging Helpers =====
file static class LoggerExtensions
{
    public static Task OutputDotnetCliAsync(in this Logger logger, string? command, IEnumerable<string?> args, string? config, bool noRestore, bool noLogo, IEnumerable<string> properties, CancellationToken cancellationToken = default)
    {
        return logger.OutputVerboseAsync(() => $"Running dotnet {string.Join(" ", ((IEnumerable<string?>)[ command,
            ..args,
            config is not null ? "-c" : null, config,
            noRestore ? "--no-restore" : null,
            noLogo ? "--no-logo" : null,
            ..properties.Select(static prop => $"/p:{prop}")
        ]).OfType<string>().Select(quote))}", cancellationToken);

        [return: NotNull] static string quote([NotNull] string value) => value switch { [] => "\"\"", ['"', .., '"'] => value, _ when value.Any(char.IsWhiteSpace) => $"\"{escape(value)}\"", _ => escape(value) };
        [return: NotNull] static string escape([NotNull] string value) => value.Replace("\"", "\\\"") /* escape containing double quotes */ switch { [.., '\\'] s => $"{s}\\" /* Windows paths: escape trailing backslash */, var s => s };
    }

    public static Task OutputDotnetFinishedAsync(in this Logger logger, string? command, int exitCode, CancellationToken cancellationToken = default)
        => logger.OutputVerboseAsync(() => $"dotnet {command} finished with exit code {exitCode}.", cancellationToken);
}

// ===== Options =====
file readonly record struct Options(ParseResult ParseResult, JsonDocument? JsonDocument)
{
    [return: NotNullIfNotNull(nameof(path))]
    private static FileSystemInfo? FromPath(string? path) => path switch
    {
        null => null,
        _ when File.Exists(path) => new FileInfo(path),
        _ when Directory.Exists(path) => new DirectoryInfo(path),
        _ when Path.HasExtension(path) => new FileInfo(path),
        _ => new DirectoryInfo(path)
    };

    public readonly bool? GetBoolean(Option<bool?> option, string propertyName)
        => ParseResult.GetValue(option) ?? (JsonDocument?.RootElement.TryGetProperty(propertyName, out var property) is true ? property.GetBoolean() : null);

    public readonly bool GetBoolean(Option<bool?> option, string propertyName, bool fallback)
        => GetBoolean(option, propertyName) ?? fallback;

    public readonly FileSystemInfo? GetFileSystemInfo(Option<FileSystemInfo?> option, string propertyName)
        => ParseResult.GetValue(option) ?? (JsonDocument?.RootElement.TryGetProperty(propertyName, out var property) is true ? FromPath(property.GetString()) : null);

    public readonly FileSystemInfo GetFileSystemInfo(Option<FileSystemInfo?> option, string propertyName, string fallback)
        => GetFileSystemInfo(option, propertyName) ?? FromPath(fallback);

    public readonly string? GetString(Option<string?> option, string propertyName)        
        => ParseResult.GetValue(option) ?? (JsonDocument?.RootElement.TryGetProperty(propertyName, out var property) is true ? property.GetString() : null);

    public string GetString(Option<string?> option, string propertyName, string fallback)
        => GetString(option, propertyName) ?? fallback;
}

// ===== Cache data JSON =====
internal readonly record struct CacheData(
    [property: JsonPropertyName("version")] Version                                   Version,
    [property: JsonPropertyName("inputs")]  CacheData.InputsData                      Inputs,
    [property: JsonPropertyName("targets")] ImmutableSortedDictionary<string, string> Targets
)
{
    internal readonly record struct InputsData(
        [property: JsonPropertyName("runtimesVersion")] string                     RuntimesVersion,
        [property: JsonPropertyName("runtimesUrl")]     string                     RuntimesUrl,
        [property: JsonPropertyName("config")]          string                     Config,
        [property: JsonPropertyName("noSymbols")]       bool                       NoSymbols,
        [property: JsonPropertyName("defines")]         ImmutableSortedSet<string> Defines,
        [property: JsonPropertyName("properties")]      ImmutableSortedSet<string> Properties
    );
}

[JsonSourceGenerationOptions()]
[JsonSerializable(typeof(CacheData))]
internal sealed partial class CacheDataSerializerContext : JsonSerializerContext;

// ===== Helpers =====
file static class Extensions
{
    public interface IDispatchComparable<T> where T : IComparable<T>;
    public interface IDispatchComparisonOperators<T> where T : IComparisonOperators<T, T, bool>;

    public static void AddRange<T>(this ICollection<T> coll, IEnumerable<T> items) { foreach (var item in items) { coll.Add(item); } }

    public static async Task<int> CopyAvailableTextFromAsync(this TextWriter dest, TextReader src, Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        var charsRead = await src.ReadAsync(buffer, cancellationToken);
        if (charsRead is > 0) { await dest.WriteAsync(buffer[..charsRead], cancellationToken); }
        return charsRead;
    }

    public static async Task<(string Id, string Version)?> GetNuGetPackageIdentityAsync(this FileInfo? nupkgFile, CancellationToken cancellationToken = default)
    {
        if (nupkgFile is { Exists: true, FullName: var fullName })
        {
            await using var file = File.OpenRead(fullName);
            using var reader = new PackageArchiveReader(file);
            if (await reader.GetIdentityAsync(cancellationToken) is { Id: var id, HasVersion: true, Version: var version }) { return (id, version.ToNormalizedString()); }
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static T Max<T>(this T value, T other, IDispatchComparable<T>? _ = default) where T : IComparable<T> => value.CompareTo(other) is >= 0 ? value : other;

    [OverloadResolutionPriority(1)]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static T Max<T>(this T value, T other, IDispatchComparisonOperators<T>? _ = default) where T : IComparisonOperators<T, T, bool> => value >= other ? value : other;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static T Min<T>(this T value, T other, IDispatchComparable<T>? _ = default) where T : IComparable<T> => value.CompareTo(other) is <= 0 ? value : other;

    [OverloadResolutionPriority(1)]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static T Min<T>(this T value, T other, IDispatchComparisonOperators<T>? _ = default) where T : IComparisonOperators<T, T, bool> => value <= other ? value : other;

    [return: NotNullIfNotNull(nameof(value))]
    public static string? NormalizeLower(this string? value)
    {
        if (value is null) { return null; }

        var valueSpan = value.AsSpan().Trim();
        return string.Create(valueSpan.Length, valueSpan, create);

        static void create(Span<char> dest, ReadOnlySpan<char> src) => src.ToLowerInvariant(dest);
    }

    public static bool TrySplitFirst(this string? value, char separator, [NotNullIfNotNull(nameof(value)), NotNullWhen(true)] out string? head, [NotNullWhen(true)] out string? tail)
    {
        if (value is null) { head = null; tail = null; return false; }

        var span = value.AsSpan();
        var idx = span.IndexOf(separator);

        if (idx is < 0) { head = value; tail = null; return false; }

        head = span[..idx].ToString(); tail = span[(idx + 1)..].ToString(); return true;
    }

    [return: NotNullIfNotNull(nameof(value))]
    public static string? TruncateToMaxLength(this string? value, int maxLength)
    {
        if (maxLength is < 0) { return value; }
        if (value is null) { return null; }
        if (maxLength is 0) { return string.Empty; }
        if (value.Length <= maxLength) { return value; }

        return string.Create(maxLength, value.AsSpan(), create);

        static void create(Span<char> dest, ReadOnlySpan<char> src) => src[..dest.Length].CopyTo(dest);
    }
}
