using Bbs.Client.Core.Domain;
using Xunit;

namespace Bbs.Client.Core.Tests;

public sealed class OwnerTokenGatedActionRulesTests
{
    [Fact]
    public void Validate_ReturnsBlocked_WhenMetadataInvalid()
    {
        var metadata = ServerAccessMetadata.Invalid("missing metadata");

        var result = OwnerTokenGatedActionRules.Validate(
            OwnerTokenActionType.CreateArena,
            metadata,
            selectedServerId: "srv-1");

        Assert.False(result.CanExecute);
        Assert.Null(result.Plan);
        Assert.Contains("unavailable", result.Message);
    }

    [Fact]
    public void Validate_ReturnsBlocked_WhenServerMissing()
    {
        var metadata = new ServerAccessMetadata(
            IsValid: true,
            OwnerToken: "owner-token",
            DashboardEndpoint: "https://localhost:8080/dashboard",
            StatusMessage: "ok",
            Source: "test");

        var result = OwnerTokenGatedActionRules.Validate(
            OwnerTokenActionType.JoinArena,
            metadata,
            selectedServerId: string.Empty);

        Assert.False(result.CanExecute);
        Assert.Null(result.Plan);
        Assert.Contains("Select a server", result.Message);
    }

    [Fact]
    public void Validate_ReturnsBlocked_WhenOwnerTokenMissing()
    {
        var metadata = new ServerAccessMetadata(
            IsValid: true,
            OwnerToken: " ",
            DashboardEndpoint: "https://localhost:8080/dashboard",
            StatusMessage: "ok",
            Source: "test");

        var result = OwnerTokenGatedActionRules.Validate(
            OwnerTokenActionType.JoinArena,
            metadata,
            selectedServerId: "srv-1");

        Assert.False(result.CanExecute);
        Assert.Null(result.Plan);
        Assert.Contains("Owner token is missing", result.Message);
    }

    [Fact]
    public void Validate_ReturnsCreateArenaPlan_WhenPreconditionsSatisfied()
    {
        var metadata = new ServerAccessMetadata(
            IsValid: true,
            OwnerToken: "owner-token",
            DashboardEndpoint: "https://localhost:8080/dashboard",
            StatusMessage: "ok",
            Source: "test");

        var result = OwnerTokenGatedActionRules.Validate(
            OwnerTokenActionType.CreateArena,
            metadata,
            selectedServerId: "srv-1");

        Assert.True(result.CanExecute);
        Assert.NotNull(result.Plan);
        Assert.Equal("Create Arena", result.Plan!.DisplayName);
        Assert.Equal("/owner/create-arena", result.Plan.PlaceholderRoute);
        Assert.Equal("POST", result.Plan.PlaceholderMethod);
    }

    [Fact]
    public void Validate_ReturnsJoinArenaPlan_WhenPreconditionsSatisfied()
    {
        var metadata = new ServerAccessMetadata(
            IsValid: true,
            OwnerToken: "owner-token",
            DashboardEndpoint: "https://localhost:8080/dashboard",
            StatusMessage: "ok",
            Source: "test");

        var result = OwnerTokenGatedActionRules.Validate(
            OwnerTokenActionType.JoinArena,
            metadata,
            selectedServerId: "srv-1");

        Assert.True(result.CanExecute);
        Assert.NotNull(result.Plan);
        Assert.Equal("Join Arena", result.Plan!.DisplayName);
        Assert.Equal("/owner/join-arena", result.Plan.PlaceholderRoute);
        Assert.Equal("POST", result.Plan.PlaceholderMethod);
    }

    [Fact]
    public void Validate_ReturnsLeaveArenaPlan_WhenPreconditionsSatisfied()
    {
        var metadata = new ServerAccessMetadata(
            IsValid: true,
            OwnerToken: "owner-token",
            DashboardEndpoint: "https://localhost:8080/dashboard",
            StatusMessage: "ok",
            Source: "test");

        var result = OwnerTokenGatedActionRules.Validate(
            OwnerTokenActionType.LeaveArena,
            metadata,
            selectedServerId: "srv-1");

        Assert.True(result.CanExecute);
        Assert.NotNull(result.Plan);
        Assert.Equal("Leave Arena", result.Plan!.DisplayName);
        Assert.Equal("/owner/leave-arena", result.Plan.PlaceholderRoute);
        Assert.Equal("POST", result.Plan.PlaceholderMethod);
    }
}