using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

public static class TrxToJsonConverter
{
    // Flood-hardening: cap any single TRX-derived text field before it enters JSON.
    private const int MaxFieldChars = 16_000;

    public static string ConvertToJson(
        string submissionId,
        string status,     // "completed | compile_error | timed_out | runner_error"
        string trxFilePath,
        string? stderr = null,
        string? note = null)
    {
        // Always emit in the required status vocabulary
        status = NormalizeStatus(status);

        var diagnostics = new Dictionary<string, object?>
        {
            ["stdout"] = null,
            ["stderr"] = Trunc(string.IsNullOrWhiteSpace(stderr) ? null : stderr),
            ["trxPath"] = File.Exists(trxFilePath) ? trxFilePath : null,
            ["note"] = Trunc(string.IsNullOrWhiteSpace(note) ? null : note)
        };

        // If TRX missing, still output valid JSON
        if (!File.Exists(trxFilePath))
        {
            return Serialize(new Dictionary<string, object?>
            {
                ["submissionId"] = submissionId,
                ["status"] = status,
                ["run"] = EmptyRun(),
                ["tests"] = new List<object>(),
                ["diagnostics"] = diagnostics
            });
        }

        // Safer XML load settings (no DTD).
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        XDocument doc;
        using (var fs = File.OpenRead(trxFilePath))
        using (var xr = XmlReader.Create(fs, settings))
        {
            doc = XDocument.Load(xr, LoadOptions.None);
        }

        var root = doc.Root ?? throw new InvalidDataException("TRX root missing.");
        XNamespace ns = root.Name.Namespace;

        // ---- run.testRunId ----
        var testRunId = (string?)root.Attribute("id");

        // ---- Times ----
        var times = root.Element(ns + "Times");
        var createdAt  = NormalizeIso((string?)times?.Attribute("creation"));
        var startedAt  = NormalizeIso((string?)times?.Attribute("start"));
        var finishedAt = NormalizeIso((string?)times?.Attribute("finish"));

        // ---- ResultSummary ----
        var resultSummary = root.Element(ns + "ResultSummary");
        var overallOutcome = NormalizeOutcome((string?)resultSummary?.Attribute("outcome"));

        // Counters
        var countersEl = resultSummary?.Element(ns + "Counters");
        var counters = new Dictionary<string, int>
        {
            ["total"] = GetIntAttr(countersEl, "total"),
            ["executed"] = GetIntAttr(countersEl, "executed"),
            ["passed"] = GetIntAttr(countersEl, "passed"),
            ["failed"] = GetIntAttr(countersEl, "failed"),
            ["skipped"] = GetIntAttr(countersEl, "notExecuted"),
            ["error"] = GetIntAttr(countersEl, "error"),
            ["timeout"] = GetIntAttr(countersEl, "timeout"),
            ["aborted"] = GetIntAttr(countersEl, "aborted"),
            ["inconclusive"] = GetIntAttr(countersEl, "inconclusive"),
        };

        // StdOut
        var stdOut = (string?)resultSummary?
            .Element(ns + "Output")?
            .Element(ns + "StdOut");

        if (!string.IsNullOrWhiteSpace(stdOut))
            diagnostics["stdout"] = Trunc(stdOut);

        // TestDefinitions lookup: testId -> (className, fullyQualifiedName)
        var defLookup = root
            .Element(ns + "TestDefinitions")?
            .Elements(ns + "UnitTest")
            .Select(ut =>
            {
                var testId = (string?)ut.Attribute("id");
                var tm = ut.Element(ns + "TestMethod");
                return new
                {
                    TestId = testId,
                    ClassName = (string?)tm?.Attribute("className"),
                    FullyQualifiedName = (string?)tm?.Attribute("name") // often FQN
                };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.TestId))
            .ToDictionary(x => x.TestId!, x => (x.ClassName, x.FullyQualifiedName))
            ?? new Dictionary<string, (string? ClassName, string? FullyQualifiedName)>();

        var tests = new List<Dictionary<string, object?>>();

        var unitTestResults = root
            .Element(ns + "Results")?
            .Elements(ns + "UnitTestResult")
            ?? Enumerable.Empty<XElement>();

        foreach (var r in unitTestResults)
        {
            var executionId = (string?)r.Attribute("executionId");
            var testId      = (string?)r.Attribute("testId");
            var testName    = (string?)r.Attribute("testName") ?? "";

            var rawOutcome = (string?)r.Attribute("outcome");
            var outcome = NormalizeOutcome(rawOutcome);

            // Treat timeout as failure (your rule)
            if (string.Equals(rawOutcome, "Timeout", StringComparison.OrdinalIgnoreCase))
                outcome = "Failed";

            var durationMs = ParseDurationMs((string?)r.Attribute("duration"));
            var tStarted   = NormalizeIso((string?)r.Attribute("startTime"));
            var tFinished  = NormalizeIso((string?)r.Attribute("endTime"));

            var errorInfo = r.Element(ns + "Output")?.Element(ns + "ErrorInfo");
            var message = (string?)errorInfo?.Element(ns + "Message");
            var stack   = (string?)errorInfo?.Element(ns + "StackTrace");

            string? className = null;
            string? fqn = null;
            if (!string.IsNullOrWhiteSpace(testId) && defLookup.TryGetValue(testId!, out var info))
            {
                className = NullIfBlank(info.ClassName);
                fqn = NullIfBlank(info.FullyQualifiedName);
            }

            tests.Add(new Dictionary<string, object?>
            {
                ["id"] = !string.IsNullOrWhiteSpace(executionId) ? executionId : (testId ?? Guid.NewGuid().ToString("D")),
                ["name"] = Trunc(testName),
                ["className"] = Trunc(className),
                ["fullyQualifiedName"] = Trunc(fqn),
                ["outcome"] = outcome,
                ["durationMs"] = durationMs,
                ["startedAt"] = tStarted,
                ["finishedAt"] = tFinished,
                ["message"] = Trunc(NullIfBlank(message)),
                ["stackTrace"] = Trunc(NullIfBlank(stack))
            });
        }

        // run.durationMs from run Times
        var runDurationMs = ComputeRunDurationMs(startedAt, finishedAt);

        var run = new Dictionary<string, object?>
        {
            ["testRunId"] = NullIfBlank(testRunId),
            ["overallOutcome"] = overallOutcome,
            ["createdAt"] = createdAt,
            ["startedAt"] = startedAt,
            ["finishedAt"] = finishedAt,
            ["durationMs"] = runDurationMs,
            ["counters"] = counters
        };

        var rootJson = new Dictionary<string, object?>
        {
            ["submissionId"] = submissionId,
            ["status"] = status,
            ["run"] = run,
            ["tests"] = tests,
            ["diagnostics"] = diagnostics
        };

        return Serialize(rootJson);
    }

