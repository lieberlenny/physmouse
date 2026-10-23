# Security policy

## Supported versions

Only the latest release gets security fixes, on GitHub and in the Microsoft Store.

## Reporting a vulnerability

Please don't open a public issue for security problems. Report them privately instead: on GitHub, open the **Security** tab of this repository and choose **Report a vulnerability**. Only the maintainer can see the report.

Please include:

- the physmouse version and whether it's the GitHub or the Store build
- your Windows version
- what an attacker can do, and the steps to reproduce it

You'll get an answer in the report. Once a fix is released, the report is published as a security advisory, crediting you if you like.

## Scope

physmouse runs a low-level mouse hook in the signed-in user's session and, if the user chooses "On, as administrator", starts elevated through a Task Scheduler task. Things that are in scope include, for example:

- anything that lets another process or user get code running through physmouse, especially through the elevated task
- input from the mouse hook reaching anything other than the cursor position
- the update check or installer trusting something they shouldn't

What physmouse reads and writes is listed in the README under [Security & enterprise](README.md#security--enterprise).
