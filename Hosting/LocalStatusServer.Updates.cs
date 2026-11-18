using System.Text.Json;

namespace Balsoft.Hive.BDC.Hosting;

/// <summary>The Updates part of the Service page (boards P3 to P6); only an Azure instance has it.</summary>
public sealed partial class LocalStatusServer
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>Set in Azure run mode; a local install is updated by Setup from the Windows app.</summary>
    public SelfUpdate? Updates { get; set; }

    private async Task RouteUpdates(ApiCall call)
    {
        if (Updates is null)
        {
            Respond(call, 404, new { error = "This instance is updated by Setup from the Windows app." });
            return;
        }
        string by = call.User ?? "someone";
        switch ((call.Method, call.Path))
        {
            case ("GET", "/service/updates"): Respond(call, 200, JsonSerializer.SerializeToNode(Updates.Describe(), WebJson)!); return;
            case ("POST", "/service/updates/check"): Answer(call, await Updates.Check(CancellationToken.None)); return;
            case ("POST", "/service/updates/run"): Answer(call, await Updates.Run(by, CancellationToken.None)); return;
            case ("POST", "/service/updates/schedule"): Answer(call, Updates.Schedule(by, NextLocal(ReadAt(call.Body)))); return;
            case ("POST", "/service/updates/cancel"): Answer(call, Updates.Cancel(by)); return;
            case ("POST", "/service/updates/return"): Answer(call, Updates.Return(by)); return;
            default: Respond(call, 404, new { error = "not found" }); return;
        }
    }

    private void Answer(ApiCall call, UpdateOutcome outcome)
        => Respond(call, outcome.Ok ? 200 : 409, new { ok = outcome.Ok, message = outcome.Message });

    /// <summary>"22:00" from the body, else 22:00.</summary>
    private static TimeOnly ReadAt(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.TryGetProperty("at", out var at) && TimeOnly.TryParseExact(at.GetString(), "HH:mm", out var time))
                return time;
        }
        catch (JsonException) { }
        return new TimeOnly(22, 0);
    }

    /// <summary>The next time of day <paramref name="at"/> in the instance's time zone (app setting TZ).</summary>
    private static DateTimeOffset NextLocal(TimeOnly at)
    {
        var zone = TimeZoneInfo.Local;
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var day = now.Date.Add(at.ToTimeSpan());
        if (day <= now.DateTime) day = day.AddDays(1);
        return new DateTimeOffset(day, zone.GetUtcOffset(day));
    }
}