namespace Theatre_TimeLine.Contracts
{
    /// <summary>
    /// Provides methods to manage road entities within the system.
    /// </summary>
    /// <remarks>This interface defines operations for saving, removing, and retrieving road entities.
    /// Implementations should ensure thread safety if accessed concurrently.</remarks>
    public interface IRoadToThereManager
    {
        /// <summary>
        /// Saves the specified road configuration.
        /// </summary>
        /// <param name="roadToThere">The road configuration to be saved. This parameter can be null, in which case no action is taken.</param>
        void SaveRoad(IRoadToThere? roadToThere);

        /// <summary>
        /// Removes the road with the specified identifier from the system.
        /// </summary>
        /// <remarks>Ensure that the road with the given <paramref name="roadId"/> exists before calling
        /// this method.</remarks>
        /// <param name="roadId">The unique identifier of the road to be removed.</param>
        void RemoveRoad(Guid roadId);

        /// <summary>
        /// Retrieves the road information associated with the specified identifier.
        /// </summary>
        /// <param name="roadId">The unique identifier of the road to retrieve.</param>
        /// <returns>An <see cref="IRoadToThere"/> object representing the road details. Returns <see langword="null"/> if no
        /// road is found with the specified identifier.</returns>
        IRoadToThere GetRoad(Guid roadId);
    }
}
