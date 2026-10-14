namespace WaBiBaBuSy.Models.Content;

/// <summary>One place to get or make content, opened in the browser.</summary>
public sealed record ContentLink(string Name, string Url, string Tip);

/// <summary>A titled group of <see cref="ContentLink"/>s.</summary>
public sealed record ContentLinkGroup(string Title, IReadOnlyList<ContentLink> Links);

/// <summary>The gallery's "Get content ▾" menu (spec 2026-10-14 folder-playlist §3). Root-domain URLs only.</summary>
public static class ContentLinks
{
    private const string Licence = " Check the licence before a public show.";

    /// <summary>Shown as the last, non-clickable menu line.</summary>
    public const string Tip = "Best results: small transparent PNG / GIF, plain background → remove.bg, seamless loop";

    /// <summary>The menu, in display order.</summary>
    public static IReadOnlyList<ContentLinkGroup> Groups { get; } = new[]
    {
        new ContentLinkGroup("Free GIFs & sprites", new[]
        {
            new ContentLink("GIPHY", "https://giphy.com", "Huge GIF library, many with transparent backgrounds (stickers)." + Licence),
            new ContentLink("Tenor", "https://tenor.com", "Reaction GIFs, good for short loops." + Licence),
            new ContentLink("itch.io game assets", "https://itch.io/game-assets/free", "Free pixel-art sprite sheets — tiny files that scale up crisply." + Licence),
            new ContentLink("OpenGameArt", "https://opengameart.org", "Free sprites and animations, many CC0." + Licence),
            new ContentLink("LottieFiles", "https://lottiefiles.com", "Free vector animations; export as GIF for the wall." + Licence),
            new ContentLink("Pixabay", "https://pixabay.com", "Free photos, illustrations and short videos." + Licence),
            new ContentLink("Pexels", "https://www.pexels.com", "Free photos and full-screen video clips." + Licence),
        }),
        new ContentLinkGroup("Make with AI", new[]
        {
            new ContentLink("ChatGPT images", "https://chatgpt.com", "Describe it and ask for a transparent background."),
            new ContentLink("Ideogram", "https://ideogram.ai", "Strong at logos and text."),
            new ContentLink("Adobe Firefly", "https://firefly.adobe.com", "Images and short clips, commercially safe training data."),
            new ContentLink("Kling", "https://klingai.com", "Animate a still image into a short clip; ask for a seamless loop."),
            new ContentLink("Luma Dream Machine", "https://lumalabs.ai", "Image or text to short video loops."),
            new ContentLink("Runway", "https://runwayml.com", "Video generation and editing."),
            new ContentLink("ComfyUI", "https://www.comfy.org", "Free and local on your own GPU — best for making lots of content."),
        }),
        new ContentLinkGroup("Clean up", new[]
        {
            new ContentLink("remove.bg", "https://www.remove.bg", "Make the background transparent so the sprite can float."),
            new ContentLink("ezgif", "https://ezgif.com", "Crop, resize, speed up and convert video to GIF."),
            new ContentLink("Upscayl", "https://upscayl.org", "Free local upscaling for small images."),
        }),
    };
}
