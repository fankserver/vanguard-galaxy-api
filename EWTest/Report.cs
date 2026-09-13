using System;
using System.Collections.Generic;
using System.Text;

namespace EWTest;

/// <summary>One live assertion. The 'suggestedAction' is the pointer to what a
/// game-update break must fix — it names the binding/contract to re-inspect.</summary>
public sealed class CheckResult
{
    public string Check = "";
    public string Status = "pass"; // pass | fail | skip
    public string Message = "";
    public string Expected = "";
    public string Actual = "";
    public string SuggestedAction = "";
    public long ElapsedMs;

    public bool Failed => Status == "fail";
}

/// <summary>A named group of checks (e.g. "availability", "world-authoring").</summary>
public sealed class SuiteResult
{
    public string Id = "";
    public string Name = "";
    public List<CheckResult> Results = new();
}

/// <summary>Hand-rolled JSON writer matching the shared protocol in docs/development/e2e-tests.md
/// and tooled by tools/e2e.py. Avoids any JSON dependency (the game must not be assumed to
/// provide Newtonsoft.Json, per CONTRIBUTING).</summary>
public static class Json
{
    public static string Escape(string? value)
    {
        if (value == null) return "null";
        var sb = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return "\"" + sb + "\"";
    }
}

/// <summary>The machine-readable report written by the harness and parsed by tools/e2e.py.</summary>
public sealed class E2EReport
{
    public const int Schema = 1;
    public Dictionary<string, string> Meta = new();
    public List<SuiteResult> Suites = new();

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"schema\":").Append(Schema).Append(',');

        sb.Append("\"meta\":{");
        bool first = true;
        foreach (var kv in Meta)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Json.Escape(kv.Key)).Append(':').Append(Json.Escape(kv.Value));
        }
        sb.Append("},");

        sb.Append("\"suites\":[");
        for (int i = 0; i < Suites.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var suite = Suites[i];
            sb.Append("{");
            sb.Append("\"id\":").Append(Json.Escape(suite.Id)).Append(',');
            sb.Append("\"name\":").Append(Json.Escape(suite.Name)).Append(',');
            sb.Append("\"results\":[");
            for (int j = 0; j < suite.Results.Count; j++)
            {
                if (j > 0) sb.Append(',');
                var r = suite.Results[j];
                sb.Append('{');
                sb.Append("\"check\":").Append(Json.Escape(r.Check)).Append(',');
                sb.Append("\"status\":").Append(Json.Escape(r.Status)).Append(',');
                sb.Append("\"message\":").Append(Json.Escape(r.Message)).Append(',');
                sb.Append("\"expected\":").Append(Json.Escape(r.Expected)).Append(',');
                sb.Append("\"actual\":").Append(Json.Escape(r.Actual)).Append(',');
                sb.Append("\"suggestedAction\":").Append(Json.Escape(r.SuggestedAction)).Append(',');
                sb.Append("\"elapsedMs\":").Append(r.ElapsedMs);
                sb.Append('}');
            }
            sb.Append("]");
            sb.Append("}");
        }
        sb.Append("],");

        int passed = 0, failed = 0, skipped = 0;
        foreach (var suite in Suites)
            foreach (var r in suite.Results)
            {
                if (r.Status == "pass") passed++;
                else if (r.Status == "fail") failed++;
                else skipped++;
            }
        int total = passed + failed + skipped;
        sb.Append("\"summary\":{\"passed\":").Append(passed)
          .Append(",\"failed\":").Append(failed)
          .Append(",\"skipped\":").Append(skipped)
          .Append(",\"total\":").Append(total).Append("}");
        sb.Append('}');
        return sb.ToString();
    }
}

/// <summary>Tiny helpers to build checks without boilerplate.</summary>
public static class Check
{
    public static CheckResult Pass(string name, string detail = "")
        => new() { Check = name, Status = "pass", Message = detail, Actual = detail };

    public static CheckResult Skip(string name, string reason)
        => new() { Check = name, Status = "skip", Message = reason, Actual = reason };

    public static CheckResult Fail(string name, string actual, string expected, string suggested)
        => new() { Check = name, Status = "fail", Message = actual, Expected = expected, Actual = actual, SuggestedAction = suggested };

    /// <summary>Run a check, converting an exception into a timed failure.</summary>
    public static CheckResult Run(string name, string expected, Action body, string suggestedAction = "")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            body();
            sw.Stop();
            return new CheckResult { Check = name, Status = "pass", Message = "ok",
                Expected = expected, Actual = expected, ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CheckResult { Check = name, Status = "fail", Message = ex.Message,
                Expected = expected, Actual = ex.GetType().Name + ": " + ex.Message,
                SuggestedAction = suggestedAction, ElapsedMs = sw.ElapsedMilliseconds };
        }
    }

    /// <summary>Record a service's availability, failing only when it signals real breakage
    /// (binding failure or unsupported game), skipping intentional disables.</summary>
    public static CheckResult Availability(string name, VGModAPI.ServiceAvailability availability, string bindingHint)
    {
        switch (availability.Reason)
        {
            case VGModAPI.ServiceUnavailableReason.None:
                return Pass(name, "available");
            case VGModAPI.ServiceUnavailableReason.Disabled:
                return Skip(name, "disabled by configuration: " + availability.Detail);
            default:
                return Fail(name,
                    availability.Reason + ": " + availability.Detail,
                    "available",
                    "Service '" + name + "' is unavailable (" + availability.Reason + "). " +
                    "Re-inspect " + bindingHint + " in the updated Assembly-CSharp.dll and " +
                    "reconcile BindingCatalog before adding the new game hash.");
        }
    }
}
