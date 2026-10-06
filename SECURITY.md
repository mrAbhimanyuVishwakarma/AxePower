# Security Policy

## Supported versions

AxePower provides security fixes for the current release line. Users should update to the newest patch available for that line.

| Version | Security support |
|---|---|
| 2.1.x | Supported |
| 2.0.x and earlier | Not supported |

Pre-release branches and locally modified builds are not supported releases, although reports that also affect a supported release are welcome.

## Report a vulnerability privately

Do **not** open a public GitHub issue, discussion, pull request, Discord post, or other public message for a suspected exploitable vulnerability.

Email the maintainer at [abhimanyuvishwakarma9228@gmail.com](mailto:abhimanyuvishwakarma9228@gmail.com) with the subject `[SECURITY] AxePower vulnerability report`.

Include as much of the following as is safe to share:

- Affected AxePower version and Windows version.
- A concise description of the impact and attack scenario.
- Reproduction steps or a minimal proof of concept.
- Relevant logs, screenshots, or code locations with secrets and personal data removed.
- Whether the issue is already known to others or has a planned disclosure date.
- Your preferred name for acknowledgement, or state that you want to remain anonymous.

Do not include live credentials, access tokens, personal data, or destructive payloads. If the report needs a large or especially sensitive attachment, ask for a safe transfer method before sending it.

Use the public issue tracker only for ordinary bugs that do not have a security impact.

## Response targets

These are good-faith targets rather than a service-level agreement:

- Acknowledgement within 3 business days.
- Initial validation and severity assessment within 7 business days.
- A status update at least every 14 calendar days while a confirmed report remains open.
- A remediation plan after validation, with critical and high-severity issues prioritized over normal release work.

Complex issues, third-party coordination, hardware-specific behavior, and maintainer availability may affect timing. Reporters will be told when a target cannot be met.

## Coordinated disclosure

1. The maintainer confirms receipt and may request additional information.
2. The report is reproduced, scoped, and assessed privately.
3. A fix and regression tests are prepared without publishing exploit details.
4. A release is made available to supported users.
5. The maintainer and reporter coordinate public disclosure after a fix is available, including an advisory or CVE when appropriate.

Please allow reasonable time for investigation and supported-user remediation before publishing details. Reporter credit will be offered unless anonymity is requested.
