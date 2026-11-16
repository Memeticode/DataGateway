namespace DataGateway.Core.Queries;

/// <summary>The source fetch size and the maximum number of response rows.</summary>
public sealed record RowLimit
{
    internal RowLimit(int keep) => Keep = keep;
    public int Keep { get; }
    public int Fetch => Keep + 1; // One more than kept, so a row past Keep means rows were left out.
}
