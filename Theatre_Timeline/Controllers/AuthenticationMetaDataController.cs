using Microsoft.AspNetCore.Mvc;

namespace Theatre_TimeLine.Controllers
{
    [Route("/.well-known/microsoft-identity-association.json")]
    [ApiController]
    public class AuthenticationMetaDataController : ControllerBase
    {
        private readonly IConfiguration configuration;
        private readonly ILogger<AuthenticationMetaDataController> logger;

        public AuthenticationMetaDataController(IConfiguration configuration,
            ILogger<AuthenticationMetaDataController> logger)
        {
            this.configuration = configuration;
            this.logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken cancellationToken)
        {
            string metaDataDir = configuration.GetValue<string>("MetaDataPhysicalPath") ?? ".well-known";
            FileInfo metaDataFileInfo = new(
                Path.Combine(
                    metaDataDir,
                    "microsoft-identity-association.json"));

            logger.LogDebug("Configured to path: {FullName}", metaDataFileInfo.FullName);

            if (!metaDataFileInfo.Exists)
            {
                logger.LogWarning("Metadata file not found at {FullName}", metaDataFileInfo.FullName);
                return NotFound();
            }

            var json = await System.IO.File.ReadAllTextAsync(metaDataFileInfo.FullName, cancellationToken);
            return Content(json, "application/json");
        }
    }
}
