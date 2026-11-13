# Get started

## Requirements

The .NET 10 SDK. The front end builds on Windows; the core library and its tests also run on Linux and macOS.

## Layout

- `WindowsApp/Skerry.Core` holds the logic: SSH sessions, SFTP, host store, known hosts, encrypted sync.
- `WindowsApp/Skerry.Core.Tests` are its tests.
- `WindowsApp` is the WinUI front end (terminal rendering uses xterm.js, bundled under `Assets/xterm`).

## Build and test

```sh
dotnet test WindowsApp/Skerry.Core.Tests/Skerry.Core.Tests.csproj
```

<!-- coderabbit-ai-generated:v1 -->

<!-- coderabbit-ai-generated-section:v1:4429d3814d62752313b0f63f6016a5f8de650e52d9b6c70ed14fa7d6458ef719 -->

The standalone SSH and SFTP tests start a real OpenSSH server using `/usr/sbin/sshd` and require `ssh-keygen`. Run these tests in Linux; on Windows, use WSL with OpenSSH server installed in the WSL environment. Installing Windows OpenSSH Server alone does not enable these tests when run natively on Windows.

To also run the tests against an existing server, set `SKERRY_TEST_SSH_KEY` to the path of a private key accepted by that server. The server must be reachable at `127.0.0.1:22` and accept the key for the username in `USER` (or the current user if `USER` is unset). These tests do not run when `SKERRY_TEST_SSH_KEY` is unset; setting it does not replace the standalone server tests.

<!-- /coderabbit-ai-generated:v1 -->
