using Microsoft.Graph.Models;
using Theatre_TimeLine.Models;
using Theatre_TimeLine.Services;

namespace Theatre_TimeLine.Contracts
{
    public enum RoadScope
    {
        Month = 0,
        Day = 1,
        Week = 2,
        Year = 3,
    }

    public interface IRoadToThere
    {
        /// <summary>
        /// Gets or sets the Start time of the road.
        /// </summary>
        DateTime? StartTime { get; set; }

        /// <summary>
        /// Gets or sets the End time of the road.
        /// </summary>
        DateTime? EndTime { get; set; }

        /// <summary>
        /// Gets or sets the Banner of the road.
        /// </summary>
        string Banner { get; set; }

        /// <summary>
        /// Gets the Tenant ID.
        /// </summary>
        Guid TenantId { get; }

        /// <summary>
        /// Gets the road ID.
        /// </summary>
        Guid RoadId { get; }

        /// <summary>
        /// Gets or sets the road admin.
        /// </summary>
        string RoadAdmin { get; set; }

        /// <summary>
        /// Gets or sets css for a road.
        /// </summary>
        string PageHostCssPath { get; set; }

        /// <summary>
        /// Gets or sets the description.
        /// </summary>
        string Description { get; set; }

        /// <summary>
        /// Gets or sets the title.
        /// </summary>
        string Title { get; set; }

        /// <summary>
        /// Gets or sets the road scope.
        /// </summary>
        RoadScope RoadScope { get; set; }

        /// <summary>
        /// Gets or sets the length of the scope.
        /// </summary>
        int RoadScopeLength { get; set; }

        /// <summary>
        /// Gets the duration of the road.
        /// </summary>
        int Duration { get; }

        /// <summary>
        /// Gets the address.
        /// </summary>
        /// <returns>An array of <see cref="IAddress"/>.</returns>
        Address[] Addresses { get; set; }
    }

    public static class RoadsToThereExtensions
    {
        /// <summary>
        /// Check if user is authorized to access the road.
        /// </summary>
        /// <param name="road">Road to check.</param>
        /// <param name="email">User's Email.</param>
        /// <param name="securityGroupService">Security Service to use.</param>
        /// <returns><c>true</c> if able to use the road.</returns>
        public static bool IsUserAuthorizedForRoad(
            this IRoadToThere road,
            string email,
            ISecurityGroupService securityGroupService,
            out RequiredSecurityLevel requiredSecurityLevel)
        {
            requiredSecurityLevel = RequiredSecurityLevel.NotAuthorized;
            if (string.Equals(road.TenantId, TenantManagerService.DemoGuid))
            {
                return true;
            }

            // No email, can't authorize.
            if (string.IsNullOrEmpty(email))
            {
                return false;
            }

            foreach (RequiredSecurityLevel enumValue in Enum.GetValues(typeof(RequiredSecurityLevel)))
            {
                bool isAuthorized = false;
                switch (enumValue)
                {
                    case RequiredSecurityLevel.RoadUser:
                        requiredSecurityLevel = RequiredSecurityLevel.RoadUser;
                        isAuthorized = CheckAuthorization(
                            email,
                            SecurityGroupNameBuilder.TenantRoadUser(road.TenantId, road.RoadId),
                            securityGroupService);
                        break;
                    case RequiredSecurityLevel.TenantUser:
                        requiredSecurityLevel = RequiredSecurityLevel.TenantUser;
                        isAuthorized = CheckAuthorization(
                            email,
                            SecurityGroupNameBuilder.TenantUser(road.TenantId),
                            securityGroupService);
                        break;
                    case RequiredSecurityLevel.TenantManager:
                        requiredSecurityLevel = RequiredSecurityLevel.TenantManager;
                        isAuthorized = CheckAuthorization(
                            email,
                            SecurityGroupNameBuilder.TenantManager(road.TenantId),
                            securityGroupService);
                        break;
                    case RequiredSecurityLevel.Global:
                        requiredSecurityLevel = RequiredSecurityLevel.Global;
                        isAuthorized = CheckAuthorization(
                            email,
                            SecurityGroupNameBuilder.GlobalAdminsGroup,
                            securityGroupService);
                        break;
                    default:
                        break;
                }

                if (isAuthorized)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CheckAuthorization(
            string email,
            string group,
            ISecurityGroupService securityGroupService)
        {
            try
            {
                if (securityGroupService.IsUserInGroupAsync(email, group).Result)
                {
                    return true;
                }
            }
            catch
            {
                // Try next and allow fail.
            }

            return false;
        }
    }
}