using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Security.Claims;
using SignalRChat.Api.Data;
using SignalRChat.Api.Features.Conversations;
using SignalRChat.Api.Hubs;

var builder = WebApplication.CreateBuilder(args);

var allowedOrigins = builder.Configuration.GetSection("AllowedCorsOrigins").Get<string[]>()
    ?? ["http://localhost:5233"];

var redisConnectionString = builder.Configuration.GetConnectionString("redis");
var instanceId = builder.Configuration["SignalRChat:InstanceId"]
    ?? Environment.MachineName;

builder.AddNpgsqlDbContext<ApplicationDbContext>("signalrchat");

builder.Services
    .AddIdentityApiEndpoints<IdentityUser>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConversationExceptionHandler>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var signalR = builder.Services.AddSignalR();

if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    var redis = ConnectionMultiplexer.Connect(redisConnectionString);

    builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
    builder.Services
        .AddDataProtection()
        .SetApplicationName("SignalRChat")
        .PersistKeysToStackExchangeRedis(redis, "SignalRChat-DataProtection-Keys");

    signalR.AddStackExchangeRedis(redisConnectionString, options =>
    {
        options.Configuration.ChannelPrefix = RedisChannel.Literal("SignalRChat");
    });
}

var app = builder.Build();

if (app.Environment.IsDevelopment()
    && builder.Configuration.GetValue("Database:ApplyMigrations", false))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    dbContext.Database.Migrate();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseExceptionHandler();
app.UseRouting();
app.UseCors();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-SignalRChat-Instance"] = instanceId;
        return Task.CompletedTask;
    });
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityApi<IdentityUser>();
app.MapGet("/account/me", (ClaimsPrincipal user) =>
    Results.Ok(new
    {
        email = user.Identity?.Name,
        instance = instanceId
    }))
    .RequireAuthorization();
app.MapPost("/logout", async (SignInManager<IdentityUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Ok();
});
app.MapConversationEndpoints();
app.MapHub<ChatHub>("/chatHub").RequireAuthorization();
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    instance = instanceId
}));

app.Run();
