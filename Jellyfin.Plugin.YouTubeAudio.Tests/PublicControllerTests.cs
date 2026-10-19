using System;
using System.IO;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.YouTubeAudio.Configuration;
using Jellyfin.Plugin.YouTubeAudio.Controllers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for who may use the approved-user download page.
/// </summary>
[Collection("Plugin")]
public sealed class PublicControllerTests
{
    private readonly IUserManager _users = Substitute.For<IUserManager>();
    private readonly PluginConfiguration _config = new();

    public PublicControllerTests()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(Path.Combine(Path.GetTempPath(), $"yta-cfg-{Guid.NewGuid():N}"));
        var plugin = new Plugin(paths, Substitute.For<IXmlSerializer>(), NullLogger<Plugin>.Instance);
        plugin.UpdateConfiguration(_config);
    }

    [Fact]
    public void SignedOutOrUnknownUsers_AreRefused()
    {
        Assert.Equal((false, false), Controller(null).CheckAccess());
        Assert.Equal((false, false), Controller("not-a-guid").CheckAccess());
        Assert.Equal((false, false), Controller(Guid.NewGuid().ToString()).CheckAccess());
    }

    [Fact]
    public void Administrators_AreAlwaysAllowed_AndMarkedAsSuch()
    {
        var admin = User("admin", isAdmin: true);

        Assert.Equal((true, true), Controller(admin.Id.ToString()).CheckAccess());
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    public void ApprovedUsers_AreAllowed_WhicheverWayTheIdWasSaved(string format)
    {
        var listener = User("listener");
        _config.ApprovedUserIds.Add(listener.Id.ToString(format).ToUpperInvariant());

        Assert.Equal((true, false), Controller(listener.Id.ToString("N")).CheckAccess());
        Assert.True(Controller(listener.Id.ToString("D")).IsAllowed());
    }

    [Fact]
    public void UsersWhoAreNotApproved_AreRefused()
    {
        var listener = User("listener");
        _config.ApprovedUserIds.Add(Guid.NewGuid().ToString("N"));

        Assert.False(Controller(listener.Id.ToString()).IsAllowed());
        var result = Controller(listener.Id.ToString()).Info();
        Assert.Equal("{ Approved = False }", Assert.IsType<OkObjectResult>(result).Value!.ToString());
    }

    private User User(string name, bool isAdmin = false)
    {
        var user = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider")
        {
            Id = Guid.NewGuid()
        };
        user.SetPermission(PermissionKind.IsAdministrator, isAdmin);
        _users.GetUserById(user.Id).Returns(user);
        return user;
    }

    private YouTubePublicController Controller(string? userIdClaim)
    {
        var controller = new YouTubePublicController(null!, null!, null!, _users, Substitute.For<IServerApplicationPaths>(), NullLogger<YouTubePublicController>.Instance);
        var identity = userIdClaim == null ? new ClaimsIdentity() : new ClaimsIdentity([new Claim("Jellyfin-UserId", userIdClaim)], "Test");
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return controller;
    }
}
