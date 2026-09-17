using StalkerModLauncher.Resources;

namespace StalkerModLauncher.Services;

internal static class AnomalyUsvfsEngineSelection
{
    public static IReadOnlyList<string> Renderers { get; } = ["DX8", "DX9", "DX10", "DX11"];

    public static string CreateRelativePath(string renderer, bool useAvx)
    {
        var normalized = renderer.Trim().ToUpperInvariant();
        if (!Renderers.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(LocalizedText.Format(Strings.Error_UnsupportedAnomalyRendererFormat, renderer), nameof(renderer));
        }

        return Path.Combine("bin", $"Anomaly{normalized}{(useAvx ? "AVX" : string.Empty)}.exe");
    }

    public static bool TryParseRelativePath(string? relativePath, out string renderer, out bool useAvx)
    {
        renderer = string.Empty;
        useAvx = false;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        foreach (var candidateRenderer in Renderers)
        {
            foreach (var candidateAvx in new[] { false, true })
            {
                var candidate = CreateRelativePath(candidateRenderer, candidateAvx);
                if (!candidate.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                renderer = candidateRenderer;
                useAvx = candidateAvx;
                return true;
            }
        }

        return false;
    }
}
