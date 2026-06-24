using System.Diagnostics.CodeAnalysis;
using CounterStrikeSharp.API.Modules.Commands;

namespace CounterStrikeSharp.API.Core.Commands;

public class CommandDefinition
{
    [SetsRequiredMembers]
    public CommandDefinition(string name, string description, CommandInfo.CommandCallback callback)
    {
        Name = name;
        Description = description;
        Callback = callback;
    }

    public CommandDefinition()
    {
    }

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required CommandInfo.CommandCallback Callback { get; init; }

    public CommandUsage ExecutableBy { get; init; } = CommandUsage.CLIENT_AND_SERVER;

    public string? UsageHint { get; init; }

    public int? MinArgs { get; init; }

    /// <summary>
    /// Owning plugin's <c>ModuleName</c>, set when a plugin registers the command.
    /// Used by the slow-frame profiler to attribute the callback's cost. Null for
    /// commands registered by the core itself.
    /// </summary>
    public string? PluginName { get; init; }

    public override string ToString()
    {
        return $"Name: {Name}, Description: {Description}, ExecutableBy: {ExecutableBy}, " +
               $"UsageHint: {UsageHint}, MinArgs: {MinArgs}";
    }
}