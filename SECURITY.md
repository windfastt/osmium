# Security policy

## Versions that get fixes

Osmium is provided as is and comes with no support. If a security problem is fixed, the fix goes into the next
release; older releases are not updated. There is no commitment to fix a report or to a time frame. Reports about
unreleased code on `main` are welcome too.

## Reporting a vulnerability

Report security problems privately, through GitHub's private vulnerability reporting:

1. Open the **Security** tab of this repository.
2. Choose **Report a vulnerability**. The form is also at
   <https://github.com/windfastt/osmium/security/advisories/new>.
3. Fill it in and submit it.

The report is private on GitHub: it is visible to you, the repository's maintainers and anyone they add to the
advisory. Please do not open a public issue, pull request or discussion about a vulnerability, and do not publish
details before a release that fixes it is available.

There is no email address for reports, no bug bounty and no fixed response time.

## What to include

- The Osmium version (`osmium version`, or the name of the file you downloaded) or the commit, and the Windows version
  and architecture (x64 or ARM64).
- The affected part (see the scope below) and the steps to reproduce the problem.
- What an attacker needs (for example: the victim opens a crafted archive, a PC on the same network, a local user
  account) and what they can achieve.
- A proof of concept if you have one, such as a small crafted `.osa` file or a script. Do not send private files, or
  passwords and cluster keys that you actually use.
- Whether the problem is already publicly known, and whether and how you would like to be credited.

## Scope

In scope:

- **Archive parsing and extraction.** A crafted `.osa` file that crashes or hangs Osmium, exhausts memory or disk
  space despite its checks, writes outside the destination folder (absolute, drive-letter or UNC paths, `..`,
  alternate data streams, reserved device names), replaces files it should not, drops the Mark of the Web, or gets
  damaged data past verification.
- **Encryption.** Weaknesses in the password-based encryption (AES-256-GCM, Argon2id, PBKDF2-HMAC-SHA512, HKDF), leaks of what
  it is meant to hide (file and folder names, sizes, times, content), or changes to an encrypted archive that go
  undetected.
- **The cluster channel.** Getting past the key check, recovering the cluster key, reading or changing traffic,
  replaying or reordering frames, spoofing discovery, a worker learning more than the samples, blocks and pipelines
  it is sent, or a wrong block from a worker that is accepted.
- **The installer and uninstaller.** Privilege escalation, unsafe file, folder or registry permissions, the firewall
  rule, the PATH change and the Explorer registration.
- **Local integration.** The named pipe between Explorer and the app, and how settings and the cluster key are stored.

Documented behavior, not a vulnerability:

- The checksums of unencrypted archives (CRC-32, XXH3, XXH128) detect damage, not deliberate changes. Anyone who can
  write an unencrypted archive can change it.
- Without the password, an encrypted archive shows: that it is an Osmium archive, its container version, its archive
  id and flags, the key-derivation settings and salt, the number of blocks and the size of each block, the length of
  the file list (padded, so only roughly), and the archive's total size. File names, file sizes, times, methods, the
  creator and the contents are encrypted.
- Workers see the content of the samples and blocks they compress, and anyone who has the cluster key is trusted.
  PC names, core counts and memory sizes are sent unencrypted during the handshake. A helper sends its own to any PC
  on the network that presents the cluster's identifier (visible in discovery broadcasts), before that PC has proven
  it holds the key.
- Trial results from workers are spot-checked (about 5 %, none for Deflate or Brotli); a dishonest worker can make
  the planner choose a worse pipeline, but every block it returns is decoded and checked.
- The cluster key is stored in plain text in `settings.json`, in the user's profile or in the `data` folder of a
  portable copy.
- The builds are not code-signed yet, so Windows SmartScreen may warn.
- Problems in third-party components belong with their maintainers (see
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)); please tell us if Osmium is affected.

**Memory.** While Osmium checks a password, the key and the memory the key derivation works in can be written
to the Windows pagefile or hibernation file. Osmium wipes them as soon as it is done, but it cannot wipe
password text held by the .NET runtime (strings cannot be wiped) or what the system has already written out.
If that matters, turn off the hibernation file and clear the pagefile at shutdown.

**Genuine builds.** Osmium refuses encrypted archives that are not protected as it protects them (see
[docs/FORMAT.md](docs/FORMAT.md), section 5). A reader can check all of this, but it cannot prove that a writer did
not leak the password through fields that must look random (salt, archive id). The defence is using genuine builds,
which can be checked with the release's provenance attestation (`gh attestation verify <file> --repo
windfastt/osmium`) and, once releases are signed, the code signature.

## After you report

If a report is confirmed and fixed, the plan is to publish a GitHub security advisory with the release that contains
the fix, crediting you if you wish. Until then the report stays private on GitHub. There is no commitment to a fix or
a timeline.
