using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Commands;

public sealed class PlanCommand : ICommand
{
    public string Name => "plan";
    public string Description => "Toggle plan mode — restricts agent to read-only tools for safe exploration.";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        context.Session.Meta.PlanMode = !context.Session.Meta.PlanMode;

        if (context.Session.Meta.PlanMode)
        {
            context.Session.AddMessage(new Message
            {
                Role = MessageRole.User,
                Content = PlanModeInstructions.Activation("activated by user via /plan"),
            });
            context.Renderer.WriteInfo("Plan mode ON — agent is restricted to read-only tools.");
            context.Renderer.WriteInfo("Use /plan again or call ExitPlanMode to resume full access.");
        }
        else
        {
            context.Session.AddMessage(new Message
            {
                Role = MessageRole.User,
                Content = PlanModeInstructions.Deactivation,
            });
            context.Renderer.WriteInfo("Plan mode OFF — all tools available.");
        }

        return Task.CompletedTask;
    }
}
