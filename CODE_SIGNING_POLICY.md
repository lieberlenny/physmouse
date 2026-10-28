# Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What is signed

Only the `physmouse-v<version>-installer.exe` published on the [GitHub releases](../../releases) page, built from this repository's own source by the GitHub Actions workflow `.github/workflows/release.yml` from a pushed `v*` tag. Nothing else is signed. The Microsoft Store version is signed by Microsoft.

The binary carries the product name `physmouse` and the version of the tag.

## Team roles

physmouse is maintained by one person.

| Role | Who |
| --- | --- |
| Author (commits to the repository) | [Lennart Blees](https://github.com/lieberlenny) |
| Reviewer (reviews changes from anyone else) | [Lennart Blees](https://github.com/lieberlenny) |
| Approver (approves every signing request) | [Lennart Blees](https://github.com/lieberlenny) |

Everyone with access to the repository or to SignPath uses multi-factor authentication. Every release is approved by hand in SignPath before it is signed.

## Privacy

physmouse has no account, ads or telemetry, and settings stay on your PC. The only network request is the update check against GitHub at startup. Admins can turn it off with the `DisableUpdateCheck` policy, and the Store version doesn't make it. Full policy: https://lennartblees.de/physmouse-privacy.html

## Verifying a download

Releases also carry a GitHub build provenance attestation: `gh attestation verify physmouse-v<version>-installer.exe -R lieberlenny/physmouse`.

## Reporting a problem

Report a suspected misuse of the certificate through the [security policy](SECURITY.md).
