namespace WhisperSubs.Configuration
{
    public enum SubtitleMode
    {
        /// <summary>
        /// Complete transcription of all speech (default, existing behavior).
        /// </summary>
        Full = 0,

        /// <summary>
        /// Only subtitle segments where detected language differs from the primary audio language.
        /// </summary>
        ForcedOnly = 1,

        /// <summary>
        /// Generate both a complete and a forced subtitle file per track.
        /// </summary>
        FullAndForced = 2,
    }
}
