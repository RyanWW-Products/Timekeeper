# Email-based timecards

Use this workflow for work reconstructed from emails and other Microsoft 365 activity. The shared email export agent prepares an Excel workbook. Timekeeper matches its activities to Quickbase, lets you review the timecards, and writes them only when you submit.

## Set up the shared agent

The agent owner copies [Timekeeper Email Export Agent Instructions.txt](Timekeeper%20Email%20Export%20Agent%20Instructions.txt) into the Microsoft 365 agent's instructions and uploads [Timekeeper Email Export Agent Knowledge.txt](Timekeeper%20Email%20Export%20Agent%20Knowledge.txt) as a knowledge file. The instructions contain the complete import contract; the knowledge file adds reconciliation rules, reporting details, and a fictional ledger example. The owner shares this agent with the team. Employees use the shared agent; they do not each create an agent.

In Agent Builder, open **Configure > Knowledge**, select the search bar, and add **My emails**. Add permitted Teams/meeting sources if needed. Sharing the agent does not give employees access to the owner's email. Employees use their own Microsoft 365 work accounts, and the owner should verify personal-mail access with a team member before rollout. Source availability depends on licensing and tenant configuration. [Microsoft: Add knowledge sources](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/agent-builder-add-knowledge)

Under **Capabilities**, enable **Create documents, charts, and code** if the toggle is shown. Some Agent Builder experiences include code interpreter automatically. This capability supports downloadable Excel workbooks and optional PDFs. Test that the agent produces an actual `.xlsx` download and ask employees to save it while the session is active. [Microsoft: Build agents](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/agent-builder-build-agents), [Microsoft: Code interpreter](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/code-interpreter)

The agent must retrieve stable IDs for individual messages or events. A conversation ID or generated citation label is insufficient for duplicate checks. Availability of complete sent/received inventories and usable source IDs must be tested in your tenant; adding instructions does not guarantee either. If stable IDs are unavailable, the agent asks whether an approved export containing them can be supplied. It must not invent IDs or claim an import-ready workbook is possible until the source limitation is resolved. If file creation is unavailable, it must say that too.

Never put Quickbase or Toggl tokens in the agent, workbook, or PDF. Configure your own Quickbase credentials in Timekeeper. Toggl credentials are optional for this workflow. Do not embed an employee's mailbox export as shared agent knowledge.

## Prepare and import a day

1. Give the email agent the reporting date, your verified Quickbase sign-in email, and the timezone configured in Timekeeper. Confirm that it is reviewing your own mailbox.
2. Confirm which incoming messages you actually reviewed, any meeting attendance, and the agent's proposed grouped activities and minutes. Resolve its questions in chat. The agent reconciles every retrieved message to timed work, a duplicate, a no-action/excluded item, or a question needing resolution. Email timestamps do not establish how long you worked.
3. Confirm the billing choices and any disclosed gaps in the source data. The agent must resolve remaining activity questions and reconcile entries and totals before final export. Download the `.xlsx` workbook. An optional PDF and any separate ledger are for personal review only.
4. Choose **Import email Excel** in Timekeeper, or drag the downloaded workbook into the file drop area. For a multi-day workbook, select the dates to include. In **Match your activities**, select the matching Quickbase assignments and tasks using live lookup results. Use **Find assignment** if needed. The workbook supplies name hints, not authoritative Quickbase record IDs. If work already exists, select its actual record under **Already in Quickbase?** instead of submitting it again.
5. Confirm **These activities and confirmed minutes are mine**, then choose **Review timecards**. Review dates, grouped hours, descriptions, assignments, tasks, and billing. Timekeeper applies the selected rounding once per group. The agent supplies confirmed whole minutes without rounding or automatic additions.
6. Choose **Write to Quickbase** after reviewing the result. Check **Submission history** for the saved receipt.

Keep related work together, but use separate rows when its date, assignment, task, or billing choice differs. Each source message/event belongs to one imported activity. Re-exporting a workbook must preserve the underlying source IDs so a new filename cannot disguise previously submitted work.

In **Settings > Time policy > Email imports**, choose confirmed minutes without additional rounding, or round grouped time up to 5 or 15 minutes. The default is 5 minutes. Automatic Timecards and weekday fill are off for email imports by default; select **Apply my Timecards and weekday fill settings to email imports** to enable your existing daily defaults.

## What the agent checks and reports

The agent keeps a raw activity ledger and a reconciliation ledger so a polished report cannot hide missing classifications. Every confirmed worked matter must appear in the main report and import. Incoming mail you did not review or act on gets an explicit no-work disposition without invented hours. Related work is consolidated, with separate rows when date, assignment, task, or billing requires them.

