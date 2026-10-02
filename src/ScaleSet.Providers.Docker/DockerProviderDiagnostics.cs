using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;

namespace ScaleSet.Providers.Docker;

internal static class DockerProviderDiagnostics
{
    private static readonly ActivitySource ActivitySource = new("ScaleSet.Providers.Docker");
    private static readonly Meter Meter = new("ScaleSet.Providers.Docker");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "scaleset.docker.requests",
        unit: "{request}",
        description: "Docker Engine requests sent by the scale-set provider.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "scaleset.docker.request.duration",
        unit: "s",
        description: "Duration of Docker Engine requests.");

    public static Activity? Start(string operation, HttpMethod method)
    {
        Activity? activity = ActivitySource.StartActivity("scaleset.docker.request", ActivityKind.Internal);
        activity?.SetTag("http.request.method", NormalizeMethod(method.Method));
        activity?.SetTag("scaleset.docker.operation", NormalizeOperation(operation));
        return activity;
    }

    public static void Record(string operation, HttpMethod method, HttpStatusCode? statusCode, double durationSeconds)
    {
        string statusClass = "none";
        if (statusCode is HttpStatusCode status)
        {
            int numericStatus = (int)status;
            if (numericStatus < 300)
            {
                statusClass = "2xx";
            }
            else if (numericStatus < 400)
            {
                statusClass = "3xx";
            }
            else if (numericStatus < 500)
            {
                statusClass = "4xx";
            }
            else
            {
                statusClass = "5xx";
            }
        }
        TagList tags = new()
        {
            { "operation", NormalizeOperation(operation) },
            { "method", NormalizeMethod(method.Method) },
            { "status_class", statusClass },
        };
        Requests.Add(1, tags);
        Duration.Record(Math.Max(0, durationSeconds), tags);
    }

    public static void SetStatus(Activity? activity, HttpStatusCode? statusCode, bool failed)
    {
        if (activity is null)
        {
            return;
        }

        if (statusCode is not null)
        {
            activity.SetTag("http.response.status_code", (int)statusCode.Value);
        }

        if (failed)
        {
            activity.SetStatus(ActivityStatusCode.Error);
        }
    }

    private static string NormalizeOperation(string operation) => operation switch
    {
        "version" => "version",
        "info" => "info",
        "inspect-image" => "inspect-image",
        "pull-image" => "pull-image",
        "list-containers" => "list-containers",
        "inspect-container" => "inspect-container",
        "create-container" => "create-container",
        "start-container" => "start-container",
        "remove-container" => "remove-container",
        _ => "other",
    };

    private static string NormalizeMethod(string method) => method.ToUpperInvariant() switch
    {
        "GET" => "GET",
        "POST" => "POST",
        "DELETE" => "DELETE",
        _ => "OTHER",
    };
}
