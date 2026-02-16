using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Components
{
    /// <summary>
    /// View mode for student selection.
    /// </summary>
    public enum StudentViewMode
    {
        /// <summary>
        /// Viewing a single student's roads.
        /// </summary>
        SingleStudent,

        /// <summary>
        /// Viewing all students' roads combined.
        /// </summary>
        AllStudents
    }

    /// <summary>
    /// Event arguments for when student selection changes.
    /// </summary>
    public class StudentSelectionChangedEventArgs
    {
        /// <summary>
        /// The new view mode.
        /// </summary>
        public StudentViewMode ViewMode { get; set; }

        /// <summary>
        /// All tokens (relevant when in AllStudents mode).
        /// </summary>
        public IReadOnlyList<AccessToken> Tokens { get; set; } = Array.Empty<AccessToken>();

        /// <summary>
        /// Combined road IDs from all students.
        /// </summary>
        public IReadOnlyList<Guid> AllAuthorizedRoadIds { get; set; } = Array.Empty<Guid>();
    }
}
