using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Theatre_TimeLine.Controllers
{
    /// <summary>
    /// Lightweight keepalive endpoint for platform health pings.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class KeepAliveController : ControllerBase
    {
        /// <summary>
        /// Returns a simple OK response without requiring authentication.
        /// </summary>
        /// <returns>An HTTP 200 keepalive response.</returns>
        [HttpGet]
        [AllowAnonymous]
        public IActionResult Get()
        {
            // Minimal response for load balancer/app service probe checks.
            return Ok(new { status = "ok" });
        }
    }
}
