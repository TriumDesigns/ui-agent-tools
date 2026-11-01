# Security policy

## Reporting a vulnerability
Please **do not** open a public issue. Use GitHub's private reporting:
**Security tab > Report a vulnerability** on this repository. We aim to acknowledge reports within a
few days and will credit you in the fix unless you prefer otherwise.

## Supported versions
Only the latest release receives fixes.

## Threat model (what is and isn't a vulnerability)
UI Agent Tools is an Editor tool that lets an AI agent change your project. By design:
- Edit scripts are arbitrary C# executed inside your Unity Editor. Run the tool only on projects and with
  agents you trust, and review changes with `git diff`.
- The Editor request bridge (`UIAgentMap/requests/`) executes commands for anyone who can write to your
  project folder. It is git-ignored by the installer.
- `run` must only execute public static methods in the `AgentEdits` assembly, and asset paths must stay inside
  `Assets/` or `Packages/`. A way around either restriction **is** in scope.