    // ---------------- helpers ----------------

    private static Dictionary<string, object?> EmptyRun() => new()
    {
        ["testRunId"] = null,
        ["overallOutcome"] = "Unknown",
        ["createdAt"] = null,
        ["startedAt"] = null,
        ["finishedAt"] = null,
        ["durationMs"] = 0,
        ["counters"] = new Dictionary<string, int>
        {
            ["total"] = 0, ["executed"] = 0, ["passed"] = 0, ["failed"] = 0, ["skipped"] = 0,
            ["error"] = 0, ["timeout"] = 0, ["aborted"] = 0, ["inconclusive"] = 0
        }
    };

    private static string Serialize(object o)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        return JsonSerializer.Serialize(o, opts);
    }

    private static int GetIntAttr(XElement? el, string attr)
        => int.TryParse((string?)el?.Attribute(attr), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string NormalizeOutcome(string? o)
    {
        if (string.IsNullOrWhiteSpace(o)) return "Unknown";
        return o switch
        {
            "Passed" => "Passed",
            "Failed" => "Failed",
            "Skipped" => "Skipped",
            "NotExecuted" => "Skipped",
            _ => string.Equals(o, "Timeout", StringComparison.OrdinalIgnoreCase) ? "Failed" : "Unknown"
        };
    }

    private static string NormalizeStatus(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "completed";
        s = s.Trim();
        return s.ToLowerInvariant() switch
        {
            "completed" => "completed",
            "compile_error" => "compile_error",
            "timed_out" => "timed_out",
            "runner_error" => "runner_error",
            // tolerate callers passing "Completed"
            "completed " => "completed",
            _ => "completed"
        };
    }

    private static int ParseDurationMs(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return 0;
        return TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var ts)
            ? (int)Math.Round(ts.TotalMilliseconds)
            : 0;
    }

    private static string? NormalizeIso(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToString("O")
            : null;
    }

    private static int ComputeRunDurationMs(string? startIso, string? endIso)
    {
        if (!DateTimeOffset.TryParse(startIso, null, DateTimeStyles.RoundtripKind, out var s)) return 0;
        if (!DateTimeOffset.TryParse(endIso, null, DateTimeStyles.RoundtripKind, out var e)) return 0;
        if (e < s) return 0;
        return (int)Math.Round((e - s).TotalMilliseconds);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? Trunc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Length <= MaxFieldChars) return s;
        return s.Substring(0, MaxFieldChars) + "\n...[TRUNCATED: field limit]...";
    }
}
