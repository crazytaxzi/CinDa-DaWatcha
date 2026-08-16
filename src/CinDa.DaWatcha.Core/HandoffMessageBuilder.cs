using System.Text;

namespace CinDa.DaWatcha.Core;

public static class HandoffMessageBuilder
{
    public static string Build(HandoffBatch batch)
    {
        var text = new StringBuilder();
        text.AppendLine("Process handoff batch");
        text.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        text.AppendLine($"Conversation: {batch.ConversationUuid}");
        text.AppendLine();
        text.AppendLine("The following watched tasks reached a terminal state:");

        foreach (var item in batch.Events)
        {
            text.AppendLine();
            text.AppendLine($"## {item.Watch.Id}");
            text.AppendLine($"- PID: {item.Watch.Process.Pid}");
            text.AppendLine($"- Process: {item.Watch.Process.Name}");
            text.AppendLine($"- Trigger: {item.Trigger}");
            text.AppendLine($"- Observed: {item.OccurredAt:O}");
            text.AppendLine($"- Detail: {item.Detail}");
            text.AppendLine();
            text.AppendLine(item.Watch.PassalongMessage.Trim());
        }

        text.AppendLine();
        text.AppendLine($"CinDa-DaWatcha-ID: {Guid.NewGuid():N}");
        return text.ToString().TrimEnd();
    }

    public static string BuildFailure(
        HandoffBatch batch, IReadOnlyList<string> errors)
    {
        var text = new StringBuilder();
        text.AppendLine("CinDa-DaWatcha handoff failure");
        text.AppendLine($"Original conversation: {batch.ConversationUuid}");
        text.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        text.AppendLine();
        text.AppendLine("The monitor failed twice while preparing a handoff.");
        foreach (var error in errors)
            text.AppendLine($"- {error}");
        text.AppendLine();
        text.AppendLine("Affected watches:");
        foreach (var item in batch.Events)
            text.AppendLine($"- {item.Watch.Id} (PID {item.Watch.Process.Pid})");
        text.AppendLine();
        text.AppendLine("Please diagnose the browser handoff and preserve this context.");
        text.AppendLine();
        text.Append($"CinDa-DaWatcha-ID: {Guid.NewGuid():N}");
        return text.ToString();
    }
}
