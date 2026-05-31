namespace operation_vote.Shared
{
  public enum VoteType
  {
    /// <summary>
    /// Not in Voters. Not in Supporters.
    /// </summary>
    Abstain,
    /// <summary>
    /// In Voters. In Supporters.
    /// </summary>
    Support,
    /// <summary>
    /// In Voters. Not in Supporters.
    /// </summary>
    Against
  }
}