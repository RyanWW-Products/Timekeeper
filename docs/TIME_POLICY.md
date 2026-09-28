Timekeeper confirmed time policy
===============================

This specification records the user's clarification following the initial reference review. It supersedes the conflicting clock-time and target-cap wording in the historical Copilot instructions under `Reference Material`.

Automatic time is based on the day's total hours. Start time, finish time, and crossing a particular clock time have no effect on Timecards or Misc internal additions. Dates and the configured timezone still determine which day an entry belongs to.

The default settings are a 0.17-hour Timecards entry and an 8.00-hour weekday target. Both are configurable per user. The target is a minimum for the fill calculation, not a cap on worked hours or the Timecards addition.

For an eligible day:

1. Count existing Quickbase time and new, rounded work that is not already represented in Quickbase. Count both billable and internal work, without counting a source entry twice.
2. Add the configured Timecards amount if enabled and not already recorded for that day. Add the full amount even if the day's total is already at or above the target, or this addition takes it above the target.
3. Add a separate Misc internal row only for the positive difference between the target and the total after Timecards. Add no zero-hour or negative-hour row.
4. Preserve all worked hours. Do not trim work, reduce the Timecards addition, or subtract time to force the total to the target.

Let E be all existing hours for that day, N be new work hours not already represented by E, T be the new Timecards addition (zero if disabled or already present), and H be the configured target:

```text
New Misc internal = max(0, H - (E + N + T))
Final daily total = E + N + T + New Misc internal
```

Existing Timecards and Misc internal rows are already included in E; do not add them again as new work. Reconcile source coverage before calculating N. Re-running a completed day must not resubmit the same work or automatic rows. Never alter previously written records silently to make a target fit.

These examples use the default settings, an eligible weekday, no prior Quickbase rows, and work totals after the configured rounding rule:

| Worked hours | New Timecards | New Misc internal | Final total |
| ---: | ---: | ---: | ---: |
| 7.00 | 0.17 | 0.83 | 8.00 |
| 7.83 | 0.17 | 0.00 | 8.00 |
| 7.90 | 0.17 | 0.00 | 8.07 |
| 8.00 | 0.17 | 0.00 | 8.17 |
| 9.00 | 0.17 | 0.00 | 9.17 |

Mixed work also counts toward the total: 7.00 billable hours and 0.50 tracked internal hours receive 0.17 Timecards and 0.33 Misc internal, totaling 8.00. With 7.00 work hours and a 0.17 Timecards row already recorded in Quickbase, only 0.83 Misc internal is added; no second Timecards row is created.

The reference workflow's weekday/weekend eligibility is a separate setting from clock time. This clarification does not explicitly change the existing weekend behavior (actual work only) or settle leave/holiday and zero-entry weekday behavior. Do not infer automatic full-day fill from an empty read. The existing request for confirmation when a fill gap exceeds three hours also remains separate from the arithmetic.

The application should calculate and validate these additions locally using exact duration data and decimal arithmetic. Export the effective settings with the source session so Copilot uses the same policy. The review table should distinguish worked time, the Timecards addition, and Misc internal.

Required implementation checks include all example rows, mixed billable/internal work, a previously recorded Timecards row, repeated reads, configurable target/addition values, and equal-duration work at different times of day yielding the same additions.
