# Getting started with Timekeeper

Timekeeper runs on 64-bit Windows. Install the supplied setup program, then open **Timekeeper** from the Start menu. Python and a console are not required. This local build is unsigned; follow your organization's installation policy if Windows or IT blocks it.

## One-time setup

1. Open **Settings**. Enter your Quickbase sign-in email. You do not need to look up or enter a Quickbase employee/user ID; Timekeeper detects it from your token.
2. Enter your Quickbase user token and, if you use Toggl, your Toggl API token. Email imports do not require a Toggl account. Keep tokens out of Copilot chat, exported files, and screenshots.
3. Check the Quickbase realm, table configuration, internal project, internal task, and timezone against your organization's setup. The supplied schema defaults come from the reference workflow and may need adjustment for another Quickbase app.
4. Review your time policy. Defaults are 0.17 Timecards hours and an eight-hour weekday target. These are per-user preferences.
5. Choose **Test connections**. Timekeeper detects your Quickbase identity internally and checks that the token's account matches your Quickbase sign-in email. Check the displayed Toggl and Quickbase accounts, select the checkbox confirming they belong to you, then choose **Save settings**. Changed credentials or settings require another connection test before saving.
6. Choose **Open Copilot** to open the team's shared Timekeeper agent. Its chat link is preconfigured. You do not need to create or configure an agent; the owner must give your Microsoft 365 account access. If your team changes agents, update **Settings → Shared Copilot agent link** with its new chat link.

The application keeps its working files under `%LOCALAPPDATA%\Timekeeper`, outside the installation directory. Uninstalling removes the application but preserves those working files. Exported files contain work details; use your organization's approved Microsoft 365 account when uploading them. This guide is available through **Getting started** and in the application's `Help` folder.

## Customize the appearance

Open **Appearance** in the sidebar or **Appearance settings** at the top of Settings. Choose **Ivory** (the original light theme), **Dark**, **Slate**, **Forest** or **Sand**. Use the theme's accent color or select Teal, Blue, Violet or Amber. **Interface size** scales text and controls to 100%, 110% or 120%; **Spacing** offers Comfortable or Compact controls and table rows.

Changes preview immediately across open windows. Choose **Save appearance** to keep them, **Cancel** to restore the previous look, or **Reset appearance** to preview the original Ivory defaults. Appearance saves separately from account settings and does not require a connection test. Your current read session and reviewed proposal stay in place.

## Get your Quickbase user token

1. Sign into your Quickbase realm and open the user menu on the top bar. Choose **Profile** (older layouts may say **My preferences**).
2. Under **My User Information**, choose **Manage my user tokens for [your realm]**.
3. Choose **New user token**, then **OK**. Name it **Timekeeper** and add a description.
4. Under **Assign token to apps**, select the Quickbase app containing your timecards and lookup tables, then save.
5. Copy the token into **Settings → Quickbase user token** in Timekeeper and test connections. Some organizations show a token only at creation, so copy it before leaving that page.

