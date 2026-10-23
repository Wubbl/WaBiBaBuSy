namespace WaBiBaBuSy.Updater;

/// <summary>
/// Clients up to 2.7 launched the updater with <c>--install-dir "C:\...\WaBiBaBuSy\"</c>: the install dir ends in
/// '\', and Windows reads the closing <c>\"</c> as a literal quote, so every later option ends up inside that one
/// value (<c>C:\...\WaBiBaBuSy" --backup-dir C:\... --process-id 1234</c>). Those clients run the updater shipped
/// in the server's package, so the repair has to live here.
/// </summary>
public static class LegacyArgs
{
    /// <summary>
    /// Re-splits everything from the first arg with a stray quote at the option names; well-formed args pass
    /// unchanged. From there on the quote state is flipped, so a space inside a later path also splits argv —
    /// hence the re-join instead of repairing one arg.
    /// </summary>
    public static string[] Repair(string[] args)
    {
        int bad = Array.FindIndex(args, a => a.Contains('"'));
        if (bad < 0) return args;

        var result = args[..bad].ToList();
        string? value = null;
        // ponytail: words re-joined with one space, so a path with a double space or a "--" word breaks
        foreach (var word in string.Join(' ', args[bad..]).Replace('"', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                if (value != null) result.Add(value);
                value = null;
                result.Add(word);
            }
            else value = value == null ? word : value + " " + word;
        }
        if (value != null) result.Add(value);
        return result.ToArray();
    }
}
