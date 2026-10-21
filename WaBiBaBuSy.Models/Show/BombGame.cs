namespace WaBiBaBuSy.Models.Show;

/// <summary>How a BiBaBo game ended (<see cref="ShowEventType.BombOutcome"/>).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum BombResult { None, Defused, Recycled, WrongCable, Timeout, Aborted }

/// <summary>The riddle at a cable's end; every kind yields a digit (design §14.1).</summary>
public enum RiddleKind { Math, Tangle, Maze, Logic, Sequence, Clock, Dots }

/// <summary>What a riddle card shows. Live is never stored: it is what solving the card tells you.</summary>
public sealed class BombRiddle
{
    public RiddleKind Kind { get; set; }

    /// <summary>Math: one line with Unicode superscript exponents ("(17·3 − 4²)⁽⁹⁻⁹⁾").</summary>
    public string? Formula { get; set; }

    /// <summary>Math: width ÷ height of the formula text, measured once on the server (the Announce rule).</summary>
    public float FormulaAspect { get; set; }

    /// <summary>Tangle: three polylines (y values at evenly spaced x from 0.05 to 0.95, card-local 0..1); strand 0 continues this cable.</summary>
    public float[][]? Strands { get; set; }

    /// <summary>Tangle: the exit each strand ends at (index into <see cref="ExitLabels"/>).</summary>
    public int[]? StrandExits { get; set; }

    /// <summary>Tangle / Maze: the digit written at each exit.</summary>
    public int[]? ExitLabels { get; set; }

    /// <summary>Sequence: the shown terms (the card text is them + "?").</summary>
    public int[]? SequenceTerms { get; set; }

    /// <summary>Maze: cells (W × H, row-major); bit 1 = wall on the right, bit 2 = wall below. Entry on the left at <see cref="MazeEntryRow"/>, exits on the right at <see cref="ExitRows"/>.</summary>
    public int MazeW { get; set; }
    public int MazeH { get; set; }
    public int[]? MazeWalls { get; set; }
    public int MazeEntryRow { get; set; }
    public int[]? ExitRows { get; set; }

    /// <summary>Logic: inputs A B C (0/1) and three gates ("XOR A C") driving the lamps 4, 2, 1.</summary>
    public int[]? LogicInputs { get; set; }
    public string[]? LogicGates { get; set; }

    /// <summary>Clock: the real time; the card draws it mirrored.</summary>
    public int ClockHour { get; set; }
    public int ClockMinute { get; set; }

    /// <summary>Dots: centres (x, y in 0..1), colour index per dot, the palette, which colour to count and its name.</summary>
    public float[]? Dots { get; set; }
    public int[]? DotColors { get; set; }
    public string[]? DotPalette { get; set; }
    public int DotTarget { get; set; }
    public string? DotTargetName { get; set; }
}

/// <summary>One cable: from a bomb terminal across the seats to its riddle card.</summary>
public sealed class BombCable
{
    public string ColorHex { get; set; } = "";
    public string EndNodeId { get; set; } = "";
    public int EndOrder { get; set; }

    /// <summary>x0, y0, x1, y1, … in canvas units; x unwrapped (may leave [0, CanvasWidth) on a Ring — draw with wrap shifts).</summary>
    public float[] Points { get; set; } = Array.Empty<float>();

    /// <summary>The card on the end seat, canvas units (inside that node's slice).</summary>
    public float CardX { get; set; }
    public float CardY { get; set; }
    public float CardW { get; set; }
    public float CardH { get; set; }

    public BombRiddle Riddle { get; set; } = new();
}

/// <summary>
/// A BiBaBo game (design 2026-10-21-bibabo-bomb-game-design.md §3): built once on the server, drawn by every seat as a
/// pure function of this object, the seat's layout and the event-local time.
/// </summary>
public sealed class BombGame
{
    /// <summary>= the EventId of the Bomb event; the BombOutcome event refers to it.</summary>
    public string GameId { get; set; } = "";
    public string BombNodeId { get; set; } = "";
    public int BombOrder { get; set; }

    /// <summary>Bomb centre and height, canvas units.</summary>
    public float BombX { get; set; }
    public float BombY { get; set; }
    public float BombSize { get; set; }

    /// <summary>The seat orders the roulette hops over; the last one is <see cref="BombOrder"/>.</summary>
    public int[] RouletteOrders { get; set; } = Array.Empty<int>();

    /// <summary>Blackout + roulette + drop + cables, then the countdown starts.</summary>
    public int IntroMs { get; set; } = 8_000;
    public int TimerMs { get; set; } = 300_000;

    public List<BombCable> Cables { get; set; } = new();

    /// <summary>BombOutcome only.</summary>
    public BombResult Result { get; set; }

    /// <summary>BombOutcome only: the cable cut (Defused / WrongCable), else −1.</summary>
    public int CutCable { get; set; } = -1;

    /// <summary>BombOutcome only: the timer when it ended.</summary>
    public int RemainingMs { get; set; }

    /// <summary>A copy for the BombOutcome event (cables and riddles shared, read-only).</summary>
    public BombGame WithOutcome(BombResult result, int cutCable, int remainingMs)
    {
        var copy = (BombGame)MemberwiseClone();
        copy.Result = result;
        copy.CutCable = cutCable;
        copy.RemainingMs = remainingMs;
        return copy;
    }
}
