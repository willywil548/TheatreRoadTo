using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Theatre_TimeLine.Services;

namespace Theatre_TimeLine.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class YouTubeController : ControllerBase
    {
        private readonly IYouTubeValidationService _validationService;

        public YouTubeController(
            IYouTubeValidationService validationService,
            IConfiguration configuration,
            ILogger<YouTubeController> logger)
        {
            _validationService = validationService;
        }

        /// <summary>
        /// Validate a YouTube video id: exists, embeddable, not age-restricted.
        /// GET /api/youtube/validate/{videoId}
        /// Requires authentication.
        /// </summary>
        [HttpGet("validate/{videoId}")]
        [Authorize]
        public async Task<IActionResult> Validate(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return BadRequest("No video id provided");
            }

            var (ok, error) = await _validationService.ValidateVideoAsync(videoId);
            if (!ok)
            {
                return BadRequest(error);
            }

            return Ok("ok");
        }

        /// <summary>
        /// Returns a simple true/false indicating whether server has a configured YouTube API key.
        /// GET /api/youtube/status
        /// </summary>
        [HttpGet("status")]
        [AllowAnonymous]
        public IActionResult Status()
        {
            var hasKey = _validationService.HasServerKey();
            return Ok(hasKey.ToString().ToLowerInvariant());
        }
    }
}
