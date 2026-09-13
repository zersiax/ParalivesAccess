namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Shared ownership gate for exclusive accessibility navigation workflows.
    /// </summary>
    public static class LotBuildInputOwnership
    {
        private static int _consumedFrame = -1;

        /// <summary>
        /// True while one of the lot/build handlers has exclusive use of navigation or Enter, or
        /// for the remainder of a frame in which an owner just closed. The one-frame latch prevents
        /// the same Enter/Escape release from falling through to an older handler in the postfix.
        /// </summary>
        public static bool OwnsInput
        {
            get
            {
                return _consumedFrame == UnityEngine.Time.frameCount
                    || TutorialHandler.OwnsInput
                    || ContextActionHandler.OwnsInput
                    || OccupationHandler.OwnsInput
                    || BuildCatalogHandler.OwnsInput
                    || ItemPlacementHandler.OwnsInput
                    || LotObjectHandler.OwnsInput;
            }
        }

        /// <summary>Retains exclusive input ownership until the current frame has completed.</summary>
        internal static void HoldThroughCurrentFrame()
        {
            _consumedFrame = UnityEngine.Time.frameCount;
        }
    }
}
