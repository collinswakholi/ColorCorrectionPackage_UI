# Security reporting

Do not disclose exploitable vulnerabilities or private images in public issues.
Use [GitHub private vulnerability reporting](https://github.com/collinswakholi/ColorCorrectionPackage_UI/security/advisories/new)
when enabled; otherwise contact the maintainer privately through the contact
details on [their GitHub profile](https://github.com/collinswakholi). Do not assume
a response SLA. The v0.1.0 preview has not undergone a comprehensive security audit.

Provide affected commit/version, OS, minimal synthetic reproduction and impact.
The native API launches a local worker and has no HTTP listener by default.
It can read/write files with the caller's permissions. Run only trusted scripts;
review output paths before batch operations. Do not deserialize untrusted legacy
pickle files. FFmpeg/image parsers process untrusted media; keep dependencies
reviewed and do not run the app elevated. An installer checksum verifies integrity,
not publisher identity: v0.1.0 is unsigned.
