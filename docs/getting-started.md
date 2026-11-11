# Get started

## Requirements

The .NET 10 SDK. The front end builds on Windows; the core library and its tests also run on Linux and macOS.

## Layout

- `WindowsApp/Skerry.Core` holds the logic: SSH sessions, SFTP, host store, known hosts, encrypted sync.
- `WindowsApp/Skerry.Core.Tests` are its tests.
- `WindowsApp` is the WinUI front end (terminal rendering uses xterm.js, bundled under `Assets/xterm`).

## Build and test

```sh
dotnet test WindowsApp/Skerry.Core.Tests
```

The SSH tests start a real `sshd`, so OpenSSH server must be installed. To use an existing server, set `SKERRY_TEST_SSH_KEY` to a private key it accepts.
