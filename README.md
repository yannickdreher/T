# T - SSH Terminal Client

[![Build](https://github.com/yannickdreher/T/actions/workflows/build.yml/badge.svg)](https://github.com/yannickdreher/T/actions/workflows/build.yml)

A modern, cross-platform SSH terminal client built with Avalonia UI.

## Features

- 🖥️ Terminal emulator (VT100/xterm-256color)
- 📁 SFTP file browser with Auto-upload on file save
- 🔐 Multiple authentication methods (password, keyboard-interactive incl. 2FA prompts, private keys incl. OpenSSH certificates and PuTTY `.ppk`, default keys from `~/.ssh`)
- 🎫 Short-lived SSH certificates from a [step-ca](https://smallstep.com/docs/step-ca/), requested with the `step` CLI when needed (e.g. OIDC browser login), per session
- 🔀 Jump hosts (ProxyJump), also chained over several hops
- 🛡️ Host key verification against the OpenSSH `~/.ssh/known_hosts` (hashed entries, wildcards, warning on changed keys)
- ♻️ Automatic reconnect after connection loss
- 📂 SSH Session management with folder organization

## Data and security

- Settings, sessions (`T.db`) and the encryption key live in `%APPDATA%\T` (Windows) or `~/.config/T`.
  Set the environment variable `T_DATA_DIR` to use a different folder (portable use, testing).
- Stored passwords and key passphrases are encrypted with AES-256-GCM. The random master key
  (`master.key`) is protected with DPAPI on Windows and readable only by the user on Linux/macOS.
- Secrets written by older versions are migrated automatically on first start. Older versions
  of T cannot read the migrated values anymore (you would have to re-enter the passwords after a downgrade).

## step-ca certificates

Certificate authorities are set up once under *Settings → step-ca*; each session then selects
the CA it should use (*Session → SSH certificate from step-ca*). Empty profile fields use the
configuration of the `step` CLI (`step ca bootstrap`).

- T generates a new ECDSA P-256 key pair for every certificate and has `step ssh certificate --sign`
  sign only the public key, in a directory only you can access. The private key never reaches `step`,
  the ssh-agent or the disk in plain text; it is stored encrypted like the passwords and bound to its
  profile (`step/<profile>.json` in the data folder, a folder only you can access).
- The certificate is checked before use: it must certify exactly the generated key, be a user
  certificate and be valid now. A certificate issued for other profile settings is discarded, and
  certificates of removed profiles or expired ones are deleted.
- `step` is started without a shell from a local absolute path: the configured one, or found in PATH
  (on Windows also the user/machine PATH from the registry and the WinGet, Scoop and Chocolatey folders),
  never the current directory or a network share; on Windows only `step.exe`. Profile values are validated
  against allow-lists, passed as `--option=value`, and positional arguments follow `--`.
- T refuses to run `step` when other users could change it, its folder, the CA root certificate or the
  data folder with `settings.json` (e.g. a shared `T_DATA_DIR`). The certificate store must not be a link.
- If `%APPDATA%` is redirected to a network share, the public key and the certificate are exchanged
  with `step` on that share; use SMB signing/encryption there (the settings are stored there as well).
- `step` gets no input, so it cannot ask questions: use an OIDC provisioner (browser login) or
  select a provisioner that needs no password. Its output is sanitized before it is shown.
- A certificate is renewed only when you connect. Automatic reconnects and SFTP connections use it
  while it is valid and never start a login; several tabs share one login.

## Development

```
dotnet build T.slnx
dotnet test --project T.Tests/T.Tests.csproj
```

## License

This project is licensed under the **GNU General Public License v3.0** - see the [LICENSE](LICENSE) file for details.

### Third-Party Licenses

This project uses several open-source libraries. See [NOTICES](NOTICES.md) for complete license information.

## Contributing

Contributions are welcome! By contributing to this project, you agree that your contributions will be licensed under the GPLv3 license.