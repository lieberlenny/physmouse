# Security policy

## Supported versions

Only the latest release gets security fixes, on GitHub and in the Microsoft Store.

## Reporting a vulnerability

Please don't open a public issue for security problems. Report them privately instead, through GitHub's private vulnerability reporting: https://github.com/lieberlenny/physmouse/security/advisories/new (or the **Security** tab of this repository, then **Report a vulnerability**). Only the maintainer can see the report.

Please include:

- the physmouse version and whether it's the GitHub or the Store build
- your Windows version
- what an attacker can do, and the steps to reproduce it

## Disclosure process

physmouse is maintained by one person in their spare time, so this is best effort, but security reports come first.

1. You get an answer in the private report, confirming whether the issue is reproduced and in scope.
2. The fix is developed privately in the advisory, and you're welcome to test it.
3. A fixed release is published on GitHub and submitted to the Microsoft Store and WinGet.
4. The vulnerability is then disclosed publicly as a GitHub security advisory (with a CVE where it applies), crediting you if you like.

Please keep the vulnerability private until the advisory is published. If a fix takes unusually long, we'll agree on a disclosure date in the report.

## Scope

physmouse runs a low-level mouse hook in the signed-in user's session and, if the user chooses "On, as administrator", starts elevated through a Task Scheduler task. Things that are in scope include, for example:

- anything that lets another process or user get code running through physmouse, especially through the elevated task
- input from the mouse hook reaching anything other than the cursor position
- the update check or installer trusting something they shouldn't

What physmouse reads and writes is listed in the README under [Security & enterprise](README.md#security--enterprise).
