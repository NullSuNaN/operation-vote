namespace operation_vote.Shared
{
  public static class ProtocolInfo
  {
    public const string Version = "1.3.3";
    public const int ClientDefaultVoteMultiplier = 1;
    public static class ClientCommands
    {
      public const string InitializeCommand = "INIT";
      public const string RegisterInstanceCommand = "REG";
      public const string AuthenticateRequestCommand = "AUTH";
      public const string AuthenticateResultCommand = "AUTH_RES";
    }
    public static class ServerCommands
    {
      public const string InitializeCommand = "INIT";
      public const string AuthenticateChallengeCommand = "AUTH";
      public const string AuthenticateResultCommand = "AUTH_RES";
      public const string UpdateStatusCommand = "UPD";
      public const string EndSessionCommand = "END";
    }
  }
}