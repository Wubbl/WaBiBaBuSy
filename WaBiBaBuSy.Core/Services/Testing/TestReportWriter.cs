using System.Text.Json;
using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>Writes report.json (and, from Task 13, report.html) into the run's results folder.</summary>
public static class TestReportWriter
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Write(TestRunReport report)
    {
        Directory.CreateDirectory(report.ResultsDirectory);
        File.WriteAllText(Path.Combine(report.ResultsDirectory, "report.json"), JsonSerializer.Serialize(report, JsonOptions));
    }
}
