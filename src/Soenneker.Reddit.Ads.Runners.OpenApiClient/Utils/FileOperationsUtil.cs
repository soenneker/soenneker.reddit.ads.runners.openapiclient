using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.Reddit.Ads.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.Process.Abstract;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using Soenneker.Utils.Yaml.Abstract;
using System.Collections.Generic;

namespace Soenneker.Reddit.Ads.Runners.OpenApiClient.Utils;

public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IConfiguration _configuration;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IYamlUtil _yamlUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IConfiguration configuration, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IFileDownloadUtil fileDownloadUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IKiotaUtil kiotaUtil, IOpenApiFixer openApiFixer,
        IYamlUtil yamlUtil)
    {
        _logger = logger;
        _configuration = configuration;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _openApiFixer = openApiFixer;
        _fileDownloadUtil = fileDownloadUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _yamlUtil = yamlUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string? localDirectory = _configuration["Reddit:Ads:LocalDirectory"];
        string gitDirectory = localDirectory is null ? await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken) : Path.GetFullPath(localDirectory);

        if (localDirectory is not null && !(await _fileUtil.Exists(Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj"))))
            throw new InvalidOperationException("LocalDirectory must point to the Reddit Ads OpenApiClient repository.");

        string targetFilePath = Path.Combine(gitDirectory, "openapi.json");

        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string openApiDocumentUrl = _configuration["Reddit:Ads:ClientGenerationUrl"] ?? "https://ads-api.reddit.com/api/v3/openapi.json";

        string? filePath = await _fileDownloadUtil.Download(openApiDocumentUrl,
            targetFilePath, fileExtension: ".json", cancellationToken: cancellationToken);

        if (filePath == null)
            throw new InvalidOperationException("Reddit Ads OpenAPI document download failed.");

        string rawDocument = await _fileUtil.Read(filePath, cancellationToken: cancellationToken);
        string trimmedDocument = rawDocument.TrimStart();

        if (!trimmedDocument.StartsWith('{') && !trimmedDocument.StartsWith('['))
        {
            string convertedFilePath = Path.Combine(gitDirectory, "openapi.converted.json");
            await _fileUtil.DeleteIfExists(convertedFilePath, cancellationToken: cancellationToken);
            await _yamlUtil.SaveAsJson(filePath, convertedFilePath, cancellationToken: cancellationToken);
            filePath = convertedFilePath;
        }

        string fixedFilePath = Path.Combine(gitDirectory, "openapi.fixed.json");
        await _fileUtil.DeleteIfExists(fixedFilePath, cancellationToken: cancellationToken);
        await _openApiFixer.Fix(filePath, fixedFilePath, cancellationToken).NoSync();

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.Generate(fixedFilePath, "RedditAdsOpenApiClient", Constants.Library, gitDirectory, cancellationToken).NoSync();

        await AddImageAssetFactory(srcDirectory, cancellationToken);

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            // Process children before parents without allocating LINQ sorting buffers.
            dirs.Sort(static (left, right) => right.Length.CompareTo(left.Length));
            foreach (string dir in dirs)
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async Task AddImageAssetFactory(string sourceDirectory, CancellationToken cancellationToken)
    {
        // Kiota 1.35 omits this factory when the model is both a property type and a base class.
        const string model = "ComponentsSchemaPostCreativeAssetsImageCreativeAsset";
        string modelPath = Path.Combine(sourceDirectory, "Models", model + ".cs");
        if (!(await _fileUtil.Exists(modelPath)) || (await _fileUtil.Read(modelPath, cancellationToken: cancellationToken))
            .Contains("static " + model + " CreateFromDiscriminatorValue", StringComparison.Ordinal) ||
            (await _fileUtil.Read(modelPath, cancellationToken: cancellationToken))
            .Contains("static global::Soenneker.Reddit.Ads.OpenApiClient.Models." + model + " CreateFromDiscriminatorValue", StringComparison.Ordinal))
            return;

        const string factory = """
            using System;
            using Microsoft.Kiota.Abstractions.Serialization;

            namespace Soenneker.Reddit.Ads.OpenApiClient.Models;

            public partial class ComponentsSchemaPostCreativeAssetsImageCreativeAsset
            {
                /// <summary>Creates an image creative asset for deserialization.</summary>
                public static ComponentsSchemaPostCreativeAssetsImageCreativeAsset CreateFromDiscriminatorValue(IParseNode parseNode)
                {
                    ArgumentNullException.ThrowIfNull(parseNode);
                    return new ComponentsSchemaPostCreativeAssetsImageCreativeAsset();
                }
            }
            """;
        await _fileUtil.Write(Path.Combine(sourceDirectory, "Models", model + ".Factory.cs"), factory, cancellationToken: cancellationToken);
    }
    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            throw new InvalidOperationException("Generated Reddit Ads client failed to build.");
        }

        if (_configuration["Reddit:Ads:LocalDirectory"] is not null)
            return;

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}
