using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
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
        /// <param name="cancellationToken">A token to cancel extraction.</param>
        /// <returns>A structured extraction response.</returns>
        Task<AiExtractionResponse> ExtractAsync(SendGridInboundEmail email, CancellationToken cancellationToken = default);
    }

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

                if (!result.Retryable || request.Attempt >= 3)
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

                var extraction = await _aiExtractionService.ExtractAsync(email, cancellationToken);
                var tenant = _tenantManagerService.GetTenant(request.TenantId);
                if (tenant == null)
                {
                    return new InboundEmailProcessingResult(false, false, "Tenant not found");
                }

                var roadIds = request.RoutingMode == InboundEmailRoutingMode.Road && request.RoadId.HasValue
                    ? new[] { request.RoadId.Value }
                    : tenant.Roads.Select(road => road.RoadId).ToArray();

                // Track successes and dropped items independently to support partial-completion behavior.
                var created = new List<object>();
                var dropped = new List<object>();

                foreach (var item in extraction.Items)
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
                        Location = DateTime.UtcNow,
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
                    Location = DateTime.UtcNow,
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
                Location = DateTime.UtcNow,
                Title = title,
                Description = description,
                Content = content,
                AddressType = AddressType.Notification,
                DelayRelease = item.DelayRelease,
                Tags = NormalizeTags(item.Tags)
            }, null);
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
    /// Calls the configured AI HTTP endpoint to convert inbound emails into typed extraction items.
    /// </summary>
    public sealed class HttpInboundEmailAiExtractionService : IInboundEmailAiExtractionService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<HttpInboundEmailAiExtractionService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpInboundEmailAiExtractionService"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client used to call the AI endpoint.</param>
        /// <param name="configuration">Application configuration for endpoint and key values.</param>
        /// <param name="logger">The logger instance.</param>
        public HttpInboundEmailAiExtractionService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<HttpInboundEmailAiExtractionService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<AiExtractionResponse> ExtractAsync(SendGridInboundEmail email, CancellationToken cancellationToken = default)
        {
            var url = _configuration["AI:API:URL"];
            var key = _configuration["AI:API:Key"];

            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException("AI:API:URL is not configured.");
            }

            var bodyContent = email.GetBodyContent() ?? email.GetTextBody() ?? email.GetHtmlBody() ?? string.Empty;
            var payload = new
            {
                from = email.GetFromEmail(),
                fromDisplayName = email.GetFromDisplayName(),
                to = email.GetToEmail(),
                subject = email.Subject,
                content = bodyContent,
                rawEmail = email.RawEmail,
                dkim = email.Dkim,
                spf = email.Spf,
                spamScore = email.GetSpamScoreValue()
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                // Send both raw MIME and parsed fields so the AI backend can choose best extraction source.
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.TryAddWithoutValidation("api-key", key);
                request.Headers.TryAddWithoutValidation("x-api-key", key);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new RetryableAiRequestException("AI service request failed due to network issue.", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new RetryableAiRequestException("AI service request timed out.", ex);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new RetryableAiRequestException($"AI service returned retryable status code {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var nonSuccess = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("AI service returned non-success status {StatusCode}: {Body}", response.StatusCode, nonSuccess);

                // Non-retryable failures produce an empty extraction to allow graceful partial pipeline completion.
                return new AiExtractionResponse();
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseResponse(responseBody);
        }

        private static AiExtractionResponse ParseResponse(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return new AiExtractionResponse();
            }

            try
            {
                // Preferred format: direct { items: [...] } JSON payload.
                var direct = JsonSerializer.Deserialize<AiExtractionResponse>(responseBody, JsonOptions);
                if (direct?.Items?.Count > 0)
                {
                    return direct;
                }
            }
            catch (JsonException)
            {
            }

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
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
