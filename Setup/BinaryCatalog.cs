using System;
using System.Linq;

namespace WhisperSubs.Setup
{
    public static class BinaryCatalog
    {
        public static readonly BinaryVariant[] Variants = new[]
        {
            new BinaryVariant("cpu", "CPU Only",
                "Works on any system. No GPU required.", true),
            new BinaryVariant("cuda12", "NVIDIA CUDA 12",
                "Hardware-accelerated via NVIDIA GPU (CUDA 12). Requires NVIDIA drivers.", false),
            new BinaryVariant("vulkan", "Vulkan (Intel / AMD / NVIDIA)",
                "Cross-vendor GPU acceleration via Vulkan. Works with Intel iGPU, AMD, and NVIDIA.", false),
            new BinaryVariant("rocm", "AMD ROCm",
                "Hardware-accelerated via AMD GPU (ROCm / HIP). Requires AMD ROCm drivers.", false),
        };

        /// <summary>
        /// Returns the release asset name for a given platform and variant.
        /// E.g. "whisper-cli-linux-x64" or "whisper-cli-linux-x64-cuda12".
        /// </summary>
        public static string GetAssetName(string platform, string variant)
        {
            var name = $"whisper-cli-{platform}";
            if (variant != "cpu") name += $"-{variant}";
            return name;
        }

        /// <summary>
        /// Returns only the variants that have prebuilt binaries for the given platform.
        /// CI builds: linux-x64 (cpu, cuda12, vulkan, rocm), linux-arm64 (cpu only).
        /// </summary>
        public static BinaryVariant[] GetAvailableVariants(string platform)
        {
            return platform switch
            {
                "linux-x64" => Variants,
                "linux-arm64" => Variants.Where(v => v.Id == "cpu").ToArray(),
                _ => Array.Empty<BinaryVariant>()
            };
        }
    }

    public class BinaryVariant
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public bool IsDefault { get; }

        public BinaryVariant(string id, string displayName, string description, bool isDefault)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            IsDefault = isDefault;
        }
    }
}
