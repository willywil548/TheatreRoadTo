using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using Theatre_TimeLine.Contracts;
using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Defines how an inbound email should be routed for address creation.
    /// </summary>
    public enum InboundEmailRoutingMode
    {
        /// <summary>
        /// Process the email across all roads in the tenant.
        /// </summary>
        Tenant = 0,

        /// <summary>
        /// Process the email for a single road only.
        /// </summary>
        Road = 1
    }

    /// <summary>
    /// Represents a queued request for asynchronous inbound email processing.
    /// </summary>
    /// <param name="TenantId">The tenant that owns the inbound email.</param>
    /// <param name="EmailId">The unique processing identifier for this email.</param>
    /// <param name="StoredAs">The tenant-qualified stored email path reference.</param>
    /// <param name="RoutingMode">The routing mode for address creation.</param>
    /// <param name="RoadId">The optional road identifier when routing to a specific road.</param>
    /// <param name="HasSubdomain">Indicates whether the recipient domain included a subdomain.</param>
    /// <param name="Attempt">The current processing attempt number.</param>
    public sealed record InboundEmailProcessingRequest(
        Guid TenantId,
        Guid EmailId,
        string StoredAs,
        InboundEmailRoutingMode RoutingMode,
        Guid? RoadId,
        bool HasSubdomain,
        int Attempt = 1);

    /// <summary>
    /// Queues inbound email processing work for asynchronous execution.
    /// </summary>
    public interface IInboundEmailProcessingQueue
    {
        /// <summary>
        /// Enqueues an inbound email processing request.
        /// </summary>
        /// <param name="request">The processing request payload.</param>
        /// <param name="cancellationToken">A token to cancel enqueueing.</param>
        /// <returns>A task-like value that completes when the item is queued.</returns>
        ValueTask QueueAsync(InboundEmailProcessingRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Extracts structured address candidates from inbound email content by using an AI backend.
    /// </summary>
    public interface IInboundEmailAiExtractionService
    {
        /// <summary>
        /// Extracts structured address items from an inbound email.
        /// </summary>
        /// <param name="email">The inbound email payload.</param>
        /// <param name="context">Routing and timeline context for extraction decisions.</param>
        /// <param name="cancellationToken">A token to cancel extraction.</param>
        /// <returns>A structured extraction response.</returns>
        Task<AiExtractionResponse> ExtractAsync(
            SendGridInboundEmail email,
            AiExtractionContext context,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents routing and timeline context supplied to AI extraction.
    /// </summary>
    /// <param name="TenantId">The target tenant identifier.</param>
    /// <param name="RoutingMode">The routing mode used for address creation.</param>
    /// <param name="RoadId">The explicit target road identifier when route is road-level.</param>
    /// <param name="TargetRoadCount">How many roads will receive created addresses.</param>
    /// <param name="RoadWindowStart">Earliest start date among target roads, when available.</param>
    /// <param name="RoadWindowEnd">Latest end date among target roads, when available.</param>
    public sealed record AiExtractionContext(
        Guid TenantId,
        InboundEmailRoutingMode RoutingMode,
        Guid? RoadId,
        int TargetRoadCount,
        DateTime? RoadWindowStart,
        DateTime? RoadWindowEnd);

    /// <summary>
    /// Represents an AI-request error that can be retried safely.
    /// </summary>
    internal sealed class RetryableAiRequestException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RetryableAiRequestException"/> class.
        /// </summary>
        /// <param name="message">The retryable error message.</param>
        public RetryableAiRequestException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RetryableAiRequestException"/> class.
        /// </summary>
        /// <param name="message">The retryable error message.</param>
        /// <param name="innerException">The inner exception that caused this error.</param>
        public RetryableAiRequestException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Background queue worker that executes inbound email processing requests.
    /// </summary>
    public sealed class InboundEmailProcessingQueue : BackgroundService, IInboundEmailProcessingQueue
    {
        private const int MaxRetryAttempts = 3;

        // In-memory channel keeps ingestion and processing decoupled without external queue infrastructure.
        private readonly Channel<InboundEmailProcessingRequest> _channel = Channel.CreateUnbounded<InboundEmailProcessingRequest>();
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<InboundEmailProcessingQueue> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="InboundEmailProcessingQueue"/> class.
        /// </summary>
        /// <param name="scopeFactory">Creates scoped dependencies for each queue message.</param>
        /// <param name="logger">The logger instance.</param>
        public InboundEmailProcessingQueue(
            IServiceScopeFactory scopeFactory,
            ILogger<InboundEmailProcessingQueue> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public ValueTask QueueAsync(InboundEmailProcessingRequest request, CancellationToken cancellationToken = default)
        {
            return _channel.Writer.WriteAsync(request, cancellationToken);
        }

        /// <summary>
        /// Runs the queue consumption loop and handles retry scheduling for retryable failures.
        /// </summary>
        /// <param name="stoppingToken">Token used to stop background processing.</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Recover any durable queued/processing artifacts so app restarts do not drop work.
            await RecoverPendingRequestsAsync(stoppingToken);

            // Continuously drain queued requests until shutdown.
            await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IInboundEmailProcessor>();

                var result = await processor.ProcessAsync(request, stoppingToken);
                if (result.Success)
                {
                    continue;
                }

                if (!result.Retryable || request.Attempt >= MaxRetryAttempts)
                {
                    _logger.LogWarning(
                        "Inbound email processing permanently failed. EmailId: {EmailId}, Attempt: {Attempt}, Reason: {Reason}",
                        request.EmailId,
                        request.Attempt,
                        result.Summary);
                    continue;
                }

                var retryRequest = request with { Attempt = request.Attempt + 1 };
                var backoffSeconds = Math.Min(10, request.Attempt * 2);

                // Retry asynchronously with bounded backoff so ingestion is never blocked.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
                        await QueueAsync(retryRequest, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }, stoppingToken);
            }
        }

        /// <summary>
        /// Rebuilds in-memory queue entries from persisted processing artifacts after restart.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel recovery work.</param>
        /// <returns>A task that completes when recovery scan finishes.</returns>
        private async Task RecoverPendingRequestsAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var tenantManagerService = scope.ServiceProvider.GetRequiredService<ITenantManagerService>();

            int recoveredCount = 0;

            foreach (var tenant in tenantManagerService.GetTenants())
            {
                var tenantEmailStoragePath = Path.Combine(tenantManagerService.GetTenantRootPath(tenant.TenantId), "emails");
                if (!Directory.Exists(tenantEmailStoragePath))
                {
                    continue;
                }

                foreach (var processingDir in Directory.GetDirectories(tenantEmailStoragePath))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var folderName = Path.GetFileName(processingDir);
                    if (!Guid.TryParseExact(folderName, "N", out var emailId))
                    {
                        // Ignore legacy email files and non-processing directories.
                        continue;
                    }

                    var statePath = Path.Combine(processingDir, "processing", "state.json");
                    var referencePath = Path.Combine(processingDir, "raw", "reference.json");

                    if (!File.Exists(statePath) || !File.Exists(referencePath))
                    {
                        continue;
                    }

                    if (!TryReadRecoveryState(statePath, out var recoveryState) ||
                        !ShouldRecover(recoveryState.Status, recoveryState.Attempt))
                    {
                        continue;
                    }

                    if (!TryBuildRecoveryRequest(tenant.TenantId, emailId, referencePath, recoveryState, out var request))
                    {
                        continue;
                    }

                    await QueueAsync(request, cancellationToken);
                    recoveredCount++;
                }
            }

            _logger.LogInformation("Recovered {RecoveredCount} inbound email processing request(s) from disk artifacts.", recoveredCount);
        }

        /// <summary>
        /// Reads state metadata needed for restart recovery.
        /// </summary>
        /// <param name="statePath">Path to state.json.</param>
        /// <param name="recoveryState">Parsed state values.</param>
        /// <returns><see langword="true"/> when parsed successfully; otherwise <see langword="false"/>.</returns>
        private static bool TryReadRecoveryState(string statePath, out RecoveryState recoveryState)
        {
            recoveryState = new RecoveryState("unknown", 0);

            try
            {
                using var stream = File.OpenRead(statePath);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;

                var status = root.TryGetProperty("status", out var statusNode) && statusNode.ValueKind == JsonValueKind.String
                    ? statusNode.GetString() ?? "unknown"
                    : "unknown";

                var attempt = root.TryGetProperty("attempt", out var attemptNode) && attemptNode.TryGetInt32(out var parsedAttempt)
                    ? parsedAttempt
                    : 0;

                recoveryState = new RecoveryState(status, attempt);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Determines whether a state should be re-queued after restart.
        /// </summary>
        /// <param name="status">Persisted state value.</param>
        /// <param name="attempt">Persisted attempt count.</param>
        /// <returns><see langword="true"/> when the item should be recovered.</returns>
        private static bool ShouldRecover(string status, int attempt)
        {
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(status, "retryable-failure", StringComparison.OrdinalIgnoreCase))
            {
                return attempt < MaxRetryAttempts;
            }

            return string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "processing", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds a queued request from persisted reference metadata.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="emailId">Processing identifier.</param>
        /// <param name="referencePath">Path to reference.json.</param>
        /// <param name="recoveryState">Persisted recovery state values.</param>
        /// <param name="request">Recovery request payload when successful.</param>
        /// <returns><see langword="true"/> when request was built; otherwise <see langword="false"/>.</returns>
        private static bool TryBuildRecoveryRequest(
            Guid tenantId,
            Guid emailId,
            string referencePath,
            RecoveryState recoveryState,
            out InboundEmailProcessingRequest request)
        {
            request = default!;

            try
            {
                using var stream = File.OpenRead(referencePath);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;

                var storedAs = root.TryGetProperty("storedAs", out var storedAsNode) && storedAsNode.ValueKind == JsonValueKind.String
                    ? storedAsNode.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(storedAs))
                {
                    return false;
                }

                Guid? roadId = null;
                if (root.TryGetProperty("routeRoadId", out var roadIdNode) && roadIdNode.ValueKind == JsonValueKind.String)
                {
                    var rawRoadId = roadIdNode.GetString();
                    if (Guid.TryParse(rawRoadId, out var parsedRoadId))
                    {
                        roadId = parsedRoadId;
                    }
                }

                var hasSubdomain = root.TryGetProperty("hasSubdomain", out var subdomainNode) &&
                                   subdomainNode.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                   subdomainNode.GetBoolean();

                var routingMode = roadId.HasValue ? InboundEmailRoutingMode.Road : InboundEmailRoutingMode.Tenant;

                var attempt = string.Equals(recoveryState.Status, "retryable-failure", StringComparison.OrdinalIgnoreCase)
                    ? Math.Min(MaxRetryAttempts, recoveryState.Attempt + 1)
                    : Math.Max(1, recoveryState.Attempt);

                request = new InboundEmailProcessingRequest(
                    TenantId: tenantId,
                    EmailId: emailId,
                    StoredAs: storedAs,
                    RoutingMode: routingMode,
                    RoadId: roadId,
                    HasSubdomain: hasSubdomain,
                    Attempt: attempt);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Represents minimal persisted state values used for queue recovery.
        /// </summary>
        /// <param name="Status">Persisted processing state string.</param>
        /// <param name="Attempt">Persisted attempt number.</param>
        private sealed record RecoveryState(string Status, int Attempt);
    }

    /// <summary>
    /// Processes queued inbound email requests into validated timeline addresses.
    /// </summary>
    public interface IInboundEmailProcessor
    {
        /// <summary>
        /// Processes a queued inbound email request.
        /// </summary>
        /// <param name="request">The queued processing request.</param>
        /// <param name="cancellationToken">A token to cancel processing.</param>
        /// <returns>The processing result summary.</returns>
        Task<InboundEmailProcessingResult> ProcessAsync(InboundEmailProcessingRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Represents the outcome of one inbound email processing attempt.
    /// </summary>
    /// <param name="Success">Indicates whether processing completed successfully.</param>
    /// <param name="Retryable">Indicates whether failure can be retried.</param>
    /// <param name="Summary">Human-readable summary of processing outcome.</param>
    /// <param name="CreatedCount">Number of addresses created.</param>
    /// <param name="DroppedCount">Number of extracted items dropped.</param>
    public sealed record InboundEmailProcessingResult(bool Success, bool Retryable, string Summary, int CreatedCount = 0, int DroppedCount = 0);

    /// <summary>
    /// Implements inbound email processing, AI extraction, verification, and address creation.
    /// </summary>
    public sealed class InboundEmailProcessor : IInboundEmailProcessor
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly ITenantManagerService _tenantManagerService;
        private readonly IEmailEncryptionService _encryptionService;
        private readonly IInboundEmailAiExtractionService _aiExtractionService;
        private readonly IYouTubeValidationService _youTubeValidationService;
        private readonly ILogger<InboundEmailProcessor> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="InboundEmailProcessor"/> class.
        /// </summary>
        /// <param name="tenantManagerService">Tenant and road persistence service.</param>
        /// <param name="encryptionService">Service for decrypting stored inbound emails.</param>
        /// <param name="aiExtractionService">AI extraction service for template generation.</param>
        /// <param name="youTubeValidationService">YouTube validation service for video items.</param>
        /// <param name="logger">The logger instance.</param>
        public InboundEmailProcessor(
            ITenantManagerService tenantManagerService,
            IEmailEncryptionService encryptionService,
            IInboundEmailAiExtractionService aiExtractionService,
            IYouTubeValidationService youTubeValidationService,
            ILogger<InboundEmailProcessor> logger)
        {
            _tenantManagerService = tenantManagerService;
            _encryptionService = encryptionService;
            _aiExtractionService = aiExtractionService;
            _youTubeValidationService = youTubeValidationService;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<InboundEmailProcessingResult> ProcessAsync(InboundEmailProcessingRequest request, CancellationToken cancellationToken)
        {
            var artifactRoot = GetArtifactRootPath(request.TenantId, request.EmailId);

            // Materialize per-email working folders so processing, outputs, and drops are auditable.
            Directory.CreateDirectory(artifactRoot);
            Directory.CreateDirectory(Path.Combine(artifactRoot, "processing"));
            Directory.CreateDirectory(Path.Combine(artifactRoot, "outputs"));
            Directory.CreateDirectory(Path.Combine(artifactRoot, "drops"));

            await WriteProcessingStateAsync(artifactRoot, new
            {
                status = "processing",
                attempt = request.Attempt,
                updatedAt = DateTime.UtcNow
            }, cancellationToken);

            if (!request.HasSubdomain)
            {
                // Preserve existing rule: address creation only happens when routed through a subdomain mailbox.
                await WriteProcessingStateAsync(artifactRoot, new
                {
                    status = "completed",
                    reason = "No subdomain route. Address creation skipped.",
                    attempt = request.Attempt,
                    updatedAt = DateTime.UtcNow,
                    createdCount = 0,
                    droppedCount = 0
                }, cancellationToken);

                return new InboundEmailProcessingResult(true, false, "Subdomain rule skipped");
            }

            try
            {
                var email = await ReadStoredEmailAsync(request, cancellationToken);
                if (email == null)
                {
                    await WriteProcessingStateAsync(artifactRoot, new
                    {
                        status = "failed",
                        reason = "Stored email could not be loaded.",
                        attempt = request.Attempt,
                        updatedAt = DateTime.UtcNow
                    }, cancellationToken);

                    return new InboundEmailProcessingResult(false, false, "Stored email missing");
                }

                var tenant = _tenantManagerService.GetTenant(request.TenantId);
                if (tenant == null)
                {
                    return new InboundEmailProcessingResult(false, false, "Tenant not found");
                }

                var roadIds = request.RoutingMode == InboundEmailRoutingMode.Road && request.RoadId.HasValue
                    ? new[] { request.RoadId.Value }
                    : tenant.Roads.Select(road => road.RoadId).ToArray();

                var targetRoads = tenant.Roads
                    .Where(road => roadIds.Contains(road.RoadId))
                    .ToList();

                var startCandidates = targetRoads.Where(road => road.StartTime.HasValue).Select(road => road.StartTime!.Value).ToList();
                var endCandidates = targetRoads.Where(road => road.EndTime.HasValue).Select(road => road.EndTime!.Value).ToList();

                var extractionContext = new AiExtractionContext(
                    TenantId: request.TenantId,
                    RoutingMode: request.RoutingMode,
                    RoadId: request.RoadId,
                    TargetRoadCount: targetRoads.Count,
                    RoadWindowStart: startCandidates.Count > 0 ? startCandidates.Min() : null,
                    RoadWindowEnd: endCandidates.Count > 0 ? endCandidates.Max() : null);

                var extraction = await _aiExtractionService.ExtractAsync(email, extractionContext, cancellationToken);
                var extractionItems = ExpandExtractionItems(extraction.Items, email);

                // Track successes and dropped items independently to support partial-completion behavior.
                var created = new List<object>();
                var dropped = new List<object>();

                foreach (var item in extractionItems)
                {
                    var candidate = await BuildAddressCandidateAsync(item, email, cancellationToken);
                    if (!candidate.IsValid || candidate.Address == null)
                    {
                        dropped.Add(new
                        {
                            item = item,
                            reason = candidate.DropReason
                        });
                        continue;
                    }

                    foreach (var roadId in roadIds)
                    {
                        if (_tenantManagerService.TryAppendAddressToRoad(request.TenantId, roadId, candidate.Address))
                        {
                            created.Add(new
                            {
                                roadId,
                                addressType = candidate.Address.AddressType.ToString(),
                                title = candidate.Address.Title
                            });
                        }
                        else
                        {
                            dropped.Add(new
                            {
                                item = item,
                                reason = $"Road {roadId} does not belong to tenant {request.TenantId}."
                            });
                        }
                    }
                }

                await WriteJsonFileAsync(Path.Combine(artifactRoot, "outputs", "created.json"), created, cancellationToken);
                await WriteJsonFileAsync(Path.Combine(artifactRoot, "drops", "dropped.json"), dropped, cancellationToken);

                // Persist a final state snapshot for operational visibility.
                await WriteProcessingStateAsync(artifactRoot, new
                {
                    status = "completed",
                    attempt = request.Attempt,
                    updatedAt = DateTime.UtcNow,
                    createdCount = created.Count,
                    droppedCount = dropped.Count
                }, cancellationToken);

                return new InboundEmailProcessingResult(true, false, "Completed", created.Count, dropped.Count);
            }
            catch (RetryableAiRequestException ex)
            {
                _logger.LogWarning(ex, "Retryable AI processing error for email {EmailId} on attempt {Attempt}", request.EmailId, request.Attempt);

                await WriteProcessingStateAsync(artifactRoot, new
                {
                    status = "retryable-failure",
                    attempt = request.Attempt,
                    updatedAt = DateTime.UtcNow,
                    reason = ex.Message
                }, cancellationToken);

                return new InboundEmailProcessingResult(false, true, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inbound email processing failed for email {EmailId} on attempt {Attempt}", request.EmailId, request.Attempt);

                await WriteProcessingStateAsync(artifactRoot, new
                {
                    status = "failed",
                    attempt = request.Attempt,
                    updatedAt = DateTime.UtcNow,
                    reason = ex.Message
                }, cancellationToken);

                return new InboundEmailProcessingResult(false, false, ex.Message);
            }
        }

        private async Task<AddressBuildCandidate> BuildAddressCandidateAsync(AiExtractionItem item, SendGridInboundEmail sourceEmail, CancellationToken cancellationToken)
        {
            var requestedType = NormalizeAddressType(item.AddressType, item);
            var title = string.IsNullOrWhiteSpace(item.Title) ? sourceEmail.Subject ?? "Inbound Email" : item.Title.Trim();
            var description = string.IsNullOrWhiteSpace(item.Description)
                ? $"Inbound email from {sourceEmail.GetFromDisplayName() ?? sourceEmail.GetFromEmail() ?? "unknown sender"}"
                : item.Description.Trim();

            // Prefer model-provided location when present; otherwise use current time.
            // Reminder-like due-date items are intentionally scheduled a couple days earlier.
            var isReminderDueDateItem = IsReminderDueDateItem(item);
            var resolvedLocation = ResolveItemLocation(item.Location, isReminderDueDateItem);

            if (requestedType == AddressType.Video)
            {
                var url = item.Video?.Url ?? item.Content;
                var videoId = item.Video?.VideoId ?? YouTubeUrlParser.ExtractVideoId(url);
                if (string.IsNullOrWhiteSpace(videoId))
                {
                    // Business rule: non-YouTube links are downgraded to plain notification content.
                    requestedType = AddressType.Notification;
                }
                else
                {
                    if (_youTubeValidationService.HasServerKey())
                    {
                        // Validate against YouTube API when server key is available.
                        var (ok, error) = await _youTubeValidationService.ValidateVideoAsync(videoId);
                        if (!ok)
                        {
                            return new AddressBuildCandidate(false, null, error ?? "YouTube validation failed");
                        }
                    }

                    return new AddressBuildCandidate(true, new Address
                    {
                        Location = resolvedLocation,
                        Title = title,
                        Description = description,
                        Content = $"https://www.youtube.com/watch?v={videoId}",
                        AddressType = AddressType.Video,
                        DelayRelease = item.DelayRelease,
                        Tags = NormalizeTags(item.Tags)
                    }, null);
                }
            }

            if (requestedType == AddressType.Survey)
            {
                var question = item.Poll?.Question;
                if (string.IsNullOrWhiteSpace(question))
                {
                    return new AddressBuildCandidate(false, null, "Survey item missing question.");
                }

                var pollType = item.Poll?.PollType?.Equals("YesNo", StringComparison.OrdinalIgnoreCase) == true
                    ? PollType.YesNo
                    : PollType.MultipleChoice;

                var options = (item.Poll?.Options ?? [])
                    .Where(option => !string.IsNullOrWhiteSpace(option))
                    .Select(option => option.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (pollType == PollType.YesNo)
                {
                    // Normalize Yes/No polls to canonical options regardless of model output.
                    options = ["Yes", "No"];
                }
                else if (options.Count < 2)
                {
                    return new AddressBuildCandidate(false, null, "MultipleChoice survey needs at least two options.");
                }

                var pollAddress = new PollAddress
                {
                    Location = resolvedLocation,
                    Title = title,
                    Description = description,
                    DelayRelease = item.DelayRelease,
                    Tags = NormalizeTags(item.Tags)
                };

                pollAddress.SetPoll(new Poll
                {
                    Question = question.Trim(),
                    PollType = pollType,
                    Options = options
                });

                return new AddressBuildCandidate(true, pollAddress, null);
            }

            var content = string.IsNullOrWhiteSpace(item.Content)
                ? sourceEmail.GetBodyContent() ?? sourceEmail.GetTextBody() ?? sourceEmail.GetHtmlBody() ?? string.Empty
                : item.Content.Trim();

            if (string.IsNullOrWhiteSpace(content))
            {
                return new AddressBuildCandidate(false, null, "Notification item has empty content.");
            }

            return new AddressBuildCandidate(true, new Address
            {
                Location = resolvedLocation,
                Title = title,
                Description = description,
                Content = content,
                AddressType = AddressType.Notification,
                DelayRelease = item.DelayRelease,
                Tags = NormalizeTags(item.Tags)
            }, null);
        }

        /// <summary>
        /// Expands AI extraction output into actionable items when the model under-extracts.
        /// </summary>
        /// <param name="items">Items returned by the AI extractor.</param>
        /// <param name="sourceEmail">Source inbound email used for fallback parsing.</param>
        /// <returns>A normalized list of extraction items ready for address creation.</returns>
        private static IReadOnlyList<AiExtractionItem> ExpandExtractionItems(IReadOnlyList<AiExtractionItem> items, SendGridInboundEmail sourceEmail)
        {
            var expanded = new List<AiExtractionItem>(items ?? []);

            // Preserve model output when it already produced multiple actionable items.
            if (expanded.Count > 1)
            {
                return expanded;
            }

            var body = sourceEmail.GetBodyContent() ?? sourceEmail.GetTextBody() ?? sourceEmail.GetHtmlBody() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(body))
            {
                return expanded;
            }

            var seenContent = new HashSet<string>(expanded
                .Select(extractedItem => extractedItem.Content)
                .Where(content => !string.IsNullOrWhiteSpace(content))
                .Select(content => content!.Trim()), StringComparer.OrdinalIgnoreCase);

            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParseKeyDetailLine(line, out var label, out var detail))
                {
                    continue;
                }

                var detailContent = $"{label}: {detail}";
                if (!seenContent.Add(detailContent))
                {
                    continue;
                }

                // Keep the original line (including icons/symbols) in content for display fidelity.
                var rawContent = line.Trim();

                expanded.Add(new AiExtractionItem
                {
                    AddressType = "Notification",
                    Confidence = 0.7,
                    Title = string.IsNullOrWhiteSpace(sourceEmail.Subject)
                        ? label
                        : $"{sourceEmail.Subject} - {label}",
                    Description = $"Inbound detail: {label}",
                    Content = rawContent,
                    Location = TryExtractReminderIsoDate(label, detail),
                    DelayRelease = false,
                    Tags = [label]
                });
            }

            // Ensure at least one item exists if AI returned nothing parseable.
            if (expanded.Count == 0)
            {
                expanded.Add(new AiExtractionItem
                {
                    AddressType = "Notification",
                    Confidence = 0.5,
                    Title = string.IsNullOrWhiteSpace(sourceEmail.Subject) ? "Inbound Email" : sourceEmail.Subject,
                    Description = $"Inbound email from {sourceEmail.GetFromDisplayName() ?? sourceEmail.GetFromEmail() ?? "unknown sender"}",
                    Content = body.Length > 500 ? body[..500] : body,
                    DelayRelease = false,
                    Tags = ["Notification"]
                });
            }

            return expanded;
        }

        /// <summary>
        /// Attempts to parse a key-value detail line from inbound email body content.
        /// </summary>
        /// <param name="line">The raw line text.</param>
        /// <param name="label">Parsed label segment before the colon.</param>
        /// <param name="detail">Parsed detail segment after the colon.</param>
        /// <returns><see langword="true"/> when the line looks actionable; otherwise <see langword="false"/>.</returns>
        private static bool TryParseKeyDetailLine(string line, out string label, out string detail)
        {
            label = string.Empty;
            detail = string.Empty;

            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= line.Length - 1)
            {
                return false;
            }

            // Remove common bullet/symbol prefixes from labels.
            var rawLabel = Regex.Replace(line[..colonIndex].Trim(), "^[^\\p{L}\\p{N}]+", string.Empty);
            var rawDetail = line[(colonIndex + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(rawLabel) || string.IsNullOrWhiteSpace(rawDetail))
            {
                return false;
            }

            // Keep only known actionable label families to avoid over-fragmenting prose lines.
            var knownLabels = new[]
            {
                "due date",
                "where to pay",
                "late fee",
                "action needed",
                "discount",
                "pay-in-full"
            };

            if (!knownLabels.Any(known => rawLabel.Contains(known, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            label = rawLabel;
            detail = rawDetail;
            return true;
        }

        /// <summary>
        /// Attempts to infer an ISO-8601 reminder date/time from natural-language detail text.
        /// Reminders are intentionally scheduled two days before the parsed due/deadline date.
        /// </summary>
        /// <param name="detail">Detail text that may contain a date.</param>
        /// <returns>ISO-8601 date/time when parseable; otherwise <see langword="null"/>.</returns>
        private static string? TryExtractReminderIsoDate(string label, string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return null;
            }

            var isDueDateLabel = label.Contains("due", StringComparison.OrdinalIgnoreCase) ||
                                 label.Contains("deadline", StringComparison.OrdinalIgnoreCase);

            var reminderLeadDays = isDueDateLabel ? 2 : 0;

            if (DateTimeOffset.TryParse(detail, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedOffset))
            {
                return parsedOffset.AddDays(-reminderLeadDays).ToString("O");
            }

            if (DateTime.TryParse(detail, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDateTime))
            {
                return parsedDateTime.AddDays(-reminderLeadDays).ToString("O");
            }

            return null;
        }

        private async Task<SendGridInboundEmail?> ReadStoredEmailAsync(InboundEmailProcessingRequest request, CancellationToken cancellationToken)
        {
            var parts = request.StoredAs.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                return null;
            }

            var emailStorageRoot = Path.Combine(_tenantManagerService.GetTenantRootPath(request.TenantId), "emails");
            var filePath = Path.Combine(emailStorageRoot, parts[1]);
            if (!File.Exists(filePath))
            {
                return null;
            }

            var decrypted = await _encryptionService.ReadEncryptedFileAsync(filePath);
            return JsonSerializer.Deserialize<SendGridInboundEmail>(decrypted);
        }

        private string GetArtifactRootPath(Guid tenantId, Guid emailId)
        {
            return Path.Combine(_tenantManagerService.GetTenantRootPath(tenantId), "emails", emailId.ToString("N"));
        }

        private static AddressType NormalizeAddressType(string? rawType, AiExtractionItem item)
        {
            if (string.Equals(rawType, "Survey", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rawType, "Poll", StringComparison.OrdinalIgnoreCase))
            {
                return AddressType.Survey;
            }

            if (string.Equals(rawType, "Video", StringComparison.OrdinalIgnoreCase))
            {
                return AddressType.Video;
            }

            if (string.Equals(rawType, "Notification", StringComparison.OrdinalIgnoreCase))
            {
                return AddressType.Notification;
            }

            // Infer when type is omitted.
            if (!string.IsNullOrWhiteSpace(item.Poll?.Question))
            {
                return AddressType.Survey;
            }

            var possibleVideoInput = item.Video?.VideoId ?? item.Video?.Url ?? item.Content;
            return string.IsNullOrWhiteSpace(YouTubeUrlParser.ExtractVideoId(possibleVideoInput))
                ? AddressType.Notification
                : AddressType.Video;
        }

        private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
        {
            return (tags ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
        }

        /// <summary>
        /// Resolves an extracted location string into a timeline location value.
        /// </summary>
        /// <param name="rawLocation">The model-provided date/time string.</param>
        /// <returns>The resolved date/time, or current UTC time when missing/invalid.</returns>
        private static DateTime ResolveItemLocation(string? rawLocation, bool applyReminderLeadTime)
        {
            if (string.IsNullOrWhiteSpace(rawLocation))
            {
                return DateTime.UtcNow;
            }

            var leadDays = applyReminderLeadTime ? 2 : 0;

            // Date-only values should map directly to that calendar day at midnight.
            if (DateOnly.TryParse(rawLocation, out var parsedDateOnly))
            {
                return parsedDateOnly.ToDateTime(TimeOnly.MinValue).AddDays(-leadDays);
            }

            // Preserve the wall-clock time from the model to avoid timezone-shifted dates
            // (for example 2026-03-22T00:00:00Z appearing as previous day local time).
            if (DateTimeOffset.TryParse(rawLocation, out var parsedOffset))
            {
                return parsedOffset.DateTime.AddDays(-leadDays);
            }

            // Fallback for date-only and non-offset values.
            if (DateTime.TryParse(rawLocation, out var parsedDateTime))
            {
                return parsedDateTime.AddDays(-leadDays);
            }

            return DateTime.UtcNow;
        }

        /// <summary>
        /// Determines whether an extracted item should be treated as a due-date reminder.
        /// </summary>
        /// <param name="item">The extracted item.</param>
        /// <returns><see langword="true"/> when reminder lead-time should be applied.</returns>
        private static bool IsReminderDueDateItem(AiExtractionItem item)
        {
            var tags = item.Tags ?? [];
            if (tags.Any(tag =>
                tag.Contains("due", StringComparison.OrdinalIgnoreCase) ||
                tag.Contains("deadline", StringComparison.OrdinalIgnoreCase) ||
                tag.Contains("reminder", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var combinedText = $"{item.Description} {item.Content}";
            return combinedText.Contains("due date", StringComparison.OrdinalIgnoreCase) ||
                   combinedText.Contains("deadline", StringComparison.OrdinalIgnoreCase) ||
                   combinedText.Contains("reminder", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task WriteJsonFileAsync<T>(string path, T payload, CancellationToken cancellationToken)
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
        }

        private static Task WriteProcessingStateAsync(string artifactRoot, object payload, CancellationToken cancellationToken)
        {
            var statePath = Path.Combine(artifactRoot, "processing", "state.json");
            return WriteJsonFileAsync(statePath, payload, cancellationToken);
        }

        private sealed record AddressBuildCandidate(bool IsValid, Address? Address, string? DropReason);
    }

    /// <summary>
    /// Calls Azure OpenAI to convert inbound emails into typed extraction items.
    /// </summary>
    public sealed class AzureOpenAiInboundEmailAiExtractionService : IInboundEmailAiExtractionService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ChatClient _chatClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AzureOpenAiInboundEmailAiExtractionService> _logger;
        private readonly IReadOnlyList<string> _systemContextMessages;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureOpenAiInboundEmailAiExtractionService"/> class.
        /// </summary>
        /// <param name="configuration">Application configuration for Azure OpenAI endpoint, key, and deployment.</param>
        /// <param name="logger">The logger instance.</param>
        public AzureOpenAiInboundEmailAiExtractionService(
            IConfiguration configuration,
            ILogger<AzureOpenAiInboundEmailAiExtractionService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            // Keep the current configuration section and add a deployment key for Azure OpenAI.
            var endpoint = _configuration["AI:API:URL"];
            var apiKey = _configuration["AI:API:Key"];
            var deploymentName = _configuration["AI:OpenAI:DeploymentName"] ?? "gpt-4.1-mini";

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("AI:API:URL is not configured.");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("AI:API:Key is not configured.");
            }

            var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            _chatClient = client.GetChatClient(deploymentName);

            // Load simple system-context messages from settings so behavior can be tuned
            // without code changes.
            _systemContextMessages = BuildSystemContextMessages();
        }

        /// <inheritdoc />
        public async Task<AiExtractionResponse> ExtractAsync(
            SendGridInboundEmail email,
            AiExtractionContext context,
            CancellationToken cancellationToken = default)
        {
            var bodyContent = email.GetBodyContent() ?? email.GetTextBody() ?? email.GetHtmlBody() ?? string.Empty;

            // Ask the model for strict JSON and include configurable context messages.
            var messages = new List<ChatMessage>();

            foreach (var contextMessage in _systemContextMessages)
            {
                messages.Add(new SystemChatMessage(contextMessage));
            }

            messages.Add(new UserChatMessage(BuildExtractionPrompt(email, bodyContent, context)));

            try
            {
                var completion = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
                var content = string.Concat(completion.Value.Content.Select(part => part.Text));
                return ParseResponse(content);
            }
            catch (RequestFailedException ex) when (ex.Status == 429 || ex.Status >= 500)
            {
                throw new RetryableAiRequestException($"Azure OpenAI returned retryable status code {ex.Status}.", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new RetryableAiRequestException("Azure OpenAI request timed out.", ex);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning(ex, "Azure OpenAI returned non-retryable status code {StatusCode}", ex.Status);
                return new AiExtractionResponse();
            }
        }

        /// <summary>
        /// Builds ordered system-context messages from configuration with safe defaults.
        /// </summary>
        /// <returns>System messages used to guide extraction behavior.</returns>
        private IReadOnlyList<string> BuildSystemContextMessages()
        {
            var messages = new List<string>();

            // First message defines role and output contract.
            messages.Add(_configuration["AI:OpenAI:SystemMessage"]
                ?? "You convert inbound emails into JSON with shape {\"items\":[{\"addressType\":\"Notification|Survey|Video\",\"confidence\":0.0,\"title\":\"\",\"description\":\"\",\"content\":\"\",\"location\":\"ISO-8601 date time\",\"delayRelease\":false,\"tags\":[\"\"],\"poll\":{\"question\":\"\",\"pollType\":\"YesNo|MultipleChoice\",\"options\":[\"\"]},\"video\":{\"url\":\"\",\"videoId\":\"\"}}]}. Return JSON only.");

            // Optional simple messages let configuration inject business rules without code edits.
            var configuredMessages = _configuration
                .GetSection("AI:OpenAI:ContextMessages")
                .GetChildren()
                .Select(child => child.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .ToList();

            if (configuredMessages.Count > 0)
            {
                messages.AddRange(configuredMessages);
            }
            else
            {
                // Safe defaults aligned with current product rules.
                messages.Add("Only YouTube links should be classified as Video. Non-YouTube links must be Notification.");
                messages.Add("Use Survey for poll-style content. Use Notification when unsure.");
                messages.Add("If a specific date/time is present in the email, include it in item.location using ISO-8601 format.");
                messages.Add("Preserve meaningful symbols and emojis from the source email in item.content when possible.");
                messages.Add("When a due date or deadline is referenced for a reminder, set item.location about two days before that due date.");
                messages.Add("Return concise, non-duplicated items and preserve factual email content.");
            }

            return messages;
        }

        /// <summary>
        /// Builds the user prompt payload sent to Azure OpenAI.
        /// </summary>
        /// <param name="email">The inbound email payload.</param>
        /// <param name="bodyContent">The normalized body content.</param>
        /// <param name="context">Routing and road timeline context.</param>
        /// <returns>The prompt string for structured extraction.</returns>
        private static string BuildExtractionPrompt(SendGridInboundEmail email, string bodyContent, AiExtractionContext context)
        {
            var modelPayload = new
            {
                from = email.GetFromEmail(),
                fromDisplayName = email.GetFromDisplayName(),
                to = email.GetToEmail(),
                subject = email.Subject,
                receivedAt = email.ReceivedAt,
                content = bodyContent,
                rawEmail = email.RawEmail,
                dkim = email.Dkim,
                spf = email.Spf,
                spamScore = email.GetSpamScoreValue(),
                context = new
                {
                    tenantId = context.TenantId,
                    routingMode = context.RoutingMode.ToString(),
                    roadId = context.RoadId,
                    targetRoadCount = context.TargetRoadCount,
                    roadWindowStart = context.RoadWindowStart,
                    roadWindowEnd = context.RoadWindowEnd
                }
            };

            return JsonSerializer.Serialize(modelPayload);
        }

        private static AiExtractionResponse ParseResponse(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return new AiExtractionResponse();
            }

            // Azure OpenAI may return fenced JSON (```json ... ```). Normalize first so
            // direct deserialization and DOM parsing can succeed consistently.
            var normalizedBody = NormalizeJsonPayload(responseBody);

            try
            {
                // Preferred format: direct { items: [...] } JSON payload.
                var direct = JsonSerializer.Deserialize<AiExtractionResponse>(normalizedBody, JsonOptions);
                if (direct?.Items?.Count > 0)
                {
                    return direct;
                }

                // Some model responses may return the item array directly.
                var directItems = JsonSerializer.Deserialize<List<AiExtractionItem>>(normalizedBody, JsonOptions);
                if (directItems?.Count > 0)
                {
                    return new AiExtractionResponse { Items = directItems };
                }
            }
            catch (JsonException)
            {
            }

            try
            {
                using var doc = JsonDocument.Parse(normalizedBody);
                var root = doc.RootElement;

                if (root.TryGetProperty("items", out var directItems))
                {
                    return new AiExtractionResponse
                    {
                        Items = JsonSerializer.Deserialize<List<AiExtractionItem>>(directItems.GetRawText(), JsonOptions) ?? []
                    };
                }

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    // Alternate format: OpenAI-style response with message.content containing JSON text.
                    var choice = choices[0];
                    if (choice.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var contentNode) &&
                        contentNode.ValueKind == JsonValueKind.String)
                    {
                        var content = CleanupFencedJson(contentNode.GetString() ?? string.Empty);
                        var fromContent = JsonSerializer.Deserialize<AiExtractionResponse>(content, JsonOptions);
                        return fromContent ?? new AiExtractionResponse();
                    }
                }
            }
            catch (JsonException)
            {
            }

            return new AiExtractionResponse();
        }

        /// <summary>
        /// Normalizes model output into a likely JSON payload for parsing.
        /// </summary>
        /// <param name="value">Raw model output text.</param>
        /// <returns>A cleaned payload intended for JSON deserialization.</returns>
        private static string NormalizeJsonPayload(string value)
        {
            var cleaned = CleanupFencedJson(value).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return string.Empty;
            }

            // If the model added commentary text, try extracting the first JSON object/array.
            if (!cleaned.StartsWith('{') && !cleaned.StartsWith('['))
            {
                var firstObjectIndex = cleaned.IndexOf('{');
                var firstArrayIndex = cleaned.IndexOf('[');

                var firstJsonIndex = -1;
                if (firstObjectIndex >= 0 && firstArrayIndex >= 0)
                {
                    firstJsonIndex = Math.Min(firstObjectIndex, firstArrayIndex);
                }
                else if (firstObjectIndex >= 0)
                {
                    firstJsonIndex = firstObjectIndex;
                }
                else if (firstArrayIndex >= 0)
                {
                    firstJsonIndex = firstArrayIndex;
                }

                if (firstJsonIndex > 0)
                {
                    cleaned = cleaned[firstJsonIndex..];
                }
            }

            var lastObjectIndex = cleaned.LastIndexOf('}');
            var lastArrayIndex = cleaned.LastIndexOf(']');
            var lastJsonIndex = Math.Max(lastObjectIndex, lastArrayIndex);

            if (lastJsonIndex >= 0 && lastJsonIndex < cleaned.Length - 1)
            {
                cleaned = cleaned[..(lastJsonIndex + 1)];
            }

            return cleaned;
        }

        private static string CleanupFencedJson(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var cleaned = value.Trim();
            if (!cleaned.StartsWith("```", StringComparison.Ordinal))
            {
                return cleaned;
            }

            cleaned = Regex.Replace(cleaned, "^```[a-zA-Z]*\\s*", string.Empty);
            cleaned = Regex.Replace(cleaned, "\\s*```$", string.Empty);
            return cleaned.Trim();
        }
    }

    /// <summary>
    /// Represents the AI extraction response envelope.
    /// </summary>
    public sealed class AiExtractionResponse
    {
        /// <summary>
        /// Gets or sets extracted address candidates from the AI response.
        /// </summary>
        public List<AiExtractionItem> Items { get; set; } = [];
    }

    /// <summary>
    /// Represents a single extracted address candidate.
    /// </summary>
    public sealed class AiExtractionItem
    {
        /// <summary>
        /// Gets or sets the requested address type (Notification, Survey, or Video).
        /// </summary>
        public string? AddressType { get; set; }

        /// <summary>
        /// Gets or sets the model confidence for this extraction item.
        /// </summary>
        public double? Confidence { get; set; }

        /// <summary>
        /// Gets or sets the extracted title.
        /// </summary>
        public string? Title { get; set; }

        /// <summary>
        /// Gets or sets the extracted description.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the extracted content payload.
        /// </summary>
        public string? Content { get; set; }

        /// <summary>
        /// Gets or sets the extracted event location time in ISO-8601 format.
        /// </summary>
        public string? Location { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether release should be delayed.
        /// </summary>
        public bool DelayRelease { get; set; }

        /// <summary>
        /// Gets or sets extracted tags.
        /// </summary>
        public List<string>? Tags { get; set; }

        /// <summary>
        /// Gets or sets poll-specific extraction details.
        /// </summary>
        public AiExtractionPoll? Poll { get; set; }

        /// <summary>
        /// Gets or sets video-specific extraction details.
        /// </summary>
        public AiExtractionVideo? Video { get; set; }
    }

    /// <summary>
    /// Represents extracted poll metadata.
    /// </summary>
    public sealed class AiExtractionPoll
    {
        /// <summary>
        /// Gets or sets the poll question.
        /// </summary>
        public string? Question { get; set; }

        /// <summary>
        /// Gets or sets the poll type label.
        /// </summary>
        public string? PollType { get; set; }

        /// <summary>
        /// Gets or sets the poll options.
        /// </summary>
        public List<string>? Options { get; set; }
    }

    /// <summary>
    /// Represents extracted video metadata.
    /// </summary>
    public sealed class AiExtractionVideo
    {
        /// <summary>
        /// Gets or sets the source video URL.
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets the extracted video identifier.
        /// </summary>
        public string? VideoId { get; set; }
    }
}
