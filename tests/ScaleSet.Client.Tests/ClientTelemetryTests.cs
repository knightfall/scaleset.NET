using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ScaleSet.Client.Tests;

public sealed class ClientTelemetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientEmitsCorrelatedActivitiesAndBoundedMetricsWithSecondsDuration(bool fails)
    {
        Assert.Equal("ScaleSet.Client", ScaleSetClientTelemetry.ActivitySourceName);
        Assert.Equal("ScaleSet.Client", ScaleSetClientTelemetry.MeterName);
        using var root = new Activity("client-telemetry-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        ActivityTraceId traceId = root.TraceId;
        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScaleSetClientTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId)
                {
                    activities.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(activityListener);
        var counts = new List<(long Value, KeyValuePair<string, object?>[] Tags)>();
        var durations = new List<(double Value, string? Unit)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ScaleSetClientTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (Activity.Current?.TraceId == traceId && instrument.Name == "scaleset.client.requests")
            {
                counts.Add((value, tags.ToArray()));
            }
        });
        meterListener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (Activity.Current?.TraceId == traceId && instrument.Name == "scaleset.client.request.duration")
            {
                durations.Add((value, instrument.Unit));
            }
        });
        meterListener.Start();
        using var handler = new TelemetryHandler(fails);
        using var http = new HttpClient(handler);
        using var client = new ScaleSetClient(
            GitHubTarget.Parse("https://github.com/example"),
            ScaleSetCredentials.FromPersonalAccessToken("telemetry-pat-sentinel"),
            new ScaleSetClientOptions { MaxReadOnlyRetries = 0 },
            http,
            timeProvider: new TickClock());

        if (fails)
        {
            ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
                () => client.GetScaleSetAsync(42, TestContext.Current.CancellationToken));
            Assert.Equal(500, exception.StatusCode);
        }
        else
        {
            RunnerScaleSet? result = await client.GetScaleSetAsync(42, TestContext.Current.CancellationToken);
            Assert.Equal(42, result?.Id);
        }

        Assert.Equal(3, activities.Count);
        Assert.All(activities, activity =>
        {
            Assert.Equal(ActivityKind.Internal, activity.Kind);
            Assert.Equal(traceId, activity.TraceId);
            Assert.Equal("scaleset.client.request", activity.OperationName);
            Assert.DoesNotContain(activity.TagObjects, tag => tag.Key.Contains("token", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(activity.TagObjects, tag => tag.Value?.ToString()?.Contains("telemetry-pat-sentinel", StringComparison.Ordinal) == true);
        });
        Assert.Equal(fails ? ActivityStatusCode.Error : ActivityStatusCode.Unset, activities[^1].Status);
        Assert.Equal(3, counts.Count);
        Assert.All(counts, count =>
        {
            Assert.Equal(1, count.Value);
            Assert.Equal(["dependency", "method", "status_class"], count.Tags.Select(tag => tag.Key).Order(StringComparer.Ordinal));
        });
        Assert.Equal(fails ? "5xx" : "2xx", counts[^1].Tags.Single(tag => tag.Key == "status_class").Value);
        Assert.Equal(3, durations.Count);
        Assert.All(durations, duration =>
        {
            Assert.Equal("s", duration.Unit);
            Assert.Equal(0.007, duration.Value, precision: 6);
        });
    }

    private sealed class TickClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, 7);

        public override DateTimeOffset GetUtcNow() => new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class TelemetryHandler(bool fails) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (request.RequestUri.AbsolutePath == "/actions/runner-registration")
            {
                string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":4102444800}"))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    url = "https://admin.example/tenant/",
                    token = $"eyJhbGciOiJSUzI1NiJ9.{payload}.signature",
                })));
            }

            return Task.FromResult(Json(fails ? HttpStatusCode.InternalServerError : HttpStatusCode.OK,
                fails ? "{\"message\":\"unavailable\"}" : "{\"id\":42}"));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
