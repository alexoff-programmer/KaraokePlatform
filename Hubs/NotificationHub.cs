using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;

namespace KaraokePlatform.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    // Оставляем пустым. SignalR из коробки знает, какой ConnectionId принадлежит какому юзеру.
}