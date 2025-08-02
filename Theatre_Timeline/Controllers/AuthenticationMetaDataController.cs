using Microsoft.AspNetCore.Mvc;

namespace Theatre_TimeLine.Controllers
{
    [Route("/.well-known/microsoft-identity-association.json")]
    [ApiController]
    public class AuthenticationMetaDataController : ControllerBase
    {
        private readonly IConfiguration configuration;

        public AuthenticationMetaDataController(IConfiguration configuration)
        {
            this.configuration = configuration;
        }

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            string metaDataDir = configuration.GetValue<string>("MetaDataPhysicalPath") ?? ".well-known";
            FileInfo metaDataFileInfo = new(
                Path.Combine(
                    metaDataDir,
                    "microsoft-identity-association.json"));
            if (!metaDataFileInfo.Exists)
            {
                return NotFound(); // Returns a 404 Not Found response
            }

            var json = await System.IO.File.ReadAllTextAsync(metaDataFileInfo.FullName);
            return Content(json, "application/json");
        }
    }
}
