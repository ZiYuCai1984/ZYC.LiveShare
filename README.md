# ZYC.LiveShare

A .NET command-line client for joining Visual Studio Live Share sessions and forwarding the host's shared TCP servers to your local machine.

## Features

- Join a session using a Live Share link or workspace ID.
- Connect anonymously or authenticate with a Live Share token or GitHub OAuth token.
- Use automatic, direct, or relay connections.
- Forward shared ports with optional custom local port mappings.

## Getting started

Requires the .NET 10 SDK and an active Live Share session with shared servers.

Build the client from the repository root:

```sh
dotnet build ZYC.LiveShare
```

Join a session (replace `<join-link>` with your invitation link):

```sh
dotnet run --project ZYC.LiveShare -- join "<join-link>" --name "Guest"
```

Anonymous access requires the host to allow anonymous guests. To authenticate, set `LIVESHARE_TOKEN` to an existing Live Share token or `LIVESHARE_GITHUB_TOKEN` to a GitHub OAuth token.

Map a shared host port to a different local port:

```sh
dotnet run --project ZYC.LiveShare -- join "<join-link>" --map 3000:8080
```

Local listeners bind to `127.0.0.1` by default. Press `Ctrl+C` to leave the session.

Show all command-line options:

```sh
dotnet run --project ZYC.LiveShare -- --help
```

## Tests

```sh
dotnet test ZYC.LiveShare.Tests
```

## License

Licensed under the [MIT License](LICENSE).
