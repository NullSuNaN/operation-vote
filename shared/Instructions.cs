namespace operation_vote.Interface.Shared
{
  public record Instructions(string[] Keys, TimeSpan? AfkLimit);
}