Use your own user token for each person's installation. The shared Copilot agent does not need anyone's tokens. The settings form also links to [Quickbase's official token instructions](https://help.quickbase.com/docs/create-and-use-user-tokens).

## Record and read your time

1. Record your work in Toggl and stop any running timer when the day is complete.
2. Choose **Read today's data**, or select the intended date range and choose **Read dates**. “Today” follows your configured timezone.
3. Check the dates and summary. Resolve any running timer or incomplete-read message before continuing.
4. Drag the large export file in the middle of Timekeeper into your agent's Microsoft 365 Copilot chat. If the browser does not accept the drag, choose **Save file** in Timekeeper and use the browser's attachment picker to select that saved file.

Use the current export for this task. Reading a different session or changing the relevant settings means Copilot needs the corresponding new export.

## Answer Copilot and return its proposal

Copilot uses the export to identify projects, assignments, tasks, and concise descriptions. Answer questions when more than one match fits. If existing Quickbase time could already cover a Toggl entry, confirm that relationship explicitly; a similar description alone is not proof.

The agent produces `timekeeper-proposal.json`. Download it, then drag the downloaded file into Timekeeper or use **Open file**. Dragging a browser download link is not supported consistently, so use the actual downloaded file if necessary. If the agent supplies JSON text instead, copy the complete JSON and choose **Paste**.

Timekeeper checks the file against the retained read session. A validation error must be resolved before writing. Warnings require your explicit acknowledgment. **Data verified** means the app's checks passed and you acknowledged any warnings; review the proposed project and description choices yourself as well.

If you read several dates but asked Copilot to prepare only one, version 0.4.3 or later asks **Which dates do you want to submit?** on import. Leave just the intended date checked and choose **Continue with selected dates**. You can also select all dates; the proposal must then account for every source on all those dates. Cancelling clears the proposed submission. The review identifies the included and excluded dates, and **Change dates** lets you revise the selection before writing. Totals, automatic additions and Quickbase checks apply only to the selected dates. Excluded days receive no new rows and do not need invented `already_recorded` links. A receipt records your date selection; read again before preparing another submission from the original range.

## Review and write

Each row includes a **Billing** dropdown: **Quickbase default**, **Billable** (green light) or **Non-billable** (dim red light). Default shows gray because the final status depends on Quickbase's project/task rules. Changing billing keeps the project, assignment and task. If override access cannot be confirmed, explicit choices are unavailable; ask your Quickbase administrator for Modify access to the Timecards **Billable Override** field. A returned record with an unconfirmed or mismatched override is retained in history and must be checked in Quickbase, not submitted again.

Review every date, project, assignment, task, category, description, and number of hours. Select a row and choose **View selected row details**, or double-click the row, to see complete descriptions, reference IDs, and Toggl source IDs. The table separates work from automatic additions. Timekeeper computes the hours and additions; the agent cannot invent extra source time.

A **possible duplicate** warning means a new row resembles existing Quickbase time. Equal hours alone do not prove it is the same work. If it is additional work, confirm that and acknowledge the warning checkbox. If the source was already recorded, ask Copilot to return the matching `already_recorded` link instead. Review and acknowledge those links too. Timekeeper records your acknowledgment with the submission. It blocks source entries already confirmed as written by this installation.

The default examples, with no existing Quickbase time, are:

| Worked hours | Timecards | Misc internal | Final total |
| ---: | ---: | ---: | ---: |
| 7.00 | 0.17 | 0.83 | 8.00 |
| 8.00 | 0.17 | 0.00 | 8.17 |
| 9.00 | 0.17 | 0.00 | 9.17 |

These examples use weekday eligibility and rounded work totals. Existing records count toward the day. A large fill gap, empty day, or unresolved duplication needs attention; do not use fill as a substitute for missing Toggl entries. Weekend handling uses actual work. There is no late-afternoon cutoff rule.

Choose **Write to Quickbase** only after reviewing the rows. Wait for the result, then check **Submission history**. Confirmed created records and unresolved outcomes are distinct.

If a request times out or its outcome is unclear, select its receipt in **Submission history**, then choose **Check selected result** to reconcile. Do not repeatedly click Write or send the same proposal through another installation to see whether it works. Reconciliation checks Quickbase; if it cannot prove the result, inspect the records with your administrator before proceeding.

## Rewrite entries you intentionally deleted from Quickbase

Timekeeper remembers successful writes even after a Quickbase record is deleted. Keep that history; use the recovery action in version 0.4.2 or later to allow replacements.

1. Open **Submission history**, select the receipt containing the deleted work entry, and choose **Recover deleted entries**. Resolve any Pending/Unknown results with **Check selected result** first.
2. Timekeeper checks the original record IDs, including records moved to another date or employee. Select only entries you intentionally deleted, then choose **Confirm deletion & allow rewrite**. Records that still exist cannot be released. A permissions change can hide a record, so verify it was actually deleted if unsure.
3. Open **Timecards**, choose **Read dates** for the affected day, and upload this new export to Copilot. Discard the previous proposal. Tell Copilot you recovered the deleted entries and want replacements. Do not mark a deleted record as `already_recorded`; use that link only for matching records still present in the new export.
4. Review the new work and recalculated Timecards/Misc internal additions, then choose **Write to Quickbase**. To rewrite a whole day, confirm in Quickbase that the old day is empty before this fresh read. Otherwise the remaining records still count toward the day's total.

Recovery does not delete or write Quickbase records. The original creation results and a dated deletion confirmation remain in the receipt. Timekeeper checks the old IDs again before a replacement submission and stops if they reappear. Recover each relevant receipt if the day includes entries from multiple submissions. Do not erase receipt files or use another installation to bypass the history.

## Import a day reconstructed from email

1. Open your team's shared email export agent in Microsoft 365. Ask it to review the desired day in your timezone. Answer its questions about work performed, estimated minutes, matter and billing. Incoming messages alone do not prove work was performed.
2. Download its `.xlsx` file. The optional PDF is for personal review only.
3. Choose **Import email Excel** in Timekeeper, or drop the workbook in the file area. If it contains several dates, choose the dates to include.
4. In **Match your activities**, select each activity's Quickbase assignment and task. Use **Find assignment** if needed. For work already in Quickbase, select the matching record under **Already in Quickbase?**. Confirm the activities are yours and choose **Review timecards**.
5. Review the hours and billing dropdowns, acknowledge any estimates or existing-time warnings, and choose **Write to Quickbase**. Keep the receipt. Reimport a fresh workbook/baseline to prepare remaining work after a partial result; map successful activities to their existing records.

Email imports use confirmed minutes and round up once per date/assignment/task/billing group, defaulting to five minutes. **Settings → Time policy → Email imports** offers no additional rounding, five minutes or fifteen minutes, and an option to apply your normal Timecards/fill settings. Automatic additions are off for email imports by default. Worked hours above the target are retained.

The workbook email and timezone must match your verified account and settings. Timekeeper uses stable message/event IDs to prevent resubmission on this installation, including files regenerated with new descriptions or grouping. Check for records created manually or on another computer as well. After intentionally deleting an email-based record, use the same recovery action above, then import the workbook again instead of reading Toggl.

## Update Timekeeper

Open **Updates** in the sidebar and choose **Check for updates**. If a newer version is available, review its notes and choose **Download and install**. Timekeeper verifies the download, closes, and opens the Windows installer. Complete that installer and reopen Timekeeper; your settings and history remain in place. Finish any active read or write first. Cancelling the download leaves your installed app unchanged.

For private GitHub releases, expand **GitHub access** and save a fine-grained token with **Contents: Read-only** access to `RyanWW-Products/Timekeeper`. Your GitHub account must have access to that repository; your organization may also require token approval. This is separate from your Toggl and Quickbase tokens. Signing into GitHub in your browser does not sign the app in. If you prefer, use **Open releases page**, sign into GitHub in your browser, and download the installer there. Public releases do not need a token. Access errors automatically expand these settings; an expired or revoked token must be replaced and saved before checking again.

## Try it without live data

Choose **Try a sample day** to explore export, proposal validation, and review with synthetic data. Demo data does not establish a live connection and must not be written to Quickbase.

## Common problems

- **Submission stops after one row with “Fresh Quickbase data could not be confirmed”:** Versions through 0.4.0 could mistake equivalent hours such as `3` and `3.0` for a record change. Update to 0.4.1 or later. Keep the receipt; only rows marked Created were confirmed written. Resolve any Pending/Unknown rows with **Check selected result**. Then read again, upload the new export, and tell Copilot which source entries the receipt confirms already exist so it can use `already_recorded` links and propose only the remaining work. Do not reuse the original proposal. New receipts identify the record/field or service error if the fresh-data check still stops a submission.
- **Quickbase cannot read a field:** The message identifies the table, record ID/name, field number and received value. Use **Open Quickbase record** to inspect the source, or **Copy error details** to share that specific diagnostic. A blank required relationship may need correction in Quickbase; an unexpected value may also indicate a field-mapping mismatch. If the record ID itself is missing, the message gives the result row's position instead. No timecards are written by a failed connection test.
- **Credentials rejected:** Check your own tokens and required permissions in Settings. Copilot cannot repair a token, and you should not send it one.
- **Quickbase identity verification returns HTTP 400:** Update to 0.3.0 or later, which uses Quickbase's supported user-token lookup. Confirm that your token is active and assigned to the app containing your timecards, save the token page, and test again. Check the realm and the Timecards table ID under Advanced table configuration if it still fails.
- **Quickbase account mismatch:** Use the Quickbase sign-in email belonging to the token, then test again. The application discovers the corresponding ID; do not try to enter an ID manually.
- **Internal project not found:** Check its name in Settings, or ask your Quickbase administrator for the correct internal project ID. This is a project identifier, separate from your automatically detected user ID.
- **Missing or ambiguous assignment:** Choose **Find assignment** in Timekeeper and search with a few words from the assignment, project, or client. Search results are added to a new export. Upload that updated file to Copilot and request a new proposal; the previous proposal no longer matches. Do not invent an ID.
- **Wrong session or employee:** Return to the intended read session and create its proposal. A file from another person or export is not interchangeable.
- **Copilot cannot attach JSON:** Use the available text-copy route or your tenant's approved file-upload option. Do not change the JSON content or paste credentials.
- **Copilot cannot create files:** Copy its single JSON response and choose Paste.
- **Data verified is unavailable:** Correct listed validation errors, then review and acknowledge any remaining warnings. Acknowledgment does not override errors.
- **A write partly succeeds:** Read the receipt and reconcile. Keep the original history; successful rows must not be submitted again.

This version opens Microsoft 365 in your browser. There is no embedded Microsoft sign-in or chat to configure.
