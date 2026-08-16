using System.Text;
using System.Globalization;

namespace CinDa.DaWatcha.Core;

public static class HandoffMessageBuilder
{
    public static string BuildBlockedWarning(
        TrainingJob job,
        ParticipantEvaluation blocked,
        string deliveryId)
    {
        var participant = blocked.Participant;
        var text = new StringBuilder();
        text.AppendLine("CinDa-DaWatcha blocked application warning");
        text.AppendLine(CultureInfo.InvariantCulture, $"Job: {job.Id}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Application: {participant.Id}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"PID: {participant.Process.Pid}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Process: {participant.Process.Name}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Application update: {participant.UpdatedAtUtc:O}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Reason: {blocked.Detail}");
        if (!string.IsNullOrWhiteSpace(participant.Detail))
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Application detail: {participant.Detail.Trim()}");
        text.AppendLine();
        text.AppendLine("The local training run has not advanced. Review this " +
            "application and provide recovery guidance without starting a " +
            "duplicate run.");
        text.AppendLine();
        text.Append(CultureInfo.InvariantCulture,
            $"CinDa-DaWatcha-ID: {deliveryId}");
        return text.ToString();
    }

    public static string BuildFinal(
        TrainingJob job,
        IReadOnlyList<ParticipantEvaluation> participants,
        string deliveryId)
    {
        var failed = participants.Any(item =>
            item.State == ParticipantOperationalState.Failed);
        var text = new StringBuilder();
        text.AppendLine("CinDa-DaWatcha training handoff");
        text.AppendLine(CultureInfo.InvariantCulture, $"Job: {job.Id}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Outcome: {(failed ? "FAILURE" : "SUCCESS")}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Started: {job.CreatedAtUtc:O}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Job update: {job.UpdatedAtUtc:O}");
        if (!string.IsNullOrWhiteSpace(job.Summary))
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Job summary: {job.Summary.Trim()}");

        foreach (var evaluation in participants)
        {
            var participant = evaluation.Participant;
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Application: {participant.Id}");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Result: {evaluation.State}");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"PID: {participant.Process.Pid}");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Process: {participant.Process.Name}");
            if (participant.ExitCode is { } exitCode)
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"Exit code: {exitCode}");
            if (participant.FinishedAtUtc is { } finished)
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"Finished: {finished:O}");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Detail: {evaluation.Detail}");
            if (!string.IsNullOrWhiteSpace(participant.HandoffMessage))
            {
                text.AppendLine();
                text.AppendLine(participant.HandoffMessage.Trim());
            }
        }

        text.AppendLine();
        text.AppendLine("All expected applications are terminal and no matching " +
            "process remains active.");
        text.AppendLine();
        text.Append(CultureInfo.InvariantCulture,
            $"CinDa-DaWatcha-ID: {deliveryId}");
        return text.ToString();
    }
}
