# Email-based timecards

Timekeeper uses **Word for review, then a small Excel file for import**. The agent prepares the Word report first. Once you approve it, the agent exports only the data Timekeeper needs. Version 0.10.0 adds manual timecards alongside the matching table's direct project mappings, task search, billing refresh, decimal hours, weekday fill and direct submission. Excel has no review pages, totals, communication ledger or formulas.

## Set up the shared agent

The owner replaces the [main instructions](Timekeeper%20Email%20Export%20Agent%20Instructions.txt) and all three context files: [Knowledge](Timekeeper%20Email%20Export%20Agent%20Knowledge.txt), [Import Format](Timekeeper%20Email%20Export%20Agent%20Import%20Format.txt), and [Report Format](Timekeeper%20Email%20Export%20Agent%20Report%20Format.txt). Follow the [owner setup guide](Timekeeper%20Email%20Export%20Agent%20Setup.txt); employees use the shared agent rather than creating their own.

Replace the old REVIEW workbook in the agent's knowledge with [Timekeeper Email Import Template.xlsx](templates/Timekeeper%20Email%20Import%20Template.xlsx). Remove superseded email-agent files so the agent receives one consistent contract. Keep a sanitized Josh Boyd report as a Word layout reference if available. The template is fictional and deliberately unapproved. The agent must replace all sample data and set confirmation to true only after approval of the actual Word report.

The agent needs authorized access to the employee's email and any additional sources used, plus document and workbook generation. Test using a team member's own account. Shared access does not grant access to the owner's mailbox, and these files do not install a Quickbase connector. Never put API tokens or employee mailbox exports into shared knowledge.

Update the app to **0.10.0 or later** for the workflow below. The minimal workbook format is unchanged from 0.7.0. Quickbase setup is required for matching and submission. Toggl is optional; email-only employees leave its token blank in Settings. The manual-entry feature needs no changes to the shared agent's instructions or workbook template.

## Prepare and import a day

1. Give the agent the date, your verified Quickbase email and the timezone configured in Timekeeper. It uses your authorized Microsoft 365 sources.
2. Review its Word report. It includes worked matters, underlying minutes, billing, evidence context, estimates, coverage gaps, communication accounting and projected totals. Email timestamps alone do not establish duration.
3. Request corrections if needed, then approve the work, minutes, billing, account, dates and disclosed limitations once. The agent creates the minimal Excel from that approved allocation. It reuses the analysis instead of repeating the entire mailbox search.
4. Set **Work date** at the top of the dashboard to the workbook's date. Use **Open email Copilot agent** on the right to open the shared email agent. Choose **Import email Excel** or drag the downloaded workbook into the Email / Quickbase panel. Its indicator lights after you accept the import. Workbooks keep their original dates; a workbook with no activities on the chosen date must be imported under its correct date. If saved manual entries exist, choose **Append manual entries** to carry them into this review or **Delete manual entries**, which asks for separate confirmation. For multiple dates, choose the dates to include.
5. In **Review email activities**, check the assignment and task dropdowns. They start with the best plausible guesses from available Quickbase choices. Guesses are editable, not verified matches; unmatched fields remain **Choose**. Use **Find assignment** when needed. Search includes accessible assignments across employees and statuses such as Active, Case Intake, Proposal and Review. Use **Direct to project** in that window for case work without an assignment. Type a name or ID in the task dropdown to filter its options. Hints in Excel are names, not authoritative record IDs. Hours display as decimals: **0.25 h = 15 minutes**. Double-click an hours box or press F2 to edit. Billing is editable only with the required Quickbase permission.
6. For email work already entered, manually select its actual record under **Quickbase record**. Email activities are never linked automatically. Use **Add timecard** for work missing from the workbook, then edit or delete it in the separate **Added timecards** table. Choose **Fill weekdays to** and a target if wanted; **Add Timecards** is a separate option. These settings apply only to this import. Open **Show submission preview** to inspect exact grouped rows, manual work, additions, totals and warnings; **Back to activity table** returns to editing.
7. Choose **Review in Timekeeper dashboard** to use its final review and **Write to Quickbase**, or confirm the single checkbox in the table's submission preview and choose **Submit directly to Quickbase**. All changes clear that confirmation. Both routes use live permission/duplicate checks and save a receipt in **Submission history**. **Cancel** closes without submitting.

Word remains your supporting review file, and approval in M365 is still required before the agent creates Excel. You do not need to upload Word into Timekeeper. There is no separate opening acknowledgement or personal-confirmation checkbox during mapping. Importing Excel does not write timecards.

## Add work missing from the workbook

**Add timecard** opens the same editor available on the Timekeeper dashboard. Choose one of the loaded dates, exact decimal hours, a description and a verified project/assignment/task. New entries start with the configured internal project and task. They select nonbillable only if your Quickbase account permits that override; otherwise check the actual Quickbase default. A gray **Default unavailable** status does not establish nonbillable status. Lookup supports assignments or **Direct to project**, and task choices can be searched by name or ID.

