using System.IO.Compression;
using System.Text.Json;
using AgentFn.Sdk;
using Microsoft.Extensions.Options;
using Quantum.Platform.Application;
using Quantum.Platform.Domain;

namespace Quantum.Platform;

public sealed class AgentFnPluginReleaseAutomatedReviewer(
    IOptions<AgentFnAutomatedReviewOptions> options) : IPluginReleaseAutomatedReviewer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ReviewableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".json", ".xml", ".config",
        ".js", ".mjs", ".ts", ".tsx", ".jsx", ".html", ".css", ".scss",
        ".sql", ".md", ".txt", ".yml", ".yaml", ".toml", ".ini", ".sh", ".ps1"
    };

    private readonly AgentFnAutomatedReviewOptions options = options.Value;

    public async Task<string> StartAsync(
        PluginListing listing,
        PluginRelease release,
        byte[] packageArchive,
        CancellationToken cancellationToken)
    {
        var workspace = BuildWorkspaceArchive(listing, release, packageArchive);
        using var client = CreateClient();
        var task = await client.CreateTaskAsync(
            new AgentFn.Sdk.CreateTaskRequest
            {
                Skill = options.Skill,
                Model = options.Model,
                Input = "Review the attached Quantum plugin release. Follow the Skill policy and return only the required structured result.",
                WorkspaceArchiveBase64 = Convert.ToBase64String(workspace),
                IdempotencyKey = $"quantum-release-{(long)release.Id}-{release.PackageSha256}"
            },
            cancellationToken);
        return task.TaskId;
    }

    public async Task<AutomatedPluginReviewResult> WaitAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var task = await client.WaitForTaskAsync(
            taskId,
            pollInterval: TimeSpan.FromSeconds(options.PollIntervalSeconds),
            timeout: TimeSpan.FromMinutes(options.TaskTimeoutMinutes),
            cancellationToken);
        if (task.Status != TaskExecutionStatus.Succeeded ||
            task.Output is not { IsSuccess: true, Value: { ValueKind: JsonValueKind.Object } output })
        {
            throw new InvalidOperationException(
                $"AgentFn review task '{taskId}' ended as {task.Status} ({task.ErrorCode ?? "no_error_code"}).");
        }

        var result = output.Deserialize<ReviewOutput>(JsonOptions) ??
            throw new InvalidOperationException("AgentFn review task returned an empty output.");
        var decision = result.Decision switch
        {
            "approve" => AutomatedPluginReviewDecision.Approve,
            "reject" => AutomatedPluginReviewDecision.Reject,
            "manual_review" => AutomatedPluginReviewDecision.ManualReview,
            _ => throw new InvalidOperationException("AgentFn review task returned an unknown decision.")
        };
        var findings = result.Findings
            .Where(static finding => finding.Severity is "critical" or "high")
            .Take(5)
            .Select(static finding => $"[{finding.Severity}] {finding.Code}: {finding.Message}");
        var summary = string.Join(" ", new[] { result.Summary }.Concat(findings));
        return new AutomatedPluginReviewResult(taskId, decision, summary);
    }

    private AgentFnClient CreateClient()
        => new(new AgentFnClientOptions
        {
            BaseUri = new Uri(options.BaseUrl, UriKind.Absolute),
            ClientId = options.ClientId,
            PrivateJwks = ReadPrivateJwks(),
            Scopes = ["skill:run", "task:read"],
            Timeout = TimeSpan.FromMinutes(options.TaskTimeoutMinutes + 1)
        });

    private string ReadPrivateJwks()
    {
        if (!string.IsNullOrWhiteSpace(options.PrivateJwks))
        {
            return options.PrivateJwks;
        }

        return File.ReadAllText(options.PrivateJwksPath);
    }

    private byte[] BuildWorkspaceArchive(
        PluginListing listing,
        PluginRelease release,
        byte[] packageArchive)
    {
        using var sourceStream = new MemoryStream(packageArchive, writable: false);
        using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read);
        using var targetStream = new MemoryStream();
        using (var target = new ZipArchive(targetStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var requestEntry = target.CreateEntry("review-request.json", CompressionLevel.Fastest);
            using (var requestTarget = requestEntry.Open())
            {
                JsonSerializer.Serialize(requestTarget, new
                {
                    pluginId = listing.PluginId,
                    pluginName = listing.Name,
                    pluginDescription = listing.Description,
                    version = release.Version,
                    quantumVersionSupport = release.QuantumVersionSupport,
                    releaseNotes = release.ReleaseNotes,
                    packageSha256 = release.PackageSha256
                }, JsonOptions);
            }

            long includedBytes = 0;
            var inventory = new List<InventoryEntry>();
            foreach (var entry in source.Entries.Where(static entry => !entry.FullName.EndsWith('/')))
            {
                var extension = Path.GetExtension(entry.FullName);
                var reviewable = string.Equals(entry.FullName, "plugin.json", StringComparison.OrdinalIgnoreCase) ||
                    ReviewableExtensions.Contains(extension);
                var withinLimits = entry.Length <= options.MaxReviewFileBytes &&
                    includedBytes + entry.Length <= options.MaxReviewTextBytes;
                var included = reviewable && withinLimits;
                inventory.Add(new InventoryEntry(
                    entry.FullName,
                    entry.Length,
                    included,
                    included ? null : reviewable ? "review_size_limit" : "binary_or_unsupported"));
                if (!included)
                {
                    continue;
                }

                var targetEntry = target.CreateEntry($"package/{entry.FullName}", CompressionLevel.Fastest);
                using var entrySource = entry.Open();
                using var entryTarget = targetEntry.Open();
                entrySource.CopyTo(entryTarget);
                includedBytes += entry.Length;
            }

            var inventoryEntry = target.CreateEntry("package-inventory.json", CompressionLevel.Fastest);
            using var inventoryTarget = inventoryEntry.Open();
            JsonSerializer.Serialize(inventoryTarget, inventory, JsonOptions);
        }

        return targetStream.ToArray();
    }

    private sealed record InventoryEntry(string Path, long SizeBytes, bool Included, string? Reason);

    private sealed record ReviewOutput
    {
        public string Decision { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public ReviewFinding[] Findings { get; init; } = [];
    }

    private sealed record ReviewFinding
    {
        public string Severity { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }
}
