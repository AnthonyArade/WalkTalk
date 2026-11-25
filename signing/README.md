# Setting up code signing (SignPath Foundation)

What to do in SignPath and GitHub once the SignPath Foundation has accepted WalkTalk. The build in
`.github/workflows/build.yml` is already prepared: it starts signing as soon as the settings below exist.

## 1. Before anything: two-factor authentication

SignPath requires it for everyone on the project, on both GitHub and SignPath.

- GitHub: Settings > Password and authentication > Enable two-factor authentication.
- SignPath: turn it on in your SignPath profile when you first sign in.

## 2. In SignPath (app.signpath.io)

### Trusted build system
1. Top menu **Trusted Build Systems** > **Add predefined** > **GitHub.com**.

### Project
2. Open the WalkTalk project (or create it): **Slug** `walktalk`.
3. Set the **Repository URL** to `https://github.com/AnthonyArade/WalkTalk`.
4. In **Trusted build systems**, click **Link** and choose GitHub.com.

### Artifact configurations
5. Add an artifact configuration, slug `app`, and paste the content of [`app.xml`](app.xml).
6. Add another, slug `installer`, and paste the content of [`installer.xml`](installer.xml).

### Signing policy
7. Open the policy for releases (create it if there is none): slug `release-signing`.
8. Turn on **Trusted build system verification** and **Verify origin**.
9. **Allowed branch names**: `main`, plus the release tags (`refs/tags/v*`; see the note at the end).
10. **Approvers**: yourself. Every release then waits for your approval in SignPath.

### CI user and its token
11. **Users and Groups** > add a **CI user**, e.g. `github-walktalk`, and copy its **API token** (it is shown only once).
12. Back in the signing policy, add that CI user to **Submitters**.

### Organization ID
13. Click your organization's name in the upper right corner and copy the **Organization ID**.

## 3. In GitHub (repository Settings > Secrets and variables > Actions)

| Kind | Name | Value |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | the CI user's token (step 11) |
| Variable | `SIGNPATH_ORGANIZATION_ID` | step 13 |
| Variable | `SIGNPATH_PROJECT_SLUG` | `walktalk` |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | `release-signing` |
| Variable | `SIGNPATH_APP_CONFIGURATION` | `app` |
| Variable | `SIGNPATH_INSTALLER_CONFIGURATION` | `installer` |

If you used other slugs in SignPath, put those here instead.

## 4. A signed release

1. Raise `<Version>` in `Directory.Build.props` (for example `1.0.1.0`), commit and push.
2. Tag the commit and push the tag: `git tag v1.0.1` then `git push origin v1.0.1`.
3. The build sends the app's files to SignPath. Approve the request in SignPath (you get an email).
4. It packs the installer and sends it to SignPath. Approve that one too.
5. The signed installer is attached to the GitHub release `v1.0.1`.

Only tags starting with `v` are signed. Ordinary pushes to `main` still build, unsigned, to check that
everything compiles.

If SignPath refuses the request because of the branch, the policy's **Allowed branch names** doesn't cover
the tag: add `refs/tags/v*` (or `v*`) there.

## What gets signed

| Sent to SignPath | Signed inside it |
|---|---|
| the app folder, as a zip | `WalkTalk.exe`, `WalkTalk.dll`, `WhisperStt.Core.dll` |
| the setup exe, as a zip | `WalkTalk-Setup-<version>.exe` |

SignPath checks that each file says product "WalkTalk" and the version from `Directory.Build.props`.
The other files (.NET, Whisper, the Microsoft runtime) are not WalkTalk's own and are left as they are.
