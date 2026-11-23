namespace DataGateway.Core.Queries;

/// <summary>The source fetch size and the maximum number of response rows.</summary>
/// <remarks>The constructor is internal: only <see cref="RowCap.Plan"/> builds one, so a plan always respects the
/// source's row cap.</remarks>
public sealed record RowLimit
{
    /// <summary>Creates a plan that keeps <paramref name="keep"/> rows.</summary>
    /// <param name="keep">The most rows the answer keeps.</param>
    internal RowLimit(int keep) => Keep = keep;
    /// <summary>The most rows the answer keeps.</summary>
    public int Keep { get; }
    /// <summary>The rows to ask the source for.</summary>
    public int Fetch => Keep + 1; // One more than kept, so a row past Keep means rows were left out.
}
