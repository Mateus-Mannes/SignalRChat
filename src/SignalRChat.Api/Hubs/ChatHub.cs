using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SignalRChat.Api.Hubs;

[Authorize]
public class ChatHub : Hub
{
    public async Task SendMessage(string message)
    {
        var user = this.Context.User?.Identity?.Name ?? "Unknown";
        await this.Clients.All.SendAsync("ReceiveMessage", user, message);
    }
}
