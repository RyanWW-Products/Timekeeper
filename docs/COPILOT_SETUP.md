# Maintain the shared Timekeeper Copilot agent (owner only)

One owner creates or updates the agent in Microsoft 365 Copilot's **Agent Builder** and shares its chat link with the team. Team members use that shared agent; they do not create agents. Each person still uses their own Timekeeper settings, API tokens and authorized Copilot account. This owner guide, agent instructions and knowledge file stay in the source repository and are not included in the team installer.

The current agent materials target Timekeeper **0.4.0**, exchange schema **1**:

- [COPILOT_AGENT_INSTRUCTIONS.txt](COPILOT_AGENT_INSTRUCTIONS.txt): paste the full text into the agent's **Instructions** field.
- [COPILOT_AGENT_KNOWLEDGE.txt](COPILOT_AGENT_KNOWLEDGE.txt): upload as one file under **Knowledge**. This replaces the historical `Timecards Setup Guide.txt` for the Python/batch-file system.

No application reinstall is needed to update the shared agent. The owner replaces these materials once and publishes the agent update for the team.

In Timekeeper, enter your Quickbase sign-in email and API tokens, then choose **Test connections**. The app discovers your Quickbase user ID automatically. The agent should never ask you to look it up or paste tokens into chat.

## Update the shared agent

1. Open your existing Timekeeper agent in Microsoft 365 Copilot's editor. For the first agent only, choose **New agent**, then **Skip to configure**.
2. Set the name to **Timekeeper**. Suggested description: “Matches exported Toggl work to Quickbase references and creates a proposal for review in Timekeeper.”
3. Replace the Instructions field with the complete contents of [COPILOT_AGENT_INSTRUCTIONS.txt](COPILOT_AGENT_INSTRUCTIONS.txt) from this repository. These instructions implement the current file/proposal workflow. Reopen the saved instructions and check that the last line is `END OF TIMEKEEPER INSTRUCTIONS`.
4. Under **Knowledge**, upload [COPILOT_AGENT_KNOWLEDGE.txt](COPILOT_AGENT_KNOWLEDGE.txt). Remove the old `Timecards Setup Guide.txt` and other legacy Python/batch-file instructions from the agent's knowledge to avoid conflicting answers. The new guide contains setup, matching rules, hours examples, the proposal format and troubleshooting; actual IDs and work always come from each current export. Microsoft supports `.txt` [embedded knowledge files](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/agent-builder-add-knowledge#embedded-file-content). If uploads are unavailable in your tenant, store this guide in an approved SharePoint location and add that file as knowledge instead.
5. Enable **Create documents, charts, and code** when available. This capability can produce downloadable files; availability depends on licensing and tenant settings.
6. Add starter prompts such as “Review my Timekeeper export,” “Help resolve an assignment match,” and “Help me set up Timekeeper.” Keep the agent focused on the current export and this guide; extra mail, web, or company-wide knowledge is not needed for this workflow.
7. Test with the synthetic export from Timekeeper, save your agent changes, and use the agent's sharing controls to copy its team-facing chat link. Supply that link to the team for Timekeeper's **Shared Copilot agent link** setting. Do not distribute an `/agents/edit/` URL as the chat link.

The owner-provided [Timekeeper chat link](https://m365.cloud.microsoft/chat/?titleId=T_b313a74c-f0a1-7381-7c08-0d5992e75a3f&source=embedded-builder) is the application's default. Existing installations using the old generic Copilot URL switch to this default; deliberately customized links are preserved. The app does not grant Microsoft 365 access. In the agent's Share dialog, the owner grants team members **Can chat** access. After replacing the instructions, choose **Update** to make the changes available to those users. See Microsoft's [sharing and management guide](https://learn.microsoft.com/en-us/microsoft-365-copilot/extensibility/agent-builder-share-manage-agents).

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
- Ask how to set up the app. The answer should use Settings and automatic user ID discovery, with no Python, batch files, manual employee ID lookup or clock-time cutoff.
- Start a fresh chat after publishing the update so prior conversations do not carry obsolete workflow advice. Test the actual shared agent as well as the editor preview; tenant file-generation and attachment capabilities need a live check.

Do not paste the historical batch-file instructions into this agent. They describe a different file format and a superseded time rule. Never put Toggl or Quickbase API tokens in agent instructions, knowledge, or chat.
