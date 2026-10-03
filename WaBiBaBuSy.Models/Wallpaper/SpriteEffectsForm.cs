namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// The Scene editor's Effects tab as plain values: <see cref="From"/> fills it from a scene, <see cref="ToConfig"/>
/// builds the config back. Fields the tab does not show (trail opacity and falloff) are carried through, so editing
/// a scene never resets them.
/// </summary>
public sealed record SpriteEffectsForm(
    bool TrailEnabled, int TrailCount, int TrailSpacingMs, bool TrailUseGradingColor, float TrailStartOpacity, float TrailScaleFalloff,
    bool PulseEnabled, float PulseAmplitude, float PulseFrequencyHz,
    float SpinDegPerSecond, BezelEffect Entry, BezelEffect Exit, string BezelColorHex, int BezelDurationMs)
{
    /// <summary>The form for <paramref name="fx"/>; defaults (everything off) for null.</summary>
    public static SpriteEffectsForm From(SpriteEffectsConfig? fx)
    {
        var trail = fx?.Trail ?? new TrailConfig();
        var pulse = fx?.Pulse ?? new PulseConfig();
        var defaults = new SpriteEffectsConfig();
        return new SpriteEffectsForm(
            fx?.Trail != null, trail.Count, trail.SpacingMs, trail.UseGradingColor, trail.StartOpacity, trail.ScaleFalloff,
            fx?.Pulse != null, pulse.Amplitude, pulse.FrequencyHz,
            fx?.SpinDegPerSecond ?? 0f, fx?.Entry ?? BezelEffect.None, fx?.Exit ?? BezelEffect.None,
            fx?.BezelColorHex ?? defaults.BezelColorHex, fx?.BezelDurationMs ?? defaults.BezelDurationMs);
    }

    /// <summary>The config; null when nothing is enabled (scenes without effects stay unchanged).</summary>
    public SpriteEffectsConfig? ToConfig()
    {
        var fx = new SpriteEffectsConfig
        {
            Trail = TrailEnabled ? new TrailConfig
            {
                Count = TrailCount, SpacingMs = TrailSpacingMs, UseGradingColor = TrailUseGradingColor,
                StartOpacity = TrailStartOpacity, ScaleFalloff = TrailScaleFalloff,
            } : null,
            Pulse = PulseEnabled ? new PulseConfig { Amplitude = PulseAmplitude, FrequencyHz = PulseFrequencyHz } : null,
            SpinDegPerSecond = SpinDegPerSecond,
            Entry = Entry,
            Exit = Exit,
            BezelColorHex = string.IsNullOrWhiteSpace(BezelColorHex) ? new SpriteEffectsConfig().BezelColorHex : BezelColorHex.Trim(),
            BezelDurationMs = BezelDurationMs,
        };
        return SpriteEffects.HasAny(fx) ? fx : null;
    }
}
