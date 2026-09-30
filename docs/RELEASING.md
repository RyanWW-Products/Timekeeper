# Publishing Timekeeper updates

Source and Windows releases live at `RyanWW-Products/Timekeeper`. The app checks the latest stable GitHub release only when the user opens **Updates** and clicks **Check for updates**. It requires a `vMAJOR.MINOR.PATCH` tag and the exact asset name `Timekeeper-Setup-MAJOR.MINOR.PATCH-win-x64.exe`. Drafts and prereleases are not offered.

For each release:

1. Update `Directory.Build.props`, the fallback version in `installer/Timekeeper.iss`, and release notes. `scripts/build.ps1` reads the current version from `Directory.Build.props` unless explicitly overridden. The UI version comes from the compiled assembly. Each build also refreshes the local `installer/Timekeeper-Setup.exe` copy and its checksum with the newly compiled release.
2. Commit reviewed changes, run the Windows checks, and push the commit plus its matching version tag to GitHub.
3. From that clean checkout, run `./scripts/publish-release.ps1 -Version 0.2.0 -NotesFile ./docs/releases/0.2.0.md`, substituting the new version and notes. This builds, tests, renders the published UI, uploads the installer and SHA256SUMS.txt to a draft, and publishes it only after the upload succeeds. The developer needs .NET 10, Inno Setup 6 and authenticated GitHub CLI access.
4. Verify the release's installer asset has a `sha256:` digest in GitHub's release API and check from an older installed app before broad distribution.

The updater downloads from the fixed repository's asset API, follows only HTTPS GitHub download redirects, enforces the release's byte count and a 200 MiB limit, and verifies SHA-256 against GitHub's asset digest before launching the installer. A failed or cancelled download is deleted. Tokens are sent only to `api.github.com`, not redirected download hosts. The installer keeps the same application ID so it upgrades the existing installation while retaining settings and receipts outside the install directory.

SHA-256 checks verify that the download matches the release; they are not a publisher signature. The installer is currently unsigned. Protect repository write access and use publisher code signing before broad enterprise rollout.

For a private repository, users need GitHub access to that repository and a fine-grained token with **Contents: Read-only**, configured in **Updates → GitHub access**. Organization approval may be required for a token. Update credentials are separate from Toggl and Quickbase tokens and stored in Windows Credential Manager. Clearing the token field and saving removes its usable value. Public releases need no token unless GitHub request limits intervene. The app cannot inherit a browser's GitHub session. **Open releases page** lets users download through that browser session instead. Access failures expand the token settings, while rate-limit errors advise waiting.

The Windows build workflow verifies commits and pull requests and retains installer/UI artifacts; it does not automatically publish every build. Reference Material, build output, working files and credentials must remain excluded from git. Live timecard writes are not part of CI.

API reference: [GitHub release assets](https://docs.github.com/en/rest/releases/assets).