Manual entries remain separate from email activities, and their exact hours count before weekday fill. The preview includes both. **Edit** preserves the entry's identity; **Delete** asks for confirmation and removes only the pending local entry. Either action clears review approval. Mapping changes and **Refresh billing** preserve manual entries. The original workbook and the agent's proposal are unchanged.

Pending drafts are saved locally per Quickbase profile when you accept the email review. **Cancel** discards that window's additions and edits and keeps the previous dashboard and saved draft. The append/delete choice before import is also committed only when the import is accepted; a failed or cancelled import preserves the saved entries. Drafts on excluded dates stay pending. You can start a manual-only day from the dashboard without Toggl or an email workbook; that mode has automatic Timecards and fill off.

Only an exact, unambiguous match can combine a manual entry with imported work or omit it because the same work already exists in Quickbase. Date, mapping, hours, description and compatible billing must agree. The preview identifies matches counted once or already in Quickbase. Uncertain similarities and separate manual entries remain for review with warnings. This does not automatically link email activities to Quickbase records.

After a write, drafts confirmed as created are retired. Failed, not-sent and unresolved entries remain pending. Reconcile unknown outcomes through **Submission history** before retrying them; keeping a draft is not proof that its prior write failed.

## Time policy and billing

Excel contains approved positive whole minutes before app rounding. In **Settings > Time policy > Email imports**, choose no additional rounding or round grouped work up to 5 or 15 minutes. The default is 5 minutes. Word may show a separate quarter-hour projection; select 15-minute rounding to use that policy. Live mapping can change grouping and projected totals.

Automatic Timecards and weekday fill are off for email imports by default. Set persistent preferences with **Apply my Timecards and weekday fill settings to email imports**, or adjust the options in the activity table for this import only. Weekday fill counts existing Quickbase time and recalculates after table edits. Work above the target and the full configured Timecards addition are retained; weekends receive no automatic additions. Word shows policy additions separately, and Excel excludes filler. Actual supported internal work is imported normally.

The Billing dropdown appears only when Quickbase confirms Modify access to Billable Override. Other accounts see read-only billing. Defaults show **Billable - Default** or **Nonbillable - Default** when the rule and required source values can be read. If **Default unavailable** appears, hover over the status for its reason and choose **Refresh billing**. This retries the live metadata and input reads; a recognized rule may resolve from one conclusive input. Required hidden inputs or unsupported formulas stay unknown, without inference from historical timecards. Green means billable, dim red means nonbillable, and gray means unknown. Explicit non-billable case work keeps its case, assignment and task. An override without permission must be cleared deliberately or its permission issue resolved before submission.

Double-click Hours, or focus the box and press F2, for a duration change. In the activity table, edited decimal hours are used exactly while unedited activities are grouped and rounded as configured. **Use imported** restores the original duration. Table edits survive mapping/billing changes and recalculate weekday fill. Later edits to final dashboard rows change only that row and leave Misc internal unchanged. Check the total and confirm again. Receipts retain local corrections and original/submitted hours; Excel remains unchanged.

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
- **assignment_hint** and **task_hint:** Descriptive matching hints; may be blank. For direct case/project work, leave assignment_hint blank and select the case with **Direct to project** in Timekeeper. Do not add columns or fabricate a management assignment.
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

The app still accepts legacy **timekeeper_email_review** workbooks and rich **timekeeper_email_activity** workbooks. Legacy Review estimates and limitations are confirmed in the final app review; rich Activities files retain their real-source-ID requirements. These are backward-compatible inputs. The revised agent should generate only the new **timekeeper_email_import** format and should not use the old REVIEW template.

## If something goes wrong

- **Unapproved workbook:** Review and approve Word, then have the agent regenerate Excel with the actual approval recorded. Do not change the template's false flag just to import sample data.
- **Invalid format or date:** Correct the named field or cell using the exact two-sheet contract. Use ISO dates when text is ambiguous.
- **Uncertain work or assignment:** Correct the work or duration with the agent; use live lookup for Quickbase relationships. Choose **Direct to project** if the work belongs on the case without an assignment.
- **Default unavailable:** Hover over the billing label and try **Refresh billing**. If the reason identifies required hidden fields or an unsupported formula, have your Quickbase administrator check them. The app cannot invent a billing result or grant override permission.
- **Incomplete coverage:** Obtain missing evidence or explicitly approve the documented subset. A relevance-ranked search does not prove complete mailbox coverage.
- **Partial or unknown submission:** Keep the receipt and use **Check selected result** before trying again. Some rows may already exist.
- **Deleted timecards:** Use **Recover deleted entries**, then import/read fresh data and review again. Renaming the workbook does not replace recovery.
