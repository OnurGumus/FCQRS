using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;

// docs:workflow
// The service that sends a welcome. A call gets a key that is the same for
// every attempt of one step, so the service can recognise a repeat.
public interface IWelcomes
{
    Task<bool> Send(string key, string owner);
}

// Welcomes a new account's owner. The input is the owner's name.
public sealed class WelcomeWorkflow(IWelcomes welcomes)
    : Workflow<string, string>("Welcome")
{
    private static readonly RetryPolicy Policy =
        Retry.times(3, TimeSpan.FromSeconds(10));

    public override async WorkflowTask<string> Run(
        WorkflowContext ctx, string owner)
    {
        await ctx.Activity("send-welcome", Policy,
            attempt => welcomes.Send(attempt.IdempotencyKey, owner));
        return "welcomed";
    }
}
// docs:end
