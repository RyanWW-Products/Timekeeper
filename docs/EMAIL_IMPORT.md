# Email-based timecards

Timekeeper 0.7.0 uses **Word for review, then a small Excel file for import**. The agent prepares the Word report first. Once you approve it, the agent exports only the data Timekeeper needs. Excel has no review pages, totals, communication ledger or formulas.

## Set up the shared agent

The owner replaces the [main instructions](Timekeeper%20Email%20Export%20Agent%20Instructions.txt) and all three context files: [Knowledge](Timekeeper%20Email%20Export%20Agent%20Knowledge.txt), [Import Format](Timekeeper%20Email%20Export%20Agent%20Import%20Format.txt), and [Report Format](Timekeeper%20Email%20Export%20Agent%20Report%20Format.txt). Follow the [owner setup guide](Timekeeper%20Email%20Export%20Agent%20Setup.txt); employees use the shared agent rather than creating their own.

Replace the old REVIEW workbook in the agent's knowledge with [Timekeeper Email Import Template.xlsx](templates/Timekeeper%20Email%20Import%20Template.xlsx). Remove superseded email-agent files so the agent receives one consistent contract. Keep a sanitized Josh Boyd report as a Word layout reference if available. The template is fictional and deliberately unapproved. The agent must replace all sample data and set confirmation to true only after approval of the actual Word report.

The agent needs authorized access to the employee's email and any additional sources used, plus document and workbook generation. Test using a team member's own account. Shared access does not grant access to the owner's mailbox, and these files do not install a Quickbase connector. Never put API tokens or employee mailbox exports into shared knowledge.

Update the app to **0.7.0 or later** for the new format. Quickbase setup is required for matching and submission. Toggl is optional; email-only employees leave its token blank in Settings.

## Prepare and import a day

1. Give the agent the date, your verified Quickbase email and the timezone configured in Timekeeper. It uses your authorized Microsoft 365 sources.
2. Review its Word report. It includes worked matters, underlying minutes, billing, evidence context, estimates, coverage gaps, communication accounting and projected totals. Email timestamps alone do not establish duration.
3. Request corrections if needed, then approve the work, minutes, billing, account, dates and disclosed limitations once. The agent creates the minimal Excel from that approved allocation. It reuses the analysis instead of repeating the entire mailbox search.
4. Choose **Import email Excel** or drag the downloaded workbook into its drop area. Briefly acknowledge that you approved the Word report and understand the limited duplicate checks. For multiple dates, choose the dates to include.
5. In **Match your activities**, select live Quickbase assignments and tasks. Use **Find assignment** as needed. Hints in Excel are names, not authoritative record IDs. Link work already entered to its actual record under **Already in Quickbase?**
6. Confirm the activities and minutes are yours, then choose **Review timecards**. Check dates, grouped hours, descriptions, assignments, tasks and billing.
7. Choose **Write to Quickbase** when ready. Keep the receipt in **Submission history**.

Word remains your supporting review file. You do not need to upload it into Timekeeper. Importing Excel and confirming its source do not write timecards.

## Time policy and billing

Excel contains approved positive whole minutes before app rounding. In **Settings > Time policy > Email imports**, choose no additional rounding or round grouped work up to 5 or 15 minutes. The default is 5 minutes. Word may show a separate quarter-hour projection; select 15-minute rounding to use that policy. Live mapping can change grouping and projected totals.

Automatic Timecards and weekday fill are off for email imports by default. Enable **Apply my Timecards and weekday fill settings to email imports** if wanted. Word shows these policy additions separately, and the new Excel excludes filler. Actual supported internal work is imported normally. The app preserves work over eight hours and the full configured Timecards addition.

The Billing dropdown appears only when Quickbase confirms Modify access to Billable Override. Other accounts see read-only billing. Defaults show **Billable - Default** or **Nonbillable - Default** when the rule and required source values can be read. Otherwise **Default unavailable** avoids guessing. Green means billable, dim red means nonbillable, and gray means unknown. Explicit non-billable case work keeps its case, assignment and task. An override without permission must be cleared deliberately or its permission issue resolved before submission.

