using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Theatre_TimeLine.Contracts;
using Theatre_TimeLine.Services;

namespace Theatre_TimeLine.Controllers
{
    /// <summary>
    /// Diagnostic controller to inspect user claims and security group membership.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]  // Must be logged in to see diagnostics
    public class DiagnosticsController : ControllerBase
    {
        private readonly ISecurityGroupService _securityGroupService;
        private readonly ILogger<DiagnosticsController> _logger;

        public DiagnosticsController(
            ISecurityGroupService securityGroupService,
            ILogger<DiagnosticsController> logger)
        {
            _securityGroupService = securityGroupService;
            _logger = logger;
        }

        /// <summary>
        /// Returns all claims from the current user's token.
        /// GET /api/diagnostics/claims
        /// </summary>
        [HttpGet("claims")]
        public IActionResult GetClaims()
        {
            var claims = User.Claims.Select(c => new
            {
                type = c.Type,
                value = c.Value
            }).ToList();

            return Ok(new
            {
                isAuthenticated = User.Identity?.IsAuthenticated ?? false,
                authenticationType = User.Identity?.AuthenticationType,
                name = User.Identity?.Name,
                email = User.GetEmail(),
                claimsCount = claims.Count,
                claims = claims
            });
        }

        /// <summary>
        /// Returns the user's security group memberships.
        /// GET /api/diagnostics/groups
        /// </summary>
        [HttpGet("groups")]
        public async Task<IActionResult> GetGroupMemberships()
        {
            string userEmail = User.GetEmail();
            
            if (string.IsNullOrEmpty(userEmail))
            {
                return Ok(new
                {
                    error = "Could not determine user email from claims",
                    email = (string?)null,
                    groups = Array.Empty<object>()
                });
            }

            // Check membership in known application groups
            var groupChecks = new List<object>();

            // Check Global Admin
            bool isGlobalAdmin = await _securityGroupService
                .IsUserInGroupAsync(userEmail, SecurityGroupNameBuilder.GlobalAdminsGroup);
            groupChecks.Add(new
            {
                group = SecurityGroupNameBuilder.GlobalAdminsGroup,
                isMember = isGlobalAdmin
            });

            // List all application groups
            var allGroups = await _securityGroupService.ListGroupsAsync();

            return Ok(new
            {
                email = userEmail,
                isGlobalAdmin = isGlobalAdmin,
                checkedGroups = groupChecks,
                allApplicationGroups = allGroups.Select(g => new
                {
                    name = g.Name,
                    id = g.Id,
                    memberCount = g.MemberCount
                }).ToList()
            });
        }

        /// <summary>
        /// Returns combined claims and group membership info.
        /// GET /api/diagnostics/whoami
        /// </summary>
        [HttpGet("whoami")]
        public async Task<IActionResult> WhoAmI()
        {
            string userEmail = User.GetEmail();
            
            bool isGlobalAdmin = false;
            if (!string.IsNullOrEmpty(userEmail))
            {
                isGlobalAdmin = await _securityGroupService
                    .IsUserInGroupAsync(userEmail, SecurityGroupNameBuilder.GlobalAdminsGroup);
            }

            var claims = User.Claims.Select(c => new
            {
                type = c.Type,
                value = c.Value
            }).ToList();

            // Look for role claims specifically
            var roleClaims = User.Claims
                .Where(c => c.Type == "roles" || 
                           c.Type == System.Security.Claims.ClaimTypes.Role ||
                           c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role")
                .Select(c => c.Value)
                .ToList();

            return Ok(new
            {
                user = new
                {
                    isAuthenticated = User.Identity?.IsAuthenticated ?? false,
                    name = User.Identity?.Name,
                    email = userEmail,
                    authenticationType = User.Identity?.AuthenticationType
                },
                security = new
                {
                    isGlobalAdmin = isGlobalAdmin,
                    globalAdminGroup = SecurityGroupNameBuilder.GlobalAdminsGroup,
                    roleClaims = roleClaims
                },
                allClaims = claims
            });
        }
    }
}
