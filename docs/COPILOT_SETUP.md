# Set up the Timekeeper Copilot agent

This release uses an agent created in Microsoft 365 Copilot's **Agent Builder**. It does not require a Copilot Studio project or an embedded Microsoft sign-in. Each person still needs their own Timekeeper settings and authorized access to Copilot.

In Timekeeper, enter your Quickbase sign-in email and API tokens, then choose **Test connections**. The app discovers your Quickbase user ID automatically. The agent should never ask you to look it up or paste tokens into chat.

## Create the agent

1. In Microsoft 365 Copilot, choose **New agent**, then **Skip to configure**.
2. Set the name to **Timekeeper**. Suggested description: “Matches exported Toggl work to Quickbase references and creates a proposal for review in Timekeeper.”
3. In Timekeeper, open **Copilot setup** and choose **Copy agent instructions**. Paste that text into Agent Builder's Instructions field. The dialog's **Copy text** button copies the setup guide, so use the dedicated instructions button. You can also use the complete contents of `COPILOT_AGENT_INSTRUCTIONS.txt`. Reopen the saved instructions and check that the last line is `END OF TIMEKEEPER INSTRUCTIONS`.
4. Enable **Create documents, charts, and code** when available. This capability can produce downloadable files; availability depends on licensing and tenant settings.
5. Add starter prompts such as “Review my Timekeeper export” and “Help resolve an assignment match.” Keep the agent focused on the uploaded export; extra mail, web, or company-wide knowledge is not needed for this workflow.
6. Test with the synthetic export from Timekeeper, then create the agent and save its link in the application.

Microsoft describes the current controls and the 8,000-character instruction limit in its [Agent Builder guide](https://learn.microsoft.com/en-us/microsoft-365-copilot/extensibility/copilot-studio-lite-build). Its [code interpreter documentation](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/code-interpreter) explains downloadable-file support.

## File exchange

Upload each daily export into that conversation. Do not add personal time exports as permanent agent knowledge. The agent returns a versioned proposal tied to that export; Timekeeper is responsible for calculation, validation, review, and writing.

Microsoft lists `.json` among [supported Copilot file formats](https://support.microsoft.com/en-us/microsoft-365-copilot/file-formats-supported-by-microsoft-365-copilot). Actual attachment and download behavior still depends on the host and tenant. Test both directions in your environment: app file to Copilot, then downloaded proposal to app. The fallback is copying JSON text into Timekeeper using **Paste**.

## Test before sharing

- Ask the agent to return a synthetic proposal and check it in Timekeeper.
- Include an ambiguous assignment and confirm the agent asks instead of guessing.
- Include existing time and confirm it requests explicit confirmation before claiming a source entry is already recorded.
- Check that the proposal preserves the session and employee IDs, references every source entry exactly once, and omits invented hours or automatic Timecards/Misc internal rows.
- Verify a nine-hour weekday keeps all work; the application should add the configured Timecards amount without capping the day at eight hours.

Do not paste the historical batch-file instructions into this agent. They describe a different file format and a superseded time rule. Never put Toggl or Quickbase API tokens in agent instructions, knowledge, or chat.
