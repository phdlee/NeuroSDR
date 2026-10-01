namespace NeuroSDR.Plugins;

/// <summary>Draws the RF spectrum trace. Implementations must not retain the sample array.</summary>
public interface ISpectrumRendererPlugin
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    void Render(Graphics graphics, Rectangle bounds, SpectrumRenderFrame frame);
}

public sealed class SpectrumRenderFrame
{
    public required float[] Spectrum { get; init; }
    public required Func<int, int> SpectrumIndexForX { get; init; }
    public int VisualOffsetPixels { get; init; }
    public float LevelOffsetDb { get; init; }
    public Color ForeColor { get; init; }
}

/// <summary>Owns and draws RF waterfall history for one display control.</summary>
public interface IWaterfallRendererPlugin : IDisposable
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    void Resize(int width, int height, Color background);
    void Push(float[] spectrum, Func<int, int> spectrumIndexForX, int visualOffsetPixels, float levelOffsetDb, Color background);
    void Render(Graphics graphics, Rectangle bounds);
    void Clear(Color background);
    void Shift(int pixels, Color background);
}

public sealed record DisplayPluginInfo(string Id, string Name, string Description, bool IsBuiltIn);

internal sealed record PluginSelection(string SpectrumId, string WaterfallId)
{
    public static PluginSelection Default { get; } = new("builtin.spectrum.neon-line", "builtin.waterfall.night");
}
