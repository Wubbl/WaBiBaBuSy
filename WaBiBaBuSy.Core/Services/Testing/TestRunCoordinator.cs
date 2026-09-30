namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>Placeholder until Task 15 (automated test mode): owns the running test run.</summary>
public sealed class TestRunCoordinator
{
    public TestRunStatus Status => TestRunStatus.Idle;

    public bool Cancel() => false;
}