The optional PDF includes the employee/date, executive summary, a dashboard of verified metrics, numbered matter entries, separate received/sent communications and other relevant activity, computed totals, coverage limitations, and an estimate disclosure. Search-result hits, unique messages, threads, and timecard entries remain separate counts. Unavailable sources are labeled unavailable, not zero.

The PDF reports confirmed minutes before Timekeeper's optional rounding and daily additions. Billable, non-billable, and **Quickbase default / unclassified** minutes sum to the total. A default billing choice is not counted as non-billable or treated as a known billable outcome. The final app review and saved Quickbase receipt show the later application result.

Ledgers stay in the authorized chat or a separate review artifact. Do not add ledger worksheets to the import workbook. Its optional `Evidence` sheet contains only source IDs referenced by `Activities`, without excluded/personal/no-action ledger records. Complete mailbox coverage and stable IDs still depend on the actual Microsoft 365 tools available; the instructions cannot guarantee them.

## Billing during review

The **Billing** dropdown offers **Quickbase default**, **Billable**, and **Non-billable** while retaining the same project, assignment, and task. A green indicator identifies a billable override; dim red identifies non-billable; gray means Quickbase will determine the default when saved. Read the label as well as the color. A blank workbook value selects **Quickbase default**.

An override requires a writable field supported by the Quickbase app and your account's permissions. If Timekeeper reports that an override is unavailable or rejected, resolve the field or permission issue before submitting that choice. The application cannot grant additional Quickbase permissions.

## Workbook contract

The machine-readable format is version 1. Worksheet names and headers must match exactly. Store dates and IDs as text, use plain values, and omit formulas, macros, external workbook links, and merged data cells. The maximum file size is 10 MB, the maximum range is 31 dates, and the maximum activity count is 1,000.

`Metadata` has columns `key,value` and these keys:

| Key | Value |
| --- | --- |
| `format` | `timekeeper_email_activity` |
| `schema_version` | `1` |
| `employee_email` | Email matching Timekeeper's verified Quickbase account |
| `time_zone` | Windows or IANA timezone equivalent to the Timekeeper profile |
| `period_start` | `YYYY-MM-DD` |
| `period_end` | Inclusive `YYYY-MM-DD` |
| `generated_at_utc` | ISO 8601 UTC timestamp ending in `Z` |
| `employee_confirmed` | `true`, only after confirmation in chat |
| `retrieval_limitations` | Specific limitations, or `none` when coverage is verified |

`Activities` has these columns in order:

```text
activity_id,date,minutes,matter_hint,assignment_hint,task_hint,description,billable,time_basis,evidence_ids
```

- `activity_id` is unique, stable text of at most 256 characters.
- `date` falls within the metadata period. `minutes` is a positive whole number, at most 1,440, confirmed before rounding.
- Matter, assignment, and task hints help the user choose actual Quickbase relationships, with at most 1,000 characters per hint. Unknown assignment/task hints may be blank. The description records what the employee did, with at most 4,000 characters.
- `billable` is `true`, `false`, or blank for the Quickbase default.
- `time_basis` is `employee_confirmed_estimate` or `measured`. A group containing estimated time uses the first value.
- `evidence_ids` contains actual stable individual source IDs separated by semicolons. Each ID is at most 2,048 characters, is case-sensitive, and appears in only one activity across the workbook. Do not generate substitute IDs.

An optional `Evidence` sheet uses:

```text
evidence_id,source_type,timestamp_utc,direction,subject,action
```

Evidence corroborates the activity; it does not add time. Every Evidence ID must belong to Activities. Keep it concise and avoid raw message bodies or the complete reconciliation ledger. Estimated minutes remain estimates even after the workbook passes structural checks.

## If something goes wrong

- **Workbook rejected:** Correct the specific format, identity, date, confirmation, or source-ID error. Do not bypass a source-ID error by inventing new IDs.
- **Ambiguous assignment:** Select the intended live Quickbase relationship. Return to the agent if the actual activity or duration is uncertain.
- **Incomplete mailbox coverage:** Obtain the missing evidence or explicitly confirm the documented subset with the agent. Do not treat a relevance-ranked search as proof of complete coverage.
- **Partial or unknown submission:** Keep the receipt and use **Check selected result**. Do not resubmit the workbook under a new filename to try again; successful rows may already exist in Quickbase.
- **A regenerated activity mixes old and new work:** After resolving the receipt, ask the agent to split already-recorded and new work into separate activities with disjoint original source IDs and confirmed minutes. Link the old activity to its actual Quickbase record during mapping. Do not invent replacement IDs or automatically divide time by message count.
