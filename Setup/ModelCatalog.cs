namespace WhisperSubs.Setup
{
    public static class ModelCatalog
    {
        public static readonly ModelEntry[] Models = new[]
        {
            new ModelEntry("ggml-large-v3-turbo-q5_0.bin", "Large V3 Turbo (Q5)", 574, true,
                "Best quality/size ratio. Recommended for most users."),
            new ModelEntry("ggml-large-v3-turbo.bin", "Large V3 Turbo (F16)", 1620, false,
                "Full-precision turbo model. Slightly better quality, 3x larger."),
            new ModelEntry("ggml-medium-q5_0.bin", "Medium (Q5)", 539, false,
                "Good quality, similar size to turbo-q5 but slower and less accurate."),
            new ModelEntry("ggml-medium.bin", "Medium (F16)", 1530, false,
                "Full-precision medium model."),
            new ModelEntry("ggml-small.bin", "Small", 488, false,
                "Faster inference, lower accuracy. Good for quick tests."),
            new ModelEntry("ggml-base.bin", "Base", 148, false,
                "Lightweight model. Fast but noticeably less accurate."),
            new ModelEntry("ggml-tiny.bin", "Tiny", 78, false,
                "Smallest model. Only for testing or very constrained environments."),
        };

        public const string HuggingFaceBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";
    }

    public class ModelEntry
    {
        public string FileName { get; }
        public string DisplayName { get; }
        public int SizeMB { get; }
        public bool IsRecommended { get; }
        public string Description { get; }

        public ModelEntry(string fileName, string displayName, int sizeMB, bool isRecommended, string description)
        {
            FileName = fileName;
            DisplayName = displayName;
            SizeMB = sizeMB;
            IsRecommended = isRecommended;
            Description = description;
        }
    }
}
