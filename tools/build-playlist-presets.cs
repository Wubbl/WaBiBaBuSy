#:project ../WaBiBaBuSy.Models/WaBiBaBuSy.Models.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Builds Playlist JSON files from a preset definition whose items reference scene files
// (the readable CrossScreenConfig JSON used by the test mode).
//   dotnet run tools/build-playlist-presets.cs -- [presets.json] [outputDir]
// Defaults: TestScenarios/showcase/playlist-presets.json → %APPDATA%\WaBiBaBuSy\playlists\
// Also validates every scenario in the presets' folder, so a broken scene fails here and not at the party.

using System.Text.Json;
using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;

var repo = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", ".."));
var presetsPath = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(repo, "TestScenarios", "showcase", "playlist-presets.json"));
var outDir = args.Length > 1 ? args[1] : Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaBiBaBuSy", "playlists");
var baseDir = Path.GetDirectoryName(presetsPath)!;

var readOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
};
var presets = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(presetsPath), readOptions)
              ?? throw new InvalidDataException($"{presetsPath}: empty");

foreach (var scenario in Directory.GetFiles(baseDir, "*.json").Where(f => f != presetsPath))
{
    ScenarioLoader.Load(scenario);
    Console.WriteLine($"scenario ok: {Path.GetFileName(scenario)}");
}

// Same options as PlaylistStore.SaveAsync, so the app reads the files back unchanged.
var writeOptions = new JsonSerializerOptions { WriteIndented = true };
Directory.CreateDirectory(outDir);
foreach (var preset in presets.Playlists)
{
    var playlist = new Playlist
    {
        Name = preset.Name,
        Loop = preset.Loop,
        Shuffle = preset.Shuffle,
        DefaultItemDurationMs = preset.DefaultItemDurationMs,
        Items = preset.Items.Select(i => new PlaylistItem
        {
            Name = i.Name,
            Config = SceneFile.Load(Path.Combine(baseDir, i.Scene)),
            DurationMs = i.DurationMs,
            SnapToLap = i.SnapToLap,
        }).ToList(),
    };
    var name = string.Concat(playlist.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    var path = Path.Combine(outDir, name + ".json");
    File.WriteAllText(path, JsonSerializer.Serialize(playlist, writeOptions));
    Console.WriteLine($"playlist: {path} ({playlist.Items.Count} items)");
}

sealed class PresetFile { public List<Preset> Playlists { get; set; } = new(); }

sealed class Preset
{
    public string Name { get; set; } = "Preset";
    public bool Loop { get; set; } = true;
    public bool Shuffle { get; set; }
    public int DefaultItemDurationMs { get; set; } = 30_000;
    public List<PresetItem> Items { get; set; } = new();
}

sealed class PresetItem
{
    public string Name { get; set; } = string.Empty;
    public string Scene { get; set; } = string.Empty;
    public int? DurationMs { get; set; }
    public bool SnapToLap { get; set; }
}
