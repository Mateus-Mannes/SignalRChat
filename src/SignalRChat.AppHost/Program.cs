using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var replicaCount = builder.Configuration.GetValue("SignalRChat:ReplicaCount", 2);
var usePersistentData = builder.Configuration.GetValue("SignalRChat:UsePersistentData", true);
var useRandomPorts = builder.Configuration.GetValue("SignalRChat:UseRandomPorts", false);

if (replicaCount is < 1 or > 20)
{
    throw new InvalidOperationException(
        "SignalRChat:ReplicaCount must be between 1 and 20.");
}

var redis = builder
    .AddRedis("redis")
    .WithArgs("--appendonly", "yes");

if (usePersistentData)
{
    redis.WithDataVolume();
}

var postgres = builder
    .AddPostgres("postgres")
    .WithArgs(
        "-c", "shared_buffers=64MB",
        "-c", "max_connections=40",
        "-c", "work_mem=1MB");

if (usePersistentData)
{
    postgres.WithDataVolume();
}

var database = postgres.AddDatabase("signalrchat");

var apiInstances = new List<IResourceBuilder<ProjectResource>>(replicaCount);
IResourceBuilder<ProjectResource>? migrationOwner = null;

for (var index = 1; index <= replicaCount; index++)
{
    var instanceName = $"signalr-api-{index}";
    var api = builder
        .AddProject<Projects.SignalRChat_Api>(instanceName, "http")
        .WithEndpoint("http", endpoint => endpoint.Port = null)
        .WithReference(database)
        .WithReference(redis)
        .WaitFor(database)
        .WaitFor(redis)
        .WithEnvironment("SignalRChat__InstanceId", instanceName)
        .WithEnvironment("Database__ApplyMigrations", index == 1 ? "true" : "false")
        .WithHttpHealthCheck("/health");

    if (migrationOwner is null)
    {
        migrationOwner = api;
    }
    else
    {
        api.WaitFor(migrationOwner);
    }

    apiInstances.Add(api);
}

var web = builder
    .AddProject<Projects.SignalRChat_Web>("web", "http")
    .WithEndpoint("http", endpoint => endpoint.Port = null)
    .WithEnvironment("SignalR__ApiBaseUrl", "")
    .WithEnvironment("SignalR__HubUrl", "/chatHub")
    .WithHttpHealthCheck("/health");

IResourceBuilder<ContainerResource> nginx = builder
    .AddContainer("nginx", "nginx", "1.29-alpine")
    .WithBindMount("./nginx", "/opt/signalrchat-nginx", isReadOnly: true)
    .WithEntrypoint("/bin/sh")
    .WithArgs("/opt/signalrchat-nginx/start-nginx.sh")
    .WithEnvironment("API_COUNT", replicaCount.ToString())
    .WithEnvironment(
        "WEB_HOSTPORT",
        web.GetEndpoint("http").Property(EndpointProperty.HostAndPort))
    .WithReference(web.GetEndpoint("http"))
    .WaitFor(web)
    .WithHttpEndpoint(port: useRandomPorts ? null : 8080, targetPort: 80, name: "http")
    .WithHttpHealthCheck("/nginx-health");

for (var index = 0; index < apiInstances.Count; index++)
{
    var api = apiInstances[index];
    var endpointVariableName = $"API_{index + 1}_HOSTPORT";

    nginx
        .WithEnvironment(
            endpointVariableName,
            api.GetEndpoint("http").Property(EndpointProperty.HostAndPort))
        .WithReference(api.GetEndpoint("http"))
        .WaitFor(api);
}

nginx.WithExternalHttpEndpoints();

builder.Build().Run();
