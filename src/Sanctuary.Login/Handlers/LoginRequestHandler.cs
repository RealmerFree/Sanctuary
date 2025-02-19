﻿using System;
using System.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

using Sanctuary.Packet;
using Sanctuary.Database;
using Sanctuary.Packet.Common;
using Sanctuary.Core.Configuration;
using Sanctuary.Packet.Common.Attributes;
using Microsoft.EntityFrameworkCore;

namespace Sanctuary.Login.Handlers;

[PacketHandler]
public static class LoginRequestHandler
{
    private static ILogger _logger = null!;
    private static LoginServerOptions _options = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(LoginRequestHandler));

        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();

        var options = serviceProvider.GetRequiredService<IOptionsMonitor<LoginServerOptions>>();
        _options = options.CurrentValue;
        options.OnChange(o => _options = o);
    }

    public static bool HandlePacket(LoginConnection connection, Span<byte> data)
    {
        if (!LoginRequest.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(LoginRequest));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(LoginRequest), packet);

        var loginReply = new LoginReply();

        // Have we already logged-in?
        if (connection.Guid > 0)
        {
            connection.Send(loginReply);

            _logger.LogWarning("User tried to login twice. ( Guid: {guid}, Session: {session} )", connection.Guid, packet.Session);

            return true;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();

        var user = dbContext.Users.SingleOrDefault(x => x.Session == packet.Session);

        if (user is null)
        {
            connection.Send(loginReply);

            _logger.LogWarning("User tried to login with an unknown Session. ( Session: {session} )", packet.Session);

            return true;
        }

        // TODO: Clear Session from DB once we use a launcher.
        // user.Session = null;
        user.LastLogin = DateTimeOffset.UtcNow;

        if (dbContext.SaveChanges() <= 0)
        {
            connection.Send(loginReply);

            return true;
        }

        if (_options.IsLocked && !user.IsAdmin)
        {
            loginReply.Status = 2;

            connection.Send(loginReply);

            return true;
        }

        connection.Guid = user.Guid;

        loginReply.LoggedIn = true;
        loginReply.Status = 1;
        loginReply.IsMember = user.IsMember;

        var accountInfo = new AccountInfo
        {
            IsMember = user.IsMember,
            MaxCharacters = user.MaxCharacters,
            IsAdminAccount = user.IsAdmin
        };

        loginReply.Payload = accountInfo.Serialize();

        connection.Send(loginReply);

        return true;
    }
}