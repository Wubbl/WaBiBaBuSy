using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>Writes report.json (for tools) and report.html (for people) into the run's results folder.</summary>
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
        File.WriteAllText(Path.Combine(report.ResultsDirectory, "report.html"), RenderHtml(report));
    }

    public static string RenderHtml(TestRunReport r)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(E(r.Scenario)).Append(" · test run</title><style>").Append(Css).Append("</style></head><body><main>");

        sb.Append("<header><h1>").Append(E(r.Scenario)).Append("</h1>").Append(Badge(r.Verdict));
        if (r.Aborted) sb.Append(" <span class=\"aborted\">aborted: ").Append(E(r.AbortReason ?? "")).Append("</span>");
        sb.Append("<p class=\"meta\">").Append(E(r.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
          .Append(" · ").Append(E(FormatDuration(r.FinishedUtc - r.StartedUtc)))
          .Append(" · v").Append(E(r.Environment.AppVersion));
        if (r.Environment.Commit != null) sb.Append(" · ").Append(E(r.Environment.Commit));
        if (r.PrefetchMs != null) sb.Append(" · prefetch ").Append(r.PrefetchMs).Append(" ms");
        sb.Append("</p></header>");

        sb.Append("<section><h2>Nodes</h2><table><tr><th>Seat</th><th>Name</th><th>Resolution</th><th>Hz</th><th>Clock offset</th><th>RTT</th><th>Version</th></tr>");
        foreach (var n in r.Environment.Nodes.OrderBy(n => n.SeatOrder))
            sb.Append("<tr><td>").Append(n.SeatOrder + 1).Append("</td><td>").Append(E(n.Name)).Append(n.IsLocal ? " <small>(server)</small>" : "")
              .Append("</td><td>").Append(n.Width).Append('×').Append(n.Height).Append("</td><td>").Append(n.RefreshHz)
              .Append("</td><td>").Append(n.ClockOffsetMs.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms</td><td>")
              .Append(n.RttMs.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms</td><td>").Append(E(n.AppVersion)).Append("</td></tr>");
        sb.Append("</table>");
        if (r.Warnings.Count > 0)
        {
            sb.Append("<ul class=\"warnings\">");
            foreach (var w in r.Warnings) sb.Append("<li>").Append(E(w)).Append("</li>");
            sb.Append("</ul>");
        }
        if (r.NodeFailures.Count > 0)
        {
            sb.Append("<h3>Node failures</h3><ul class=\"missing\">");
            foreach (var f in r.NodeFailures)
                sb.Append("<li>").Append(E(DateTimeOffset.FromUnixTimeMilliseconds(f.AtUtcMs).ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)))
                  .Append(" · ").Append(E(f.Name)).Append(": ").Append(E(f.Reason)).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("<p class=\"note\">").Append(E(r.ClockNote)).Append("</p></section>");

        foreach (var step in r.Steps) RenderStep(sb, step, r.Thresholds);

        if (r.LogFiles.Count > 0)
        {
            sb.Append("<section><h2>Logs</h2><ul>");
            foreach (var f in r.LogFiles) sb.Append("<li><a href=\"").Append(E(f)).Append("\">").Append(E(f)).Append("</a></li>");
            sb.Append("</ul></section>");
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void RenderStep(StringBuilder sb, StepReport s, ScenarioThresholds thresholds)
    {
        sb.Append("<section class=\"step\"><h2>").Append(s.Index).Append(". ").Append(E(s.Label ?? s.Kind))
          .Append(" <small>").Append(E(s.Kind)).Append("</small> ").Append(Badge(s.Verdict)).Append("</h2>");
        if (s.Message.Length > 0) sb.Append("<p>").Append(E(s.Message)).Append("</p>");

        var timed = s.Probes.Where(p => p.Drift is { Verdict: not Verdict.Skipped }).ToList();
        if (timed.Count >= 2) sb.Append(DriftChart(timed, thresholds));

        foreach (var p in s.Probes)
        {
            bool hasImages = p.Samples.Any(x => x.CapturePath != null);
            bool interesting = hasImages || p.Parity.Count > 0 || p.PerfViolations.Count > 0 || p.Messages.Count > 0 || s.Probes.Count == 1 || p.Verdict is Verdict.Fail or Verdict.Warn || p.Error != null;
            if (!interesting) continue;

            sb.Append("<div class=\"probe\"><h3>Probe ").Append(E(p.ProbeId)).Append(' ').Append(Badge(p.Verdict)).Append("</h3>");
            if (p.Error != null) sb.Append("<p class=\"missing\">").Append(E(p.Error)).Append("</p>");
            if (p.Drift != null) sb.Append("<p>").Append(E(p.Drift.Message)).Append("</p>");
            foreach (var m in p.Messages) sb.Append("<p class=\"missing\">").Append(E(m)).Append("</p>");

            if (p.Drift != null && p.Drift.Errors.Count > 0)
            {
                sb.Append("<table><tr><th>Node</th><th>Error</th><th>Clock bound</th><th>Note</th></tr>");
                foreach (var e in p.Drift.Errors)
                    sb.Append("<tr><td>").Append(E(e.NodeName)).Append("</td><td>").Append(e.ErrorMs.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(" ms</td><td>±").Append(e.ClockBoundMs.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms</td><td>")
                      .Append(e.UsedRenderTime ? "render time (no present stats)" : "").Append("</td></tr>");
                sb.Append("</table>");
            }

            if (hasImages)
            {
                sb.Append("<div class=\"strip\">");
                foreach (var sample in p.Samples.Where(x => x.CapturePath != null))
                    sb.Append(Screenshot(sample, p.Positions.FirstOrDefault(c => c.NodeId == sample.NodeId && c.MonitorIndex == sample.MonitorIndex)));
                sb.Append("</div>");
            }

            foreach (var par in p.Parity)
            {
                sb.Append("<p>").Append(Badge(par.Verdict)).Append(' ').Append(E(par.NodeName)).Append(" vs ").Append(E(par.ReferenceNodeName))
                  .Append(": ").Append(E(par.Message));
                if (par.DiffImagePath != null) sb.Append(" · <a href=\"").Append(E(par.DiffImagePath)).Append("\">diff image</a>");
                sb.Append("</p>");
            }

            foreach (var missing in p.Samples.Where(x => x.Missing || x.Error != null))
                sb.Append("<p class=\"missing\">").Append(E(missing.NodeName)).Append(": ").Append(E(missing.Error ?? "missing")).Append("</p>");
            foreach (var v in p.PerfViolations) sb.Append("<p class=\"perf\">").Append(E(v)).Append("</p>");
            sb.Append("</div>");
        }
        sb.Append("</section>");
    }

    private static string Screenshot(NodeProbeSample s, PositionCheckResult? check)
    {
        var sb = new StringBuilder("<figure>");
        sb.Append("<a href=\"").Append(E(s.CapturePath!)).Append("\"><svg viewBox=\"0 0 ").Append(s.Width).Append(' ').Append(s.Height)
          .Append("\" role=\"img\" aria-label=\"").Append(E(s.NodeName)).Append(" capture\"><image href=\"").Append(E(s.CapturePath!))
          .Append("\" width=\"").Append(s.Width).Append("\" height=\"").Append(s.Height).Append("\"/>");
        if (check != null && check.Expected.Visibility is MarkerVisibility.Visible or MarkerVisibility.Partial)
            sb.Append(Rect(check.Expected.X, check.Expected.Y, check.Expected.Width, check.Expected.Height, "expected"));
        if (check?.Detected is { Found: true } d)
            sb.Append(Rect(d.X, d.Y, d.Width, d.Height, "detected"));
        if (check != null)
            sb.Append("<path class=\"player\" d=\"M").Append(F(check.PlayerCenterX - 10)).Append(' ').Append(F(check.PlayerCenterY))
              .Append("h20M").Append(F(check.PlayerCenterX)).Append(' ').Append(F(check.PlayerCenterY - 10)).Append("v20\"/>");
        sb.Append("</svg></a><figcaption>").Append(E(s.NodeName));
        if (s.MonitorIndex > 0) sb.Append(" #").Append(s.MonitorIndex);
        sb.Append(" · ").Append(s.RenderedElapsedMs).Append(" ms");
        if (check != null) sb.Append(" · ").Append(Badge(check.Verdict)).Append(' ').Append(E(check.Message));
        sb.Append("</figcaption></figure>");
        return sb.ToString();
    }

    private static string DriftChart(IReadOnlyList<ProbeReport> probes, ScenarioThresholds thresholds)
    {
        const int w = 600, h = 160, pad = 28;
        double max = Math.Max(Math.Max(60, thresholds.DriftSpreadMs * 1.2), probes.Max(p => p.Drift!.SpreadMs) * 1.1);
        string X(int i) => F(pad + i * (double)(w - 2 * pad) / Math.Max(1, probes.Count - 1));
        string Y(double v) => F(h - pad - v / max * (h - 2 * pad));
        var points = string.Join(' ', probes.Select((p, i) => $"{X(i)},{Y(p.Drift!.SpreadMs)}"));
        var sb = new StringBuilder("<figure class=\"chart\"><svg viewBox=\"0 0 ").Append(w).Append(' ').Append(h).Append("\" role=\"img\" aria-label=\"drift spread per probe\">");
        sb.Append("<line class=\"axis\" x1=\"").Append(pad).Append("\" y1=\"").Append(h - pad).Append("\" x2=\"").Append(w - pad).Append("\" y2=\"").Append(h - pad).Append("\"/>");
        foreach (var (v, cls) in new[] { (thresholds.DriftWarnMs, "warn"), (thresholds.DriftSpreadMs, "fail") })
            sb.Append("<line class=\"limit ").Append(cls).Append("\" x1=\"").Append(pad).Append("\" y1=\"").Append(Y(v)).Append("\" x2=\"").Append(w - pad)
              .Append("\" y2=\"").Append(Y(v)).Append("\"/><text x=\"").Append(w - pad + 2).Append("\" y=\"").Append(Y(v)).Append("\">").Append(F(v)).Append("</text>");
        sb.Append("<polyline points=\"").Append(points).Append("\"/>");
        sb.Append("</svg><figcaption>Drift spread (ms) per probe</figcaption></figure>");
        return sb.ToString();
    }

    private static string Rect(float x, float y, float w, float h, string cls) =>
        $"<rect class=\"{cls}\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\"/>";

    private static string Badge(Verdict v) => $"<span class=\"verdict {v.ToString().ToLowerInvariant()}\">{v.ToString().ToLowerInvariant()}</span>";
    private static string E(string text) => WebUtility.HtmlEncode(text);
    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string FormatDuration(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.TotalSeconds:F0} s";

    private const string Css = """
        :root{--bg:#f7f7f5;--fg:#1d1d1b;--muted:#6b6b66;--card:#fff;--line:#deded8;--pass:#1f7a3f;--warn:#a15c00;--fail:#b3261e;--skip:#6b6b66;--accent:#c026d3}
        @media (prefers-color-scheme: dark){:root{--bg:#161615;--fg:#ececea;--muted:#9a9a94;--card:#20201f;--line:#34342f;--pass:#5cc27f;--warn:#e0a13a;--fail:#ef6b62;--skip:#9a9a94;--accent:#e879f9}}
        body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,sans-serif}
        main{max-width:1100px;margin:0 auto;padding:24px 16px}
        h1{display:inline;margin:0 8px 0 0;font-size:24px}h2{font-size:17px;margin:0 0 8px}h3{font-size:14px;margin:12px 0 4px}
        section{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px;margin:16px 0}
        .meta,.note,small,figcaption{color:var(--muted)}
        table{border-collapse:collapse;width:100%;margin:8px 0}th,td{text-align:left;padding:4px 8px;border-bottom:1px solid var(--line)}
        .verdict{display:inline-block;padding:1px 8px;border-radius:999px;font-size:12px;font-weight:600;color:#fff}
        .verdict.pass{background:var(--pass)}.verdict.warn{background:var(--warn)}.verdict.fail{background:var(--fail)}.verdict.skipped{background:var(--skip)}
        .aborted,.missing{color:var(--fail)}.perf,.warnings{color:var(--warn)}
        .strip{display:flex;gap:8px;overflow-x:auto;padding-bottom:4px}
        figure{margin:0;flex:0 0 auto;width:280px}figure.chart{width:100%}svg{width:100%;height:auto;display:block;border-radius:6px;background:#000}
        figure.chart svg{background:transparent}
        polyline{fill:none;stroke:var(--accent);stroke-width:2}.axis{stroke:var(--line)}
        .limit{stroke-dasharray:4 4}.limit.warn{stroke:var(--warn)}.limit.fail{stroke:var(--fail)}text{fill:var(--muted);font-size:10px}
        rect.expected{fill:none;stroke:#22c55e;stroke-width:3;stroke-dasharray:8 6}rect.detected{fill:none;stroke:#ff00ff;stroke-width:3}
        path.player{stroke:#facc15;stroke-width:3}
        a{color:inherit}
        """;
}
