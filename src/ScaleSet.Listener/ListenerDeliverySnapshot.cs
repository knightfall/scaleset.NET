using System.Collections.ObjectModel;
using ScaleSet.Client;

namespace ScaleSet.Listener;

internal static class ListenerDeliverySnapshot
{
    private const int MaximumJobs = 16384;
    private const int MaximumLabels = 1024;
    private const int MaximumTextLength = 1048576;

    internal static RunnerScaleSetMessage Create(RunnerScaleSetMessage message)
    {
        ValidateStatistics(message.Statistics);
        if (message.JobAvailableMessages is null || message.JobAssignedMessages is null ||
            message.JobStartedMessages is null || message.JobCompletedMessages is null ||
            (long)message.JobAvailableMessages.Count + message.JobAssignedMessages.Count +
                message.JobStartedMessages.Count + message.JobCompletedMessages.Count > MaximumJobs)
        {
            throw new InvalidDataException("The listener delivery has invalid or oversized job collections.");
        }

        int remainingText = MaximumTextLength;
        return message with
        {
            JobAvailableMessages = Copy(message.JobAvailableMessages, ref remainingText),
            JobAssignedMessages = Copy(message.JobAssignedMessages, ref remainingText),
            JobStartedMessages = Copy(message.JobStartedMessages, ref remainingText),
            JobCompletedMessages = Copy(message.JobCompletedMessages, ref remainingText),
        };
    }

    internal static void ValidateStatistics(RunnerScaleSetStatistic? statistics)
    {
        if (statistics is { TotalAvailableJobs: < 0 } or { TotalAcquiredJobs: < 0 } or
            { TotalAssignedJobs: < 0 } or { TotalRunningJobs: < 0 } or
            { TotalRegisteredRunners: < 0 } or { TotalBusyRunners: < 0 } or { TotalIdleRunners: < 0 })
        {
            throw new InvalidDataException("The listener delivery contains negative statistics.");
        }
    }

    private static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> jobs, ref int remainingText) where T : JobMessage
    {
        var snapshot = new T[jobs.Count];
        for (int index = 0; index < snapshot.Length; index++)
        {
            T job = jobs[index] ?? throw new InvalidDataException("The listener delivery contains a null job.");
            CountText(job.RepositoryName, ref remainingText);
            CountText(job.OwnerName, ref remainingText);
            CountText(job.JobId, ref remainingText);
            CountText(job.JobWorkflowRef, ref remainingText);
            CountText(job.JobDisplayName, ref remainingText);
            CountText(job.EventName, ref remainingText);
            if (job is JobStartedMessage started)
            {
                CountText(started.RunnerName, ref remainingText);
            }

            if (job is JobCompletedMessage completed)
            {
                CountText(completed.Result, ref remainingText);
                CountText(completed.RunnerName, ref remainingText);
            }

            var labels = CopyLabels(job.RequestLabels, ref remainingText);
            snapshot[index] = job is JobAvailableMessage available
                ? (T)(JobMessage)(available with { AcquireJobUrl = string.Empty, RequestLabels = labels })
                : job with { RequestLabels = labels };
        }

        return Array.AsReadOnly(snapshot);
    }

    private static ReadOnlyCollection<string>? CopyLabels(IReadOnlyList<string>? labels, ref int remainingText)
    {
        if (labels is null)
        {
            return null;
        }

        if (labels.Count > MaximumLabels)
        {
            throw new InvalidDataException("The listener delivery contains too many job labels.");
        }

        var snapshot = new string[labels.Count];
        for (int index = 0; index < snapshot.Length; index++)
        {
            string label = labels[index] ?? throw new InvalidDataException("The listener delivery contains a null job label.");
            CountText(label, ref remainingText);
            snapshot[index] = label;
        }

        return Array.AsReadOnly(snapshot);
    }

    private static void CountText(string? text, ref int remainingText)
    {
        remainingText -= text?.Length ?? 0;
        if (remainingText < 0)
        {
            throw new InvalidDataException("The listener delivery exceeds the maximum text size.");
        }
    }
}
