# Security policy

Gear Backup reads and writes files on your computer and on the devices you connect, so security reports are welcome.

## Reporting a vulnerability

Please report problems privately, not in a public issue: open the repository's **Security** tab on GitHub and choose
**Report a vulnerability**. Include the version (Help, About), what you did, and what you expected to happen. A sample
file helps if the problem involves a ZIP, song or backup. You will get a reply as soon as the maintainer can, and you
will be credited in the release notes if you wish.

## What is covered

The latest release is supported. Reports about these are especially useful:

- A path, ZIP entry or device file name that makes Gear Backup read or write outside the folder it was given.
- A damaged or hostile backup, ZIP, M8 song or MTP device that crashes the app, fills the disk or runs code.
- Anything that lets another program on the PC redirect what Gear Backup loads or runs.

## Things to know

- ZIP checksums detect damage, not tampering: anyone can change a ZIP and its checksum list together. Only restore ZIPs
  you trust.
- Installers and programs are not code-signed yet. Compare the SHA-256 file published with each release.
- Backups are not encrypted. Keep them somewhere only you can reach.
