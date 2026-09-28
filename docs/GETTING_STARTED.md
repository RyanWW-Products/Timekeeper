# Getting started with Timekeeper

Timekeeper runs on 64-bit Windows. Install the supplied setup program, then open **Timekeeper** from the Start menu. Python and a console are not required. This local build is unsigned; follow your organization's installation policy if Windows or IT blocks it.

## One-time setup

1. Open **Settings**. Enter your Quickbase sign-in email. You do not need to look up or enter a Quickbase employee/user ID; Timekeeper detects it from your token.
2. Enter your Toggl API token and Quickbase user token in the application. Keep these out of Copilot chat, exported files, and screenshots.
3. Check the Quickbase realm, table configuration, internal project, internal task, and timezone against your organization's setup. The supplied schema defaults come from the reference workflow and may need adjustment for another Quickbase app.
4. Review your time policy. Defaults are 0.17 Timecards hours and an eight-hour weekday target. These are per-user preferences.
5. Choose **Test connections**. Timekeeper fills the read-only Quickbase user ID and checks that the token's account matches your Quickbase sign-in email. Check the displayed Toggl and Quickbase identities, select the checkbox confirming they belong to you, then choose **Save settings**. Changed credentials or settings require another connection test before saving.
6. Open **Copilot setup** in Timekeeper and follow its guide. Choose **Copy agent instructions** to copy the text for Agent Builder; **Copy text** copies the setup guide instead. If you have an agent link, save it in Timekeeper's settings.

The application keeps its working files under `%LOCALAPPDATA%\Timekeeper`, outside the installation directory. Uninstalling removes the application but preserves those working files. Exported files contain work details; use your organization's approved Microsoft 365 account when uploading them. The setup guide is available through **Copilot setup**; installed guides are also in the application's `Help` folder.

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

## Review and write

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

## Update Timekeeper

Open **Updates** in the sidebar and choose **Check for updates**. If a newer version is available, review its notes and choose **Download and install**. Timekeeper verifies the download, closes, and opens the Windows installer. Complete that installer and reopen Timekeeper; your settings and history remain in place. Finish any active read or write first. Cancelling the download leaves your installed app unchanged.

For private GitHub releases, expand **GitHub access (optional)** and save a fine-grained token with **Contents: Read-only** access to `RyanWW-Products/Timekeeper`. Your GitHub account must have access to that repository; your organization may also require token approval. This is separate from your Toggl and Quickbase tokens. If you prefer, use **Open releases page**, sign into GitHub in your browser, and download the installer there. Public releases do not need a token.

## Try it without live data

Choose **Try a sample day** to explore export, proposal validation, and review with synthetic data. Demo data does not establish a live connection and must not be written to Quickbase.

## Common problems

- **Credentials rejected:** Check your own tokens and required permissions in Settings. Copilot cannot repair a token, and you should not send it one.
- **Quickbase account mismatch:** Use the Quickbase sign-in email belonging to the token, then test again. The application discovers the corresponding ID; do not try to enter an ID manually.
- **Internal project not found:** Check its name in Settings, or ask your Quickbase administrator for the correct internal project ID. This is a project identifier, separate from your automatically detected user ID.
- **Missing or ambiguous assignment:** Choose **Find assignment** in Timekeeper and search with a few words from the assignment, project, or client. Search results are added to a new export. Upload that updated file to Copilot and request a new proposal; the previous proposal no longer matches. Do not invent an ID.
- **Wrong session or employee:** Return to the intended read session and create its proposal. A file from another person or export is not interchangeable.
- **Copilot cannot attach JSON:** Use the available text-copy route or your tenant's approved file-upload option. Do not change the JSON content or paste credentials.
- **Copilot cannot create files:** Copy its single JSON response and choose Paste.
- **Data verified is unavailable:** Correct listed validation errors, then review and acknowledge any remaining warnings. Acknowledgment does not override errors.
- **A write partly succeeds:** Read the receipt and reconcile. Keep the original history; successful rows must not be submitted again.

This version opens Microsoft 365 in your browser. There is no embedded Microsoft sign-in or chat to configure.
