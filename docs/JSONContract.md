{
  "submissionId": "string",
  "status": "completed | compile_error | timed_out | runner_error",
  "run": {
    "testRunId": "string|null",
    "overallOutcome": "Passed | Failed | Skipped | Unknown",
    "createdAt": "ISO-8601|null",
    "startedAt": "ISO-8601|null",
    "finishedAt": "ISO-8601|null",
    "durationMs": 0,
    "counters": {
      "total": 0,
      "executed": 0,
      "passed": 0,
      "failed": 0,
      "skipped": 0,
      "error": 0,
      "timeout": 0,
      "aborted": 0,
      "inconclusive": 0
    }
  },
  "tests": [
    {
      "id": "string",
      "name": "string",
      "className": "string|null",
      "fullyQualifiedName": "string|null",
      "outcome": "Passed | Failed | Skipped | Unknown",
      "durationMs": 0,
      "startedAt": "ISO-8601|null",
      "finishedAt": "ISO-8601|null",
      "message": "string|null",
      "stackTrace": "string|null"
    }
  ],
  "diagnostics": {
    "stdout": "string|null",
    "stderr": "string|null",
    "trxPath": "string|null",
    "note": "string|null"
  }
}

---

- run.testRunId ← <TestRun id="...">

- run.createdAt/startedAt/finishedAt ← <Times creation/start/finish>

- run.overallOutcome ← <ResultSummary outcome="Failed">

- run.counters.* ← <Counters total="…" executed="…" passed="…" failed="…" notExecuted="…" …>

    - Map notExecuted → skipped in your JSON.

- diagnostics.stdout ← <ResultSummary><Output><StdOut>…</StdOut>

Each item in tests[] comes from <UnitTestResult …> plus optional join to <TestDefinitions>:

- tests[i].id ← use executionId (best) or testId

- tests[i].name ← testName

- tests[i].durationMs ← duration="00:00:00.0200070"

- tests[i].message and stackTrace ← <ErrorInfo><Message> and <StackTrace>

- className ← from <TestDefinitions><UnitTest><TestMethod className="…"> by matching testId + executionId