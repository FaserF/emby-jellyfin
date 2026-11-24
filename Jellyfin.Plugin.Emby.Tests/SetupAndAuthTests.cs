using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Jellyfin.Plugin.Emby.Common;
using Jellyfin.Plugin.Emby.Controllers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Emby.Tests;

public class SetupAndAuthTests
{
    [Fact]
    public void EmbyPluginController_ShouldRequireAuth_AndElevationOnSyncAndSeerrTest()
    {
        var controllerType = typeof(EmbyPluginController);
        controllerType.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();

        string[] mustNotBeAnonymous =
        [
            nameof(EmbyPluginController.TestAndSaveConnection),
            nameof(EmbyPluginController.GetUserMappings),
            nameof(EmbyPluginController.UnlinkUserAccount),
            nameof(EmbyPluginController.GetFederationStatus),
            nameof(EmbyPluginController.TriggerSyncNow),
            nameof(EmbyPluginController.TestSeerrConnection)
        ];

        foreach (var methodName in mustNotBeAnonymous)
        {
            var method = controllerType.GetMethod(methodName);
            method.Should().NotBeNull($"Method {methodName} should exist");
            method!.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull($"{methodName} must not allow anonymous access");
        }

        controllerType.GetMethod(nameof(EmbyPluginController.TriggerSyncNow))!
            .GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be("RequiresElevation");

        controllerType.GetMethod(nameof(EmbyPluginController.TestSeerrConnection))!
            .GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be("RequiresElevation");
    }

    [Fact]
    public void UnlinkUserAccount_ShouldForbidCrossUserAction_ForNonAdmin()
    {
        var controller = new EmbyPluginController(
            new Mock<IHttpClientFactory>().Object,
            new Mock<ILibraryManager>().Object,
            new Mock<ILogger<EmbyPluginController>>().Object,
            new Mock<ILoggerFactory>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N")),
                        new Claim("Jellyfin-User", "Alice")
                    ], "TestAuth"))
                }
            }
        };

        controller.UnlinkUserAccount("Bob").Should().BeOfType<ForbidResult>();

        var ownResult = controller.UnlinkUserAccount("Alice").Should().BeOfType<OkObjectResult>().Subject;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ownResult.Value));
        doc.RootElement.GetProperty("unlinked").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void ControllerAuthHelper_ShouldIdentifyAdminAndOwnerCorrectly()
    {
        var userManager = new Mock<IUserManager>();
        var aliceId = Guid.NewGuid();
        var alicePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("Jellyfin-UserId", aliceId.ToString("N")),
            new Claim("Jellyfin-User", "Alice")
        ], "TestAuth"));

        ControllerAuthHelper.TryGetCaller(alicePrincipal, userManager.Object, out var caller).Should().BeTrue();
        caller!.IsAdmin.Should().BeFalse();
        ControllerAuthHelper.IsOwnerOrAdmin(caller, "Alice").Should().BeTrue();
        ControllerAuthHelper.IsOwnerOrAdmin(caller, "Bob").Should().BeFalse();
    }
}
