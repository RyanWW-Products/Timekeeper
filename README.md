# Timekeeper

Timekeeper is a Windows application for turning Toggl entries or confirmed email activities into reviewed Quickbase timecards. It validates imported files locally, shows the exact rows, and writes only after the user chooses **Write to Quickbase**.

The installed application includes its .NET runtime. End users do not need Python, a terminal, or a separate .NET installation.

Choose **Appearance** in the sidebar for Ivory, Dark, Slate, Forest or Sand themes, accent colors, interface size and spacing. Changes preview immediately and save per user. Restrained serif headings and monospaced totals carry through settings, updates and review dialogs; see [interface design](docs/DESIGN.md) for shared styles and visual verification.

## Get started

1. Download and run the Windows installer from [Releases](https://github.com/RyanWW-Products/Timekeeper/releases/latest), or use the latest local build at `installer/Timekeeper-Setup.exe`.
2. Open Timekeeper from the Start menu, enter your work email and API credentials in Settings, and choose **Test connections**. The app discovers your Quickbase user ID automatically.
3. Use **Open Copilot** for the preconfigured shared Timekeeper agent and follow [Getting started](docs/GETTING_STARTED.md). Team members do not create agents; the owner grants them access in Microsoft 365.

The installer is per user and includes an optional desktop shortcut and standard Windows uninstall entry. This local build is unsigned. Organizational installation and Copilot access policies still apply.

## Daily workflow

Record time in Toggl, stop running timers, and choose **Read today's data** in Timekeeper. Drag the exported file into your Timekeeper agent in Microsoft 365 Copilot. Answer its matching questions, then return the proposal using drag and drop, **Open file**, or **Paste**. Review validated rows and choose **Write to Quickbase**. Keep the resulting history entry; it records confirmed and unresolved outcomes.

The first release uses your browser for Microsoft 365 sign-in and chat. Embedded Microsoft 365 sign-in/chat is not implemented. File generation and attachment support depend on your tenant; JSON text can be pasted back into Timekeeper when Copilot cannot create a downloadable proposal.

The team owner maintains the shared agent using the repository's [owner guide](docs/COPILOT_SETUP.md), [agent instructions](docs/COPILOT_AGENT_INSTRUCTIONS.txt) and [knowledge file](docs/COPILOT_AGENT_KNOWLEDGE.txt). Paste the instructions into the agent's Instructions field and upload the knowledge file under Knowledge. Agent creation is not part of the installed team's setup flow.

For short, fragmented work, the shared email agent reconstructs a day from the employee's available mail and asks them to confirm activity and minutes in chat. Download its Excel workbook, choose **Import email Excel**, match activities to live Quickbase assignments and tasks, then review. Toggl is optional for this workflow. The [email agent instructions](docs/Timekeeper%20Email%20Export%20Agent%20Instructions.txt) and [email import guide](docs/EMAIL_IMPORT.md) define the workbook contract and setup. A personal PDF is optional and is not submission input. Email time uses confirmed minutes, with five-minute rounding after grouping and no automatic additions by default; both are configurable.

Billing shows **Billable - Default** or **Nonbillable - Default** when Quickbase exposes the fields needed to resolve its rule. Accounts with Modify access can choose a billing override; other accounts see a read-only status. If Quickbase hides the default inputs or uses an unsupported formula, the label is **Default unavailable**. Green means billable, dim red means nonbillable, and gray means unknown. Overrides preserve the assignment and task and are checked again before writing.

Double-click an hours box, or focus it and press F2, to make a last-minute duration edit. Enter decimal hours and save. Other rows, including Misc internal, stay unchanged. Review the updated daily total and acknowledge the warning. Receipts keep the original and submitted hours; source and duplicate checks still apply.

Version 0.6.0 also accepts the supported `timekeeper_email_review` Excel layout. It shows the draft activities and limitations and requires local confirmation before matching assignments. These files use limited row-fingerprint duplicate checks because they lack stable message IDs. The final `timekeeper_email_activity` format with genuine source IDs remains supported. Email-only users leave the Toggl token blank in Settings; only Quickbase is tested and used.

## Time policy

Per-user defaults are **0.17 hours for Timecards** and an **eight-hour weekday target**. The application calculates rounded work and automatic additions; Copilot proposes the matching and descriptions. Worked hours and the full Timecards addition are preserved: nine hours of work becomes **9.17 hours**, with no Misc internal addition. Clock time does not affect this calculation. See [the time policy](docs/TIME_POLICY.md) for the confirmed arithmetic.

## Build from source

Use **Updates → Check for updates** in the app to find and install newer versions. Downloads are verified against GitHub's SHA-256 digest before installation. Private releases require repository read access and a GitHub token in the Updates window; browser downloads are also available. See [Publishing updates](docs/RELEASING.md) for the release process.

Developer prerequisites: Windows, the .NET 10 SDK, and Inno Setup 6. Runtime packs may need downloading during the first self-contained publish.

Open `Timekeeper.slnx` in an editor that supports the solution format, or build using the script below.

```powershell
./scripts/build.ps1
```

If local PowerShell policy disables direct script execution, this invocation applies only to the build process and does not change a saved execution policy:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/build.ps1
```

The script uses the version in `Directory.Build.props`, runs the console test projects when present, publishes a self-contained `win-x64` application to `artifacts/publish`, and compiles the installer to `artifacts/installer`. It also refreshes `installer/Timekeeper-Setup.exe` and its SHA-256 file with that same build, so a new installation starts on the current version. Generated installers stay out of Git; downloadable installers are attached to releases. The script does not install or launch the application. The generated publish folder is replaced each build. To select another compiler location or an available .NET 10 runtime patch:

```powershell
./scripts/build.ps1 -InnoSetupCompiler 'C:\Tools\Inno Setup 6\ISCC.exe' -RuntimeFrameworkVersion '10.0.11'
```

Use a .NET 10 runtime pack; .NET 9 packs cannot satisfy this application. Historical files under `Reference Material` are excluded from the package. Only compiled application files and the new user guides are included.

## Verification boundary

Synthetic demo and offline tests are intended to verify rules, validation, and API response handling. A successful build or demo does not establish that your credentials, Quickbase field configuration, tenant file exchange, or live writes work. Live Toggl/Quickbase operation and the complete Copilot exchange require a controlled check with your own setup before rollout. An ambiguous write result must be reconciled in History before another submission.