Double-click Hours, or focus the box and press F2, for a last-minute duration change. Saving changes that row only and requires acknowledgement of the updated total. Original hours remain in the receipt. Make duration changes after matching and billing changes, which can regroup the proposal.

## Minimal workbook contract

Use exactly two worksheets, **Metadata** and **Timecards**, with exact row-1 headers, plain saved values and no extra columns or sheets. No title rows, totals, hours, formulas, merged cells, macros, external links, evidence IDs or review content.

Metadata has columns **key,value** and exactly four entries:

| Key | Value |
| --- | --- |
| format | timekeeper_email_import |
| employee_email | Email matching the verified Quickbase account |
| time_zone | Windows or IANA timezone equivalent to the app profile |
| employee_confirmed | true, only after actual Word approval |

Timecards has seven columns in this exact order:

```text
date,minutes,matter_hint,assignment_hint,task_hint,description,billable
```

- **date:** Local work date, preferably YYYY-MM-DD text.
- **minutes:** Approved positive whole minutes, before app rounding.
- **matter_hint:** Required matter/project/internal name.
- **assignment_hint** and **task_hint:** Descriptive matching hints; may be blank.
- **description:** Required concise account of the employee's work.
- **billable:** true, false, or blank for deliberate Quickbase default. Blank does not mean nonbillable.

Timekeeper derives dates from the rows. No schema version, reporting-period fields, generated timestamp, hours calculation or evidence sheet is needed. The app treats minimal-format rows as employee-confirmed estimates; measurement detail stays in Word.

Limits are 10 MiB, 1,000 rows, 31 inclusive calendar dates and 1,440 underlying minutes per date. Hints allow 1,000 characters each; descriptions allow 4,000. See [Import Format](Timekeeper%20Email%20Export%20Agent%20Import%20Format.txt) for complete generation rules.

Normal Excel date cells, General/text serials and unambiguous date text remain supported. ISO text avoids locale ambiguity: use 2026-10-12 rather than ambiguous 10/12/2026 text. Numeric text and common Boolean representations are accepted. Saved workbooks can remain open in Excel, but unsaved edits are not imported.

## Duplicate checks and corrections

The minimal format does not require stable message IDs. The app uses local row fingerprints from normalized employee, date, matter, assignment, task and description. Minutes, billing and row order are excluded. Editing only those values or changing the filename does not make the same work new.

Two otherwise identical rows are treated as duplicates even if minutes or billing differ. Distinct real activities need truthful descriptions of their distinct work. Do not add meaningless words to bypass a warning.

These checks are limited. Changed descriptions/hints, regrouping, another import format or work entered elsewhere may evade matching. Check live existing Quickbase time and receipts. Preserve unchanged approved descriptions and grouping when regenerating a workbook, especially after a partial submission.

For a correction, update the affected Word allocation, approve the revised proposal and generate a replacement Excel. Do not overwrite history or disguise previously sent rows as new work. Import alone is not a timecard replacement operation.

## Older workbooks

The app still accepts legacy **timekeeper_email_review** workbooks and rich **timekeeper_email_activity** workbooks under their existing contracts. Review files require local confirmation; rich Activities files retain their real-source-ID requirements. These are backward-compatible inputs. The revised agent should generate only the new **timekeeper_email_import** format and should not use the old REVIEW template.

## If something goes wrong

- **Unapproved workbook:** Review and approve Word, then have the agent regenerate Excel with the actual approval recorded. Do not change the template's false flag just to import sample data.
- **Invalid format or date:** Correct the named field or cell using the exact two-sheet contract. Use ISO dates when text is ambiguous.
- **Uncertain work or assignment:** Correct the work or duration with the agent; use live lookup for Quickbase relationships.
- **Incomplete coverage:** Obtain missing evidence or explicitly approve the documented subset. A relevance-ranked search does not prove complete mailbox coverage.
- **Partial or unknown submission:** Keep the receipt and use **Check selected result** before trying again. Some rows may already exist.
- **Deleted timecards:** Use **Recover deleted entries**, then import/read fresh data and review again. Renaming the workbook does not replace recovery.
