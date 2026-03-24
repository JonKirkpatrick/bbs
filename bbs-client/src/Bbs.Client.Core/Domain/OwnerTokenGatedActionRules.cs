using System;

namespace Bbs.Client.Core.Domain;

public enum OwnerTokenActionType
{
    CreateArena = 0,
    JoinArena = 1
}

public sealed record OwnerTokenActionPlan(
    OwnerTokenActionType ActionType,
    string DisplayName,
    string PlaceholderRoute,
    string PlaceholderMethod);

public sealed record OwnerTokenActionGuardResult(
    bool CanExecute,
    string Message,
    OwnerTokenActionPlan? Plan);

public static class OwnerTokenGatedActionRules
{
    public static OwnerTokenActionGuardResult Validate(
        OwnerTokenActionType actionType,
        ServerAccessMetadata accessMetadata,
        string? selectedServerId)
    {
        if (!accessMetadata.IsValid)
        {
            return new OwnerTokenActionGuardResult(
                CanExecute: false,
                Message: "Owner-token actions are unavailable until valid server access metadata is loaded.",
                Plan: null);
        }

        if (string.IsNullOrWhiteSpace(selectedServerId))
        {
            return new OwnerTokenActionGuardResult(
                CanExecute: false,
                Message: "Select a server before invoking owner-token actions.",
                Plan: null);
        }

        if (string.IsNullOrWhiteSpace(accessMetadata.OwnerToken))
        {
            return new OwnerTokenActionGuardResult(
                CanExecute: false,
                Message: "Owner token is missing from server access metadata.",
                Plan: null);
        }

        var plan = BuildPlan(actionType);
        return new OwnerTokenActionGuardResult(
            CanExecute: true,
            Message: $"{plan.DisplayName} preconditions satisfied for server '{selectedServerId}'.",
            Plan: plan);
    }

    public static OwnerTokenActionPlan BuildPlan(OwnerTokenActionType actionType)
    {
        return actionType switch
        {
            OwnerTokenActionType.CreateArena => new OwnerTokenActionPlan(
                ActionType: OwnerTokenActionType.CreateArena,
                DisplayName: "Create Arena",
                PlaceholderRoute: "/owner/create-arena",
                PlaceholderMethod: "POST"),
            OwnerTokenActionType.JoinArena => new OwnerTokenActionPlan(
                ActionType: OwnerTokenActionType.JoinArena,
                DisplayName: "Join Arena",
                PlaceholderRoute: "/owner/join-arena",
                PlaceholderMethod: "POST"),
            _ => throw new ArgumentOutOfRangeException(nameof(actionType), actionType, "Unsupported owner-token action type.")
        };
    }
}