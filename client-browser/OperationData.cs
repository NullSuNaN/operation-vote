using operation_vote.Client;

namespace operation_vote.Interface.ClientBrowser
{
  public record OperationData(Operation.OperationType Type, TimeSpan? AfkLimit);
}