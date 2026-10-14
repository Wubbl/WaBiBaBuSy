using System.Collections.Generic;
using WaBiBaBuSy.Models.Content;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Everything a client needs to start a cross-screen D2D scene, as received from the server via a LOAD command
/// with renderer_type = "d2d_crossscreen": the scene (background + layers, cm already resolved to px), this node's
/// seat-map layout, the shared start and the downloaded assets.
/// </summary>
public class CrossScreenApplyRequest
{
    /// <summary>Locally-cached copy of the bottom layer's file (the command's content id).</summary>
    public required string FilePath { get; init; }

    /// <summary>Which local monitor to render on.</summary>
    public int MonitorIndex { get; init; }

    public long SharedStartTimestampMs { get; init; }

    /// <summary>JSON <see cref="CrossScreenConfig"/> with the server's file paths (rewritten via <see cref="LocalAssetPaths"/>).</summary>
    public string SceneJson { get; init; } = "";

    /// <summary>JSON <c>NodeLayout</c> from the server's seat map — the canvas of Sequential layers. Empty = this monitor alone.</summary>
    public string LayoutJson { get; init; } = "";

    /// <summary>Every file the scene references, as sent by the server (Tier 1.3).</summary>
    public IReadOnlyList<ContentAssetRef> Assets { get; init; } = System.Array.Empty<ContentAssetRef>();

    /// <summary>Server-side original path → local cache path for every asset that was downloaded.</summary>
    public IReadOnlyDictionary<string, string> LocalAssetPaths { get; init; } = new Dictionary<string, string>();
}
