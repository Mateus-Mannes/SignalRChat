namespace SignalRChat.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class AspireTopologyCollection : ICollectionFixture<AppHostFixture>
{
    public const string Name = "Aspire topology";
}
