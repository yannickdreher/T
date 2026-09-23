# T - SSH Terminal Client

[![Build](https://github.com/yannickdreher/T/actions/workflows/build.yml/badge.svg)](https://github.com/yannickdreher/T/actions/workflows/build.yml)

A modern, cross-platform SSH terminal client built with Avalonia UI.

## Features

- 🖥️ Terminal emulator (VT100/xterm-256color)
- 📁 SFTP file browser with Auto-upload on file save
- 🔐 Multiple authentication methods (password, keyboard-interactive incl. 2FA prompts, private keys incl. OpenSSH certificates and PuTTY `.ppk`, default keys from `~/.ssh`)
